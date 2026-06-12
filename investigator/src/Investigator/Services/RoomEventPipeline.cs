using Investigator.Models;

namespace Investigator.Services;

public sealed class RoomEventPipeline
{
    private readonly RoomEventBus _bus;
    private readonly IReadOnlyList<IEventEnricher> _enrichers;
    private int _seq;

    public RoomEventPipeline(RoomEventBus bus, IEnumerable<IEventEnricher> enrichers)
    {
        _bus = bus;
        _enrichers = enrichers.ToList();
    }

    public RoomEventBus Bus => _bus;

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
            _bus.Publish(e);

        return assignedSeq;
    }

    private RoomEvent AssignSeq(RoomEvent evt) =>
        evt with { Seq = Interlocked.Increment(ref _seq) };
}
