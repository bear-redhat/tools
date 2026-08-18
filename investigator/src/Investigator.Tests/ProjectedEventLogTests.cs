using System.Text.Json;
using Investigator.Models;

namespace Investigator.Tests;

public class ProjectedEventLogTests
{
    private static readonly DateTimeOffset T = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static RoomEvent.ToolRequest MessageCall(int seq, string to, string text, string from = "little-bear") =>
        new(seq, from, T, "message",
            JsonSerializer.SerializeToElement(new { to, text }),
            DisplayCommand: $"message {to}");

    private static RoomEvent.TextMessage Reply(int seq, string text) =>
        new(seq, "user", T, text) { To = "little-bear" };

    private static RoomEvent.ToolRequest Call(int seq, string tool = "run_oc", string? cmd = "oc get pods") =>
        new(seq, "little-bear", T, tool, default, DisplayCommand: cmd);

    private static RoomEvent.ToolResponse Result(int seq, int requestSeq, string output,
        int exitCode = 0, string? file = null, string? summary = null, string? caller = null) =>
        new(seq, "tool:run_oc", T, "run_oc", output, requestSeq,
            ExitCode: exitCode, OutputFile: file, Summary: summary) { To = caller };

    [Fact]
    public void ToolCall_RetainsCommandAndSequence()
    {
        var log = new ProjectedEventLog();
        log.Append(Call(5));

        var entry = Assert.Single(log.Read(0, 10, 10_000).Entries);
        Assert.Equal("tool_call", entry.Kind);
        Assert.Equal(5, entry.Seq);
        Assert.Equal("run_oc", entry.Tool);
        Assert.Equal("oc get pods", entry.Command);
    }

    [Fact]
    public void ToolResult_RetainsExitCodeSummaryAndOutputFileReference()
    {
        var log = new ProjectedEventLog();
        log.Append(Result(6, requestSeq: 5, "denied", exitCode: 1,
            file: "tool_outputs/001-run_oc.txt", summary: "forbidden"));

        var entry = Assert.Single(log.Read(0, 10, 10_000).Entries);
        Assert.Equal("tool_result", entry.Kind);
        Assert.Equal(1, entry.ExitCode);
        Assert.Equal("forbidden", entry.Summary);
        Assert.Equal("tool_outputs/001-run_oc.txt", entry.OutputFile);
        Assert.Equal(5, entry.RequestSeq);
    }

    [Fact]
    public void LongOutput_IsClippedAndFlagged_ButTheReferenceSurvives()
    {
        var log = new ProjectedEventLog(maxTextChars: 50);
        log.Append(Result(1, 0, new string('x', 5_000), file: "tool_outputs/big.txt"));

        var entry = Assert.Single(log.Read(0, 10, 100_000).Entries);
        Assert.True(entry.Clipped);
        Assert.Contains("clipped", entry.Text);
        Assert.True(entry.Text!.Length < 200);
        // The full body is still reachable on disk.
        Assert.Equal("tool_outputs/big.txt", entry.OutputFile);
    }

    [Fact]
    public void Read_PagesForwardWithTheCursor()
    {
        var log = new ProjectedEventLog();
        for (var i = 1; i <= 5; i++) log.Append(Call(i));

        var first = log.Read(0, maxEntries: 2, maxChars: 100_000);
        Assert.Equal([1, 2], first.Entries.Select(e => e.Seq));
        Assert.Equal(2, first.NextSeq);
        Assert.True(first.Truncated);

        var second = log.Read(first.NextSeq, maxEntries: 2, maxChars: 100_000);
        Assert.Equal([3, 4], second.Entries.Select(e => e.Seq));

        var third = log.Read(second.NextSeq, maxEntries: 10, maxChars: 100_000);
        Assert.Equal([5], third.Entries.Select(e => e.Seq));
        Assert.False(third.Truncated);
    }

