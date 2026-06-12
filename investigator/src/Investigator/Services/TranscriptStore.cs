using System.Threading.Channels;
using Investigator.Models;

namespace Investigator.Services;

public sealed class TranscriptStore
{
    private readonly List<RoomEvent> _events = [];
    private readonly Channel<RoomEvent> _channel = Channel.CreateUnbounded<RoomEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _lock = new();

    public void SeedHistory(IEnumerable<RoomEvent> events)
    {
        lock (_lock) _events.AddRange(events);
    }

    public void Append(RoomEvent evt)
    {
        lock (_lock) _events.Add(evt);
        _channel.Writer.TryWrite(evt);
    }

    public ChannelReader<RoomEvent> Reader => _channel.Reader;

    public IReadOnlyList<RoomEvent> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
