using System.Threading.Channels;
using Investigator.Models;

namespace Investigator.Services;

public sealed class TranscriptStore
{
    private readonly List<RoomEvent> _events = [];
    private readonly Channel<RoomEvent> _channel = Channel.CreateUnbounded<RoomEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _lock = new();

    private int _seq;

    public void SeedHistory(IEnumerable<RoomEvent> events)
    {
        lock (_lock)
        {
            foreach (var evt in events)
            {
                _events.Add(evt);
                if (evt.Seq > _seq) _seq = evt.Seq;
            }
        }
    }

    /// <summary>
    /// Appends to the durable log, stamping a monotonic sequence number that continues
    /// past any seeded history. Callers previously constructed events with Seq 0 and only
    /// the projection pipeline ever assigned one, so everything written straight to the
    /// store -- external input, recall and stand-down instructions, session end -- stayed
    /// at 0 and could not be ordered or addressed.
    ///
    /// This numbering is independent of <see cref="RoomEventPipeline"/>'s: the store
    /// numbers the persisted log, the pipeline numbers the projected stream.
    /// </summary>
    /// <returns>False once the room has ended and nothing will read the event.</returns>
    public bool Append(RoomEvent evt)
    {
        RoomEvent sequenced;
        lock (_lock)
        {
            sequenced = evt with { Seq = ++_seq };
            _events.Add(sequenced);
        }
        return _channel.Writer.TryWrite(sequenced);
    }

    public ChannelReader<RoomEvent> Reader => _channel.Reader;

    public IReadOnlyList<RoomEvent> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