    [Fact]
    public void Read_StopsOnCharacterBudget_ButAlwaysReturnsAtLeastOneEntry()
    {
        var log = new ProjectedEventLog();
        log.Append(Result(1, 0, new string('a', 1_500)));
        log.Append(Result(2, 0, new string('b', 1_500)));

        // A budget smaller than a single entry must still make progress, or a client
        // paging through a large transcript would loop forever on the same cursor.
        var page = log.Read(0, maxEntries: 10, maxChars: 10);
        Assert.Single(page.Entries);
        Assert.True(page.Truncated);
        Assert.Equal(1, page.NextSeq);
    }

    [Fact]
    public void Read_FiltersByKind()
    {
        var log = new ProjectedEventLog();
        log.Append(Call(1));
        log.Append(Result(2, 1, "ok"));
        log.Append(new RoomEvent.TextMessage(3, "little-bear", T, "thinking out loud"));

        var page = log.Read(0, 10, 100_000, kind: "tool_result");
        Assert.Single(page.Entries);
        Assert.Equal("tool_result", page.Entries[0].Kind);
    }

    [Fact]
    public void Read_FiltersByAgent()
    {
        var log = new ProjectedEventLog();
        log.Append(new RoomEvent.TextMessage(1, "little-bear", T, "lead speaking"));
        log.Append(new RoomEvent.TextMessage(2, "sharp-badger", T, "scout speaking"));

        var page = log.Read(0, 10, 100_000, agentId: "sharp-badger");
        Assert.Single(page.Entries);
        Assert.Equal("scout speaking", page.Entries[0].Text);
    }

    [Fact]
    public void AgentFilter_ExcludesAnotherAgentsToolResults()
    {
        // Regression: the filter used to accept every tool_result regardless of caller,
        // so filtering to one scout returned every scout's output -- the largest entries,
        // and precisely what the filter exists to keep out of a bounded response.
        var log = new ProjectedEventLog();
        log.Append(Result(1, 0, "badger output", caller: "sharp-badger"));
        log.Append(Result(2, 0, "owl output", caller: "keen-owl"));

        var page = log.Read(0, 10, 100_000, agentId: "sharp-badger");

        var entry = Assert.Single(page.Entries);
        Assert.Equal("badger output", entry.Text);
    }

    [Fact]
    public void FilteredCursor_AdvancesPastSkippedEntries()
    {
        // Deriving nextSeq from the returned page left filtered-out entries below the
        // cursor, so the same filtered read never made progress.
        var log = new ProjectedEventLog();
        log.Append(Call(1));
        log.Append(new RoomEvent.TextMessage(2, "little-bear", T, "chatter"));
        log.Append(new RoomEvent.TextMessage(3, "little-bear", T, "more chatter"));
        log.Append(Call(4));

        var first = log.Read(0, maxEntries: 1, maxChars: 100_000, kind: "tool_call");
        Assert.Equal([1], first.Entries.Select(e => e.Seq));

        var second = log.Read(first.NextSeq, maxEntries: 10, maxChars: 100_000, kind: "tool_call");
        Assert.Equal([4], second.Entries.Select(e => e.Seq));

        var third = log.Read(second.NextSeq, maxEntries: 10, maxChars: 100_000, kind: "tool_call");
        Assert.Empty(third.Entries);
        Assert.Equal(second.NextSeq, third.NextSeq);
    }

