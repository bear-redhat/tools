using Investigator.Models;

namespace Investigator.Services;

public interface IEventEnricher
{
    ValueTask<IReadOnlyList<RoomEvent>> EnrichAsync(RoomEvent evt, CancellationToken ct);
}
