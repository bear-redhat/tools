using System.Net;
using System.Text.Json;
using Investigator.Models;
using Investigator.Services;

namespace Investigator.Tests;

/// <summary>
/// Characterization tests for <see cref="AgentRunner"/>. These pin behaviour that was
/// earned through production incidents -- truncation recovery, the HTTP 400 compaction
/// ladder, text-only synthesis, and inbox-drain ordering -- so the event-plane refactor
/// cannot quietly regress them.
/// </summary>
public class AgentRunnerTests
{
    private static AgentRunnerHarness Harness() => new();

    [Fact]
    public async Task TerminalTool_Concludes_AndAttachesToolMeta()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.Text("all done"),
                      AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.SetToolResult("conclude", new AgentRunner.ToolExecutionResult(
            "concluded", OutputFile: "tool_outputs/001-conclude.txt", Summary: "root cause found"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        Assert.Single(h.ToolCalls);
        Assert.Equal("conclude", h.ToolCalls[0].Name);

        var last = Assert.IsType<LlmToolResultMessage>(h.Messages[^1]);
        var meta = Assert.Single(last.ToolMeta);
        Assert.Equal("toolu_c", meta.ToolUseId);
        Assert.Equal("root cause found", meta.Summary);
        Assert.Equal("tool_outputs/001-conclude.txt", meta.OutputFile);
    }

    [Fact]
    public async Task TerminalTool_WithoutSummary_StillCarriesExitCodeMetadata()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.SetToolResult("conclude", new AgentRunner.ToolExecutionResult("boom", ExitCode: 3, TimedOut: true));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        var last = Assert.IsType<LlmToolResultMessage>(h.Messages[^1]);
        var meta = Assert.Single(last.ToolMeta);
        Assert.Null(meta.Summary);
        Assert.Equal(3, meta.ExitCode);
        Assert.True(meta.TimedOut);
    }

