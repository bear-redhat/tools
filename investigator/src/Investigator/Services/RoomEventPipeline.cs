using Investigator.Models;

namespace Investigator.Services;

public sealed class RoomEventPipeline
{
    private readonly RoomEventBus _bus;
    private readonly IReadOnlyList<IEventEnricher> _enrichers;
    private int _seq;

    /// <param name="startSeq">
    /// Sequence number to continue from. On resume the persisted log is replayed through a
    /// throwaway pipeline to rebuild room state, consuming sequence numbers 1..N; the live
    /// pipeline must start at N so newly emitted events do not collide with the replayed
    /// ones and a client cursor stays monotonic across a restart.
    /// </param>
    /// <param name="log">
    /// Optional retained log. The bus fans out to live subscribers only, so without this
    /// the projected stream -- every tool call and result -- is unrecoverable once emitted.
    /// </param>
    public RoomEventPipeline(RoomEventBus bus, IEnumerable<IEventEnricher> enrichers,
        int startSeq = 0, ProjectedEventLog? log = null)
    {
        _bus = bus;
        _enrichers = enrichers.ToList();
        _seq = startSeq;
        _log = log;
    }

    private readonly ProjectedEventLog? _log;

    public RoomEventBus Bus => _bus;

    /// <summary>Highest sequence number assigned so far.</summary>
    public int CurrentSeq => Volatile.Read(ref _seq);

    public T? GetEnricher<T>() where T : class, IEventEnricher =>
        _enrichers.OfType<T>().FirstOrDefault();

    public async ValueTask<int> EmitAsync(RoomEvent evt, CancellationToken ct = default)
    {
        evt = AssignSeq(evt);
        var assignedSeq = evt.Seq;
        var batch = new List<RoomEvent> { evt };

        foreach (var enricher in _enrichers)
        {
            var extras = await enricher.EnrichAsync(evt, ct);
            foreach (var e in extras)
                batch.Add(AssignSeq(e));
        }

        foreach (var e in batch)
        {
            _log?.Append(e);
            _bus.Publish(e);
        }

        return assignedSeq;
    }

    private RoomEvent AssignSeq(RoomEvent evt) =>
        evt with { Seq = Interlocked.Increment(ref _seq) };
}
