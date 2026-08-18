using Investigator.Models;
using Investigator.Services;

namespace Investigator.Tests;

/// <summary>
/// The projected event stream is the only thing a remote client can page through, so its
/// sequence numbers have to be monotonic within a run and continue across a resume.
/// </summary>
public class EventSequencingTests
{
    private static RoomEvent.ExternalInput Input(string text) =>
        new(0, "user", DateTimeOffset.UtcNow, text);

    [Fact]
    public void TranscriptStore_StampsMonotonicSequenceNumbers()
    {
        var store = new TranscriptStore();

        store.Append(Input("first"));
        store.Append(Input("second"));
        store.Append(Input("third"));

        Assert.Equal([1, 2, 3], store.Events.Select(e => e.Seq));
    }

    [Fact]
    public void TranscriptStore_PublishesTheSequencedEvent_NotTheZeroedOriginal()
    {
        var store = new TranscriptStore();

        store.Append(Input("hello"));

        Assert.True(store.Reader.TryRead(out var published));
        Assert.Equal(1, published!.Seq);
    }

    [Fact]
    public void TranscriptStore_SeededHistory_ContinuesPastHighestSeenSeq()
    {
        var store = new TranscriptStore();
        store.SeedHistory([
            new RoomEvent.ExternalInput(7, "user", DateTimeOffset.UtcNow, "old"),
            new RoomEvent.ExternalInput(11, "user", DateTimeOffset.UtcNow, "older"),
        ]);

        store.Append(Input("new"));

        Assert.Equal(12, store.Events[^1].Seq);
    }

    [Fact]
    public async Task RoomEventPipeline_ContinuesFromSeedRatherThanRestartingAtOne()
    {
        var pipeline = new RoomEventPipeline(new RoomEventBus(), [], startSeq: 40);

        var first = await pipeline.EmitAsync(Input("after-resume"));
        var second = await pipeline.EmitAsync(Input("and-another"));

        Assert.Equal(41, first);
        Assert.Equal(42, second);
        Assert.Equal(42, pipeline.CurrentSeq);
    }

    [Fact]
    public async Task RoomEventPipeline_FreshRoomStartsAtOne()
    {
        var pipeline = new RoomEventPipeline(new RoomEventBus(), []);

        Assert.Equal(1, await pipeline.EmitAsync(Input("first")));
    }

    [Fact]
    public void ResumedSession_CarriesTheReplayedHistoryAndItsSequencePosition()
    {
        var snapshot = new SessionSnapshot
        {
            Id = "conv-1",
            Events =
            [
                new RoomEvent.ExternalInput(1, "user", DateTimeOffset.UtcNow, "why did the job fail?"),
                new RoomEvent.ExternalInput(2, "little-bear", DateTimeOffset.UtcNow, "looking into it"),
            ],
        };

        var session = snapshot.ToSession();

        // Replay rebuilds the inspectable transcript, and its highest seq is what the live
        // pipeline continues from -- one source of truth rather than a stashed copy.
        Assert.True(session.InvestigationEventLog.Count > 0);
        Assert.True(session.InvestigationEventLog.HighestSeq > 0);
    }

    [Fact]
    public void HighestSeq_SurvivesEviction_SoARestartNeverReusesASequenceNumber()
    {
        var log = new ProjectedEventLog(capacity: 2);
        for (var i = 1; i <= 5; i++)
            log.Append(new RoomEvent.TextMessage(i, "little-bear", DateTimeOffset.UtcNow, $"m{i}"));

        Assert.Equal(2, log.Count);
        Assert.Equal(5, log.HighestSeq);

        // A room restarting in process seeds from HighestSeq, not from what is retained.
        var resumed = new RoomEventPipeline(new RoomEventBus(), [], startSeq: log.HighestSeq, log: log);
        Assert.Equal(6, resumed.EmitAsync(Input("after restart")).AsTask().Result);
    }
}