    [Fact]
    public async Task OrdinaryToolCalls_AttachMetadataForEveryCall()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.ToolUse("run_oc", "toolu_1"),
                      AgentRunnerHarness.ToolUse("run_aws", "toolu_2"))
             .Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.SetToolResult("run_oc", new AgentRunner.ToolExecutionResult(
            "pods listed", OutputFile: "tool_outputs/001-run_oc.txt", Summary: "12 pods, 2 CrashLoopBackOff"));
        h.SetToolResult("run_aws", new AgentRunner.ToolExecutionResult("denied", ExitCode: 1));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        // Both calls land in one tool_result turn, each with its own metadata.
        var resultTurn = h.Messages.OfType<LlmToolResultMessage>()
            .First(m => m.ToolMeta.Any(x => x.ToolUseId == "toolu_1"));
        Assert.Equal(2, resultTurn.ToolMeta.Count);

        var oc = resultTurn.ToolMeta.Single(m => m.ToolUseId == "toolu_1");
        Assert.Equal("12 pods, 2 CrashLoopBackOff", oc.Summary);
        Assert.Equal("tool_outputs/001-run_oc.txt", oc.OutputFile);
        Assert.Equal(0, oc.ExitCode);

        var aws = resultTurn.ToolMeta.Single(m => m.ToolUseId == "toolu_2");
        Assert.Equal(1, aws.ExitCode);
        Assert.Null(aws.OutputFile);
    }

    [Fact]
    public async Task OrdinaryToolCall_ExecutesThenContinuesToNextTurn()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.ToolUse("run_oc", "toolu_1", """{"command":"get pods"}"""))
             .Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        Assert.Equal(["run_oc", "conclude"], h.ToolCalls.Select(t => t.Name));
        Assert.Equal(2, h.Llm.CallCount);
    }

    [Fact]
    public async Task TextOnly_NudgesFirst_ThenSynthesizesMessageToolCall()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.Text("just chatting"))
             .Returns(AgentRunnerHarness.Text("still just chatting"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        // First text-only turn nudges rather than giving up.
        Assert.Contains(h.StoredMessages(),
            m => AgentRunnerHarness.ContentText(m).Contains("must use a tool call"));

        // Second one synthesizes a message tool call so the text is not lost.
        var call = Assert.Single(h.ToolCalls);
        Assert.Equal("message", call.Name);
        Assert.Equal("user", call.Input.GetProperty("to").GetString());
        Assert.Equal("still just chatting", call.Input.GetProperty("text").GetString());
        Assert.StartsWith("toolu_synth_", call.StepId);
    }

    [Fact]
    public async Task Truncation_WithNoSurvivingTools_RetriesWithReducedThinkingBudget()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.Text("partial thought"),
                      AgentRunnerHarness.TruncatedToolUse("run_oc", "toolu_cut"))
             .Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        // The truncated call is never executed; the model is asked to re-emit it.
        Assert.Equal(["conclude"], h.ToolCalls.Select(t => t.Name));
        Assert.Contains(h.StoredMessages(),
            m => AgentRunnerHarness.ContentText(m).Contains("cut off before the tool call"));

        // Retry runs with a reduced thinking budget: max(1024, ThinkingBudget / 4).
        Assert.Equal([null, 2500], h.Llm.ThinkingBudgets);
    }

    [Fact]
    public async Task Truncation_WithSurvivingTools_ExecutesSurvivorsAndPlaceholdersTheRest()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.ToolUse("run_oc", "toolu_ok"),
                      AgentRunnerHarness.TruncatedToolUse("run_aws", "toolu_cut"))
             .Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        // The surviving tool runs; the truncated one does not.
        Assert.Equal(["run_oc", "conclude"], h.ToolCalls.Select(t => t.Name));

        var resultsMsg = h.Messages.First(m =>
            m.Role == "user" &&
            m.Content.ValueKind == JsonValueKind.Array &&
            m.Content.GetRawText().Contains("tool_result"));
        Assert.Contains("cut off before the input was complete",
            AgentRunnerHarness.ContentText(resultsMsg));
    }

    [Fact]
    public async Task HttpBadRequest_ExhaustsCompactionLadder_ThenForcesConclude()
    {
        var h = Harness();
        for (var i = 0; i < 4; i++)
            h.Llm.Throws(new HttpRequestException("prompt too long", null, HttpStatusCode.BadRequest));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        // Three compaction attempts, then a forced conclude rather than a lost turn.
        Assert.Equal(4, h.Llm.CallCount);
        var call = Assert.Single(h.ToolCalls);
        Assert.Equal("conclude", call.Name);
        Assert.Contains("context window exhausted", call.Input.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task InboxMessageDuringToolExecution_LandsAfterToolResult()
    {
        var h = Harness();
        h.Llm.Returns(AgentRunnerHarness.ToolUse("run_oc", "toolu_1"))
             .Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.OnToolCall = (name, _, _) =>
        {
            if (name == "run_oc")
            {
                // Arrives mid-turn, while the tool is still running.
                h.Send("interject");
                h.CloseInbox();
            }
            return Task.CompletedTask;
        };
        h.Send("go");

        await h.RunAsync();

        var interjectIndex = h.Messages.FindIndex(m =>
            AgentRunnerHarness.ContentText(m) == "interject");
        Assert.True(interjectIndex > 0, "interjected message was dropped");

        // It must land after the tool_result array, never between tool_use and tool_result.
        var previous = h.Messages[interjectIndex - 1];
        Assert.Equal(JsonValueKind.Array, previous.Content.ValueKind);
        Assert.Contains("tool_result", previous.Content.GetRawText());
    }

    [Fact]
    public async Task MaxToolCalls_InjectsForcedConclusionPrompt()
    {
        var h = Harness();
        h.MaxToolCalls = 1;
        h.Llm.Returns(AgentRunnerHarness.ToolUse("run_oc", "toolu_1"))
             .Returns(AgentRunnerHarness.ToolUse("conclude", "toolu_c"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        Assert.Contains(h.StoredMessages(),
            m => AgentRunnerHarness.ContentText(m).Contains("used all 1 tool calls"));
    }

    [Fact]
    public async Task ConditionallyTerminalTool_EndsTheTurn()
    {
        var h = Harness();
        h.IsConditionallyTerminal = (name, input) =>
            name == "message" && input.TryGetProperty("to", out var to) && to.GetString() == "user";
        h.Llm.Returns(AgentRunnerHarness.ToolUse("message", "toolu_m", """{"to":"user","text":"which cluster?"}"""));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        // The turn ends after the tool runs -- this is what parks the lead awaiting a human.
        Assert.Equal(1, h.Llm.CallCount);
        Assert.Equal(["message"], h.ToolCalls.Select(t => t.Name));
        Assert.Contains(h.Stored, s => s.IsConcludedBatch);
    }

    [Fact]
    public async Task ShouldSuppressNextTurn_RollsBackInboxBatchWithoutCallingModel()
    {
        var h = Harness();
        h.ShouldSuppressNextTurn = () => true;
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        Assert.Equal(0, h.Llm.CallCount);
        Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task LlmFailure_SurfacesSystemErrorMessage_AndEndsTurn()
    {
        var h = Harness();
        h.Llm.Throws(new IOException("stream died"));
        h.Send("go");
        h.CloseInbox();

        await h.RunAsync();

        Assert.Equal(1, h.Llm.CallCount);
        Assert.Contains("[system error]", AgentRunnerHarness.ContentText(h.Messages[^1]));
    }
}
