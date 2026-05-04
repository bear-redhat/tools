using System.Collections.Concurrent;
using System.Threading.Channels;
using Investigator.Models;

namespace Investigator.Services;

public sealed class RoomEventBus
{
    private readonly ConcurrentDictionary<string, (Channel<RoomEvent> Channel, Func<RoomEvent, bool>? Filter)> _subs = new();

    public ChannelReader<RoomEvent> Subscribe(string id, Func<RoomEvent, bool>? filter = null)
    {
        var ch = Channel.CreateUnbounded<RoomEvent>();
        _subs[id] = (ch, filter);
        return ch.Reader;
    }

    public void Unsubscribe(string id)
    {
        if (_subs.TryRemove(id, out var entry))
            entry.Channel.Writer.TryComplete();
    }

    public void Publish(RoomEvent evt)
    {
        foreach (var (_, (ch, filter)) in _subs)
        {
            if (filter is null || filter(evt))
                ch.Writer.TryWrite(evt);
        }
    }

    public void Complete()
    {
        foreach (var (_, (ch, _)) in _subs)
            ch.Writer.TryComplete();
    }
}
