using System.Threading.Channels;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// In-process SSE event bus. Singleton: holds subscriber channels and a rolling recent-events buffer.
/// External data services call Publish() or PublishAsync(); FeedController streams via Subscribe().
/// </summary>
public class FeedService
{
    private readonly List<Channel<LiveFeedEvent>> _channels = new();
    private readonly List<LiveFeedEvent> _recent = new();
    private readonly object _lock = new();
    private const int RecentMax = 100;

    public IReadOnlyList<LiveFeedEvent> RecentEvents
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public ChannelReader<LiveFeedEvent> Subscribe()
    {
        var ch = Channel.CreateBounded<LiveFeedEvent>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock) _channels.Add(ch);
        return ch.Reader;
    }

    public void Unsubscribe(ChannelReader<LiveFeedEvent> reader)
    {
        lock (_lock)
        {
            var ch = _channels.FirstOrDefault(c => c.Reader == reader);
            if (ch == null) return;
            _channels.Remove(ch);
            ch.Writer.TryComplete();
        }
    }

    public void Publish(LiveFeedEvent item)
    {
        lock (_lock)
        {
            _recent.Add(item);
            if (_recent.Count > RecentMax)
                _recent.RemoveAt(0);

            foreach (var ch in _channels)
                ch.Writer.TryWrite(item);
        }
    }

    public Task PublishAsync(LiveFeedEvent evt, CancellationToken ct = default)
    {
        Publish(evt);
        return Task.CompletedTask;
    }
}
