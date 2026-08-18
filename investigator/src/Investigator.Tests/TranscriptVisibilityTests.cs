using System.Text.Json;
using Investigator.Models;
using Investigator.Services;

namespace Investigator.Tests;

/// <summary>
/// End-to-end check of the visibility chain that makes a remote client able to follow an
/// investigation step by step:
///
///   AgentRunner tool metadata -> LlmContext -> TranscriptProjector -> RoomEventPipeline
///   -> ProjectedEventLog
///
/// Every link had to work for a tool call to be inspectable after the fact.
/// </summary>
public class TranscriptVisibilityTests
{
    private static readonly DateTimeOffset T = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed record Chain(TranscriptProjector Projector, ProjectedEventLog Log);

    private static Chain BuildChain(string leadId = "little-bear")
    {
        var log = new ProjectedEventLog();
        var pipeline = new RoomEventPipeline(new RoomEventBus(), [], startSeq: 0, log: log);
        return new Chain(new TranscriptProjector(leadId, e => pipeline.EmitAsync(e)), log);
    }

    private static LlmMessage Assistant(params object[] blocks) => new()
    {
        Role = "assistant",
        Content = JsonSerializer.SerializeToElement(blocks),
    };

    private static LlmToolResultMessage ToolResults(
        IEnumerable<object> blocks, params ToolCallMeta[] meta) => new()
    {
        Role = "user",
        Content = JsonSerializer.SerializeToElement(blocks.ToArray()),
        ToolMeta = meta.ToList(),
    };

    [Fact]
    public async Task ToolCallAndResult_BecomeInspectableEvents_LinkedByRequestSeq()
    {
        var chain = BuildChain();

        var ctx = new RoomEvent.LlmContext(0, "little-bear", T,
        [
            Assistant(new
            {
                type = "tool_use",
                id = "toolu_1",
                name = "run_oc",
                input = new { command = "get pods -n ci" },
            }),
            ToolResults(
                [new { type = "tool_result", tool_use_id = "toolu_1", content = "NAME READY\nfoo 1/1" }],
                new ToolCallMeta
                {
                    ToolUseId = "toolu_1",
                    Summary = "12 pods, 2 CrashLoopBackOff",
                    ExitCode = 0,
                    OutputFile = "tool_outputs/001-run_oc.txt",
                }),
        ]);

        await chain.Projector.ReplayAsync([ctx]);

        var entries = chain.Log.Read(0, 50, 100_000).Entries;

        var call = Assert.Single(entries, e => e.Kind == "tool_call");
        Assert.Equal("run_oc", call.Tool);
        Assert.Equal("oc get pods -n ci", call.Command);

        var result = Assert.Single(entries, e => e.Kind == "tool_result");
        Assert.Equal("run_oc", result.Tool);
        Assert.Equal("12 pods, 2 CrashLoopBackOff", result.Summary);
        Assert.Equal("tool_outputs/001-run_oc.txt", result.OutputFile);
        Assert.Contains("CrashLoopBackOff", result.Summary);

        // The pairing is what lets a client show a call next to its own result.
        Assert.Equal(call.Seq, result.RequestSeq);
    }

    [Fact]
    public async Task ToolResultWithoutMetadata_StillAppears_ButCarriesNoExitCodeDetail()
    {
        // Guards the regression this whole change was about: before tool metadata was
        // attached on the ordinary path, every non-terminal result looked like this.
        var chain = BuildChain();

        var ctx = new RoomEvent.LlmContext(0, "little-bear", T,
        [
            Assistant(new { type = "tool_use", id = "toolu_1", name = "run_oc", input = new { command = "get nodes" } }),
            new LlmMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(
                    new object[] { new { type = "tool_result", tool_use_id = "toolu_1", content = "node list" } }),
            },
        ]);

        await chain.Projector.ReplayAsync([ctx]);