    [Fact]
    public void FilterMatchingNothing_ReachesTheEndInsteadOfLooping()
    {
        var log = new ProjectedEventLog();
        for (var i = 1; i <= 5; i++) log.Append(new RoomEvent.TextMessage(i, "little-bear", T, $"m{i}"));

        var page = log.Read(0, 10, 100_000, kind: "tool_call");

        Assert.Empty(page.Entries);
        Assert.Equal(5, page.NextSeq);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void BudgetBreak_LeavesTheCursorOnTheLastReturnedEntry()
    {
        var log = new ProjectedEventLog();
        log.Append(Result(1, 0, new string('a', 2_000)));
        log.Append(Result(2, 0, new string('b', 2_000)));
        log.Append(Result(3, 0, new string('c', 2_000)));

        var page = log.Read(0, maxEntries: 2, maxChars: 100_000);

        Assert.Equal([1, 2], page.Entries.Select(e => e.Seq));
        Assert.Equal(2, page.NextSeq);
        Assert.True(page.Truncated);
    }

    [Fact]
    public void HighestSeq_ReportsProgressEvenWhenThePageIsEmpty()
    {
        var log = new ProjectedEventLog();
        log.Append(Call(1));
        log.Append(Call(2));

        var page = log.Read(sinceSeq: 2, 10, 100_000);
        Assert.Empty(page.Entries);
        Assert.Equal(2, page.NextSeq);
        Assert.Equal(2, page.HighestSeq);
    }

    [Fact]
    public void Capacity_EvictsOldest_AndReportsAGapToStaleCursors()
    {
        var log = new ProjectedEventLog(capacity: 3);
        for (var i = 1; i <= 6; i++) log.Append(Call(i));

        Assert.Equal(3, log.Count);
        Assert.Equal(3, log.Dropped);

        // A client resuming from before the retained window is told it has a hole,
        // rather than silently receiving a transcript with events missing.
        Assert.True(log.Read(sinceSeq: 0, 10, 100_000).Gap);
        // A client already inside the window does not.
        Assert.False(log.Read(sinceSeq: 4, 10, 100_000).Gap);
    }

    [Fact]
    public void RawModelContext_IsNotRetained()
    {
        var log = new ProjectedEventLog();
        log.Append(new RoomEvent.LlmContext(1, "little-bear", T, []));

        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void ToolCall_RetainsItsArguments_NotJustTheDisplayOneLiner()
    {
        var log = new ProjectedEventLog();
        log.Append(MessageCall(1, "user", "which cluster should I focus on?"));

        var entry = Assert.Single(log.Read(0, 10, 100_000).Entries);
        Assert.Equal("message user", entry.Command);
        Assert.Contains("which cluster should I focus on?", entry.Text);
        Assert.Equal("user", entry.To);
    }

    // -- pending user request ---------------------------------------------------

    [Fact]
    public void MessageAddressedToTheUser_IsReportedAsPending()
    {
        var log = new ProjectedEventLog();
        log.Append(Call(1));
        log.Append(MessageCall(2, "user", "which cluster should I focus on?"));

        var pending = log.FindPendingUserRequest();

        Assert.NotNull(pending);
        Assert.Equal(2, pending!.Seq);
        Assert.Contains("which cluster", pending.Text);
    }

    [Fact]
    public void PendingRequest_ClearsOnceTheHumanReplies()
    {
        var log = new ProjectedEventLog();
        log.Append(MessageCall(1, "user", "which cluster?"));
        Assert.NotNull(log.FindPendingUserRequest());

        log.Append(Reply(2, "build01"));

        Assert.Null(log.FindPendingUserRequest());
    }

    [Fact]
    public void AskingAgainAfterAReply_BecomesPendingOnceMore()
    {
        var log = new ProjectedEventLog();
        log.Append(MessageCall(1, "user", "which cluster?"));
        log.Append(Reply(2, "build01"));
        log.Append(MessageCall(3, "user", "which time window?"));

        var pending = log.FindPendingUserRequest();

        Assert.NotNull(pending);
        Assert.Equal(3, pending!.Seq);
        Assert.Contains("time window", pending.Text);
    }

    [Fact]
    public void MessageToAScout_IsNotAPendingUserRequest()
    {
        var log = new ProjectedEventLog();
        log.Append(MessageCall(1, "sharp-badger", "check the kubelet logs"));

        Assert.Null(log.FindPendingUserRequest());
    }

    [Fact]
    public void OrdinaryToolActivity_LeavesNothingPending()
    {
        var log = new ProjectedEventLog();
        log.Append(Call(1));
        log.Append(Result(2, 1, "ok"));

        Assert.Null(log.FindPendingUserRequest());
    }

    [Fact]
    public void WorkContinuingAfterTheQuestion_DoesNotClearIt()
    {
        // Only a reply from the human clears the block. A scout finishing its errand
        // in the meantime must not make the lead look unblocked.
        var log = new ProjectedEventLog();
        log.Append(MessageCall(1, "user", "which cluster?"));
        log.Append(Call(2));
        log.Append(Result(3, 2, "pods listed"));

        var pending = log.FindPendingUserRequest();

        Assert.NotNull(pending);
        Assert.Equal(1, pending!.Seq);
    }
}
