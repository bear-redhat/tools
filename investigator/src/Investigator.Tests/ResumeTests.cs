using System.Text.Json;
using Investigator.Models;
using Investigator.Services;

namespace Investigator.Tests;

/// <summary>
/// Resume rebuilds an agent's LLM context from the persisted event log. If it drops,
/// duplicates or misorders anything, the resumed agent either forgets the case or is fed
/// a malformed conversation the API rejects.
/// </summary>
public class ResumeTests
{
    private static readonly DateTimeOffset T = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Lead = "little-bear";

    private static RoomEvent.ExternalInput UserSays(string text, string to = Lead) =>
        new(0, "user", T, text) { To = to };

    /// <summary>What AgentRunner stores when it drains an inbox message.</summary>
    private static RoomEvent.LlmContext InboxBatch(string text, string agent = Lead) =>
        new(0, agent, T,
            [new LlmInboxMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(text),
                SourceFrom = "user",
                SourceTo = agent,
            }],
            IsInboxBatch: true);

    private static RoomEvent.LlmContext AssistantToolUse(string agent, string toolUseId, string tool) =>
        new(0, agent, T,
        [
            new LlmMessage
            {
                Role = "assistant",
                Content = JsonSerializer.SerializeToElement(new object[]
                {
                    new { type = "tool_use", id = toolUseId, name = tool, input = new { command = "get pods" } },
                }),
            },
        ]);

    private static RoomEvent.LlmContext ToolResult(string agent, string toolUseId, string content) =>
        new(0, agent, T,
        [
            new LlmMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(new object[]
                {
                    new { type = "tool_result", tool_use_id = toolUseId, content },
                }),
            },
        ]);

    private static string TextOf(LlmMessage m) =>
        m.Content.ValueKind == JsonValueKind.String ? m.Content.GetString() ?? "" : m.Content.GetRawText();

    [Fact]
    public void Replay_DoesNotDuplicateAUserMessage()
    {
        // PostUserMessageAsync writes an ExternalInput; the projector turns it into an
        // inbox TextMessage, and AgentRunner then stores it again inside an inbox-batch
        // LlmContext. Both live in the durable log, so a naive replay counts it twice.
        var events = new List<RoomEvent>
        {
            UserSays("why did e2e-aws fail?"),
            InboxBatch("why did e2e-aws fail?"),
        };

        var replayed = LlmContextApplier.Replay(events, Lead);

        var occurrences = replayed.Count(m => TextOf(m).Contains("why did e2e-aws fail?"));
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Replay_KeepsAMessageThatArrivedButWasNeverDrained()
    {
        // The pod died between PostUserMessageAsync and the agent reading its inbox, so the
        // only trace is the ExternalInput. Losing it would silently drop what was said.
        var events = new List<RoomEvent> { UserSays("actually check build02 instead") };

        var replayed = LlmContextApplier.Replay(events, Lead);

        var single = Assert.Single(replayed);
        Assert.Equal("actually check build02 instead", TextOf(single));
    }

    [Fact]
    public void Replay_AppendsAnUndrainedMessageAfterTheDeliveredOnes()
    {
        var events = new List<RoomEvent>
        {
            UserSays("first"),
            InboxBatch("first"),
            UserSays("second"),   // never drained
        };

        var replayed = LlmContextApplier.Replay(events, Lead);

        Assert.Equal(2, replayed.Count);
        Assert.Equal("second", TextOf(replayed[^1]));
    }

    [Fact]
    public void Replay_DoesNotDuplicateAScoutInstruction()
    {
        // Recall and stand-down are ExternalInputs from the lead, rendered into the scout's
        // context with a "[little-bear]: " prefix, so matching cannot be plain equality.
        const string scout = "sharp-badger";
        var events = new List<RoomEvent>
        {
            new RoomEvent.ExternalInput(0, Lead, T, "Return to the sitting-room at once.") { To = scout },
            new RoomEvent.LlmContext(0, scout, T,
                [new LlmInboxMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("[little-bear]: Return to the sitting-room at once."),
                    SourceFrom = Lead,
                    SourceTo = scout,
                }],
                IsInboxBatch: true),
        };

        var replayed = LlmContextApplier.Replay(events, scout);

        Assert.Single(replayed);
    }

    [Fact]
    public void Replay_KeepsMessagesInOrder()
    {
        var events = new List<RoomEvent>
        {
            UserSays("first"),
            InboxBatch("first"),
            AssistantToolUse(Lead, "toolu_1", "run_oc"),
            ToolResult(Lead, "toolu_1", "pods"),
        };

        var replayed = LlmContextApplier.Replay(events, Lead);

        Assert.Equal("assistant", replayed[^2].Role);
        Assert.Equal("user", replayed[^1].Role);
        Assert.Contains("tool_result", TextOf(replayed[^1]));
    }

    [Fact]
    public void Replay_IgnoresOtherAgentsContext()
    {
        var events = new List<RoomEvent>
        {
            InboxBatch("lead work"),
            AssistantToolUse("sharp-badger", "toolu_s", "run_oc"),
            ToolResult("sharp-badger", "toolu_s", "scout output"),
        };

        var replayed = LlmContextApplier.Replay(events, Lead);

        Assert.DoesNotContain(replayed, m => TextOf(m).Contains("scout output"));
    }

    [Fact]
    public void Replay_AppliesCompactionRemovals()
    {
        var events = new List<RoomEvent>
        {
            InboxBatch("one"),
            InboxBatch("two"),
            new RoomEvent.LlmContext(0, Lead, T,
                [new LlmMessage { Role = "user", Content = JsonSerializer.SerializeToElement("[compacted summary]") }],
                Removed: 2),
        };

        var replayed = LlmContextApplier.Replay(events, Lead);

        var single = Assert.Single(replayed);
        Assert.Contains("compacted summary", TextOf(single));
    }

    // -- dangling tool calls ----------------------------------------------------

    [Fact]
    public void CrashMidTool_GetsAnAbortResult_SoTheContextIsWellFormed()
    {
        var events = new List<RoomEvent>
        {
            InboxBatch("investigate"),
            AssistantToolUse(Lead, "toolu_1", "run_oc"),
            // pod died here -- no tool_result was ever stored
        };

        var closed = EventLogScanner.CloseDanglingToolCalls(events);
        var replayed = LlmContextApplier.Replay(closed, Lead);

        // Every tool_use must be answered by a tool_result in the next message, or the
        // API rejects the whole conversation.
        Assert.Equal("assistant", replayed[^2].Role);
        Assert.Contains("toolu_1", TextOf(replayed[^2]));
        Assert.Equal("user", replayed[^1].Role);
        Assert.Contains("toolu_1", TextOf(replayed[^1]));
        Assert.Contains("aborted", TextOf(replayed[^1]));
    }

    [Fact]
    public void ClosingDanglingCalls_IsIdempotent()
    {
        var events = new List<RoomEvent>
        {
            AssistantToolUse(Lead, "toolu_1", "run_oc"),
        };

        var once = EventLogScanner.CloseDanglingToolCalls(events);
        var twice = EventLogScanner.CloseDanglingToolCalls(once);

        Assert.Equal(once.Count, twice.Count);
    }

    [Fact]
    public void CompletedToolCalls_AreNotAborted()
    {
        var events = new List<RoomEvent>
        {
            AssistantToolUse(Lead, "toolu_1", "run_oc"),
            ToolResult(Lead, "toolu_1", "pods listed"),
        };

        var closed = EventLogScanner.CloseDanglingToolCalls(events);

        Assert.Equal(events.Count, closed.Count);
        Assert.DoesNotContain(closed, e =>
            e is RoomEvent.LlmContext c && c.Messages.Any(m => TextOf(m).Contains("aborted")));
    }

    [Fact]
    public void MultipleToolsInOneTurn_AllGetAborted()
    {
        var assistant = new RoomEvent.LlmContext(0, Lead, T,
        [
            new LlmMessage
            {
                Role = "assistant",
                Content = JsonSerializer.SerializeToElement(new object[]
                {
                    new { type = "tool_use", id = "toolu_1", name = "run_oc", input = new { } },
                    new { type = "tool_use", id = "toolu_2", name = "run_aws", input = new { } },
                }),
            },
        ]);

        var closed = EventLogScanner.CloseDanglingToolCalls([assistant]);
        var replayed = LlmContextApplier.Replay(closed, Lead);

        var results = TextOf(replayed[^1]);
        Assert.Contains("toolu_1", results);
        Assert.Contains("toolu_2", results);
    }

    [Fact]
    public void EventsBeforeAPriorSessionEnd_AreNotReAborted()
    {
        var events = new List<RoomEvent>
        {
            AssistantToolUse(Lead, "toolu_old", "run_oc"),
            new RoomEvent.SessionEnded(0, "system", T),
            InboxBatch("new run"),
        };

        var closed = EventLogScanner.CloseDanglingToolCalls(events);

        Assert.Equal(events.Count, closed.Count);
    }
}