        var result = Assert.Single(chain.Log.Read(0, 50, 100_000).Entries, e => e.Kind == "tool_result");
        Assert.Null(result.Summary);
        Assert.Null(result.OutputFile);
    }

    [Fact]
    public async Task MultipleToolsInOneTurn_EachGetTheirOwnLinkedPair()
    {
        var chain = BuildChain();

        var ctx = new RoomEvent.LlmContext(0, "little-bear", T,
        [
            Assistant(
                new { type = "tool_use", id = "toolu_1", name = "run_oc", input = new { command = "get pods" } },
                new { type = "tool_use", id = "toolu_2", name = "run_aws", input = new { command = "sts get-caller-identity" } }),
            ToolResults(
                [
                    new { type = "tool_result", tool_use_id = "toolu_1", content = "pods" },
                    new { type = "tool_result", tool_use_id = "toolu_2", content = "denied" },
                ],
                new ToolCallMeta { ToolUseId = "toolu_1", ExitCode = 0, Summary = "ok" },
                new ToolCallMeta { ToolUseId = "toolu_2", ExitCode = 1, Summary = "access denied" }),
        ]);

        await chain.Projector.ReplayAsync([ctx]);

        var entries = chain.Log.Read(0, 50, 100_000).Entries;
        var calls = entries.Where(e => e.Kind == "tool_call").ToList();
        var results = entries.Where(e => e.Kind == "tool_result").ToList();

        Assert.Equal(2, calls.Count);
        Assert.Equal(2, results.Count);

        var ocCall = calls.Single(c => c.Tool == "run_oc");
        var awsCall = calls.Single(c => c.Tool == "run_aws");

        Assert.Equal(0, results.Single(r => r.RequestSeq == ocCall.Seq).ExitCode);
        Assert.Equal(1, results.Single(r => r.RequestSeq == awsCall.Seq).ExitCode);
        Assert.Equal("access denied", results.Single(r => r.RequestSeq == awsCall.Seq).Summary);
    }

    [Fact]
    public async Task ExternalInput_AppearsAsAddressedText()
    {
        var chain = BuildChain();

        await chain.Projector.ReplayAsync(
        [
            new RoomEvent.ExternalInput(0, "user", T, "focus on build01") { To = "little-bear" },
        ]);

        var entry = Assert.Single(chain.Log.Read(0, 50, 100_000).Entries, e => e.Kind == "text");
        Assert.Equal("user", entry.From);
        Assert.Equal("little-bear", entry.To);
        Assert.Equal("focus on build01", entry.Text);
    }

    [Fact]
    public async Task LeadAskingTheHuman_SurfacesAsAPendingQuestion_ThenClearsOnReply()
    {
        // The full path for "the investigator has been waiting on you": the lead emits a
        // message tool call addressed to the user, which ends its turn and parks it.
        var chain = BuildChain();

        await chain.Projector.ReplayAsync(
        [
            new RoomEvent.LlmContext(0, "little-bear", T,
            [
                Assistant(new
                {
                    type = "tool_use",
                    id = "toolu_m",
                    name = "message",
                    input = new { to = "user", text = "Which cluster should I focus on?" },
                }),
            ]),
        ]);

        var pending = chain.Log.FindPendingUserRequest();
        Assert.NotNull(pending);
        Assert.Contains("Which cluster should I focus on?", pending!.Text);

        // The human answers; PostUserMessageAsync lands as an ExternalInput.
        await chain.Projector.ReplayAsync(
        [
            new RoomEvent.ExternalInput(0, "user", T, "build01") { To = "little-bear" },
        ]);

        Assert.Null(chain.Log.FindPendingUserRequest());
    }

    [Fact]
    public async Task ReplayIsDeterministic_SoACursorSurvivesARestart()
    {
        var ctx = new RoomEvent.LlmContext(0, "little-bear", T,
        [
            Assistant(new { type = "tool_use", id = "toolu_1", name = "run_oc", input = new { command = "get pods" } }),
            ToolResults([new { type = "tool_result", tool_use_id = "toolu_1", content = "pods" }],
                new ToolCallMeta { ToolUseId = "toolu_1", ExitCode = 0 }),
        ]);

        var first = BuildChain();
        await first.Projector.ReplayAsync([ctx]);

        var second = BuildChain();
        await second.Projector.ReplayAsync([ctx]);

        Assert.Equal(
            first.Log.Read(0, 50, 100_000).Entries.Select(e => (e.Seq, e.Kind, e.Tool)),
            second.Log.Read(0, 50, 100_000).Entries.Select(e => (e.Seq, e.Kind, e.Tool)));
    }
}
