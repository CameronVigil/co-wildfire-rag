using System.Threading.Channels;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// In-process SSE event bus. Singleton: holds subscriber channels and a rolling recent-events buffer.
/// External data services (FirmsService, AirNowService) call Publish(); FeedController streams via Subscribe().
/// </summary>
public class FeedService
{
    private readonly List<Channel<FeedItem>> _channels = new();
    private readonly List<FeedItem> _recent = new();
    private readonly object _lock = new();
    private const int RecentMax = 100;

    public IReadOnlyList<FeedItem> RecentEvents
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public ChannelReader<FeedItem> Subscribe()
    {
        var ch = Channel.CreateBounded<FeedItem>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock) _channels.Add(ch);
        return ch.Reader;
    }

    public void Unsubscribe(ChannelReader<FeedItem> reader)
    {
        lock (_lock)
        {
            var ch = _channels.FirstOrDefault(c => c.Reader == reader);
            if (ch == null) return;
            _channels.Remove(ch);
            ch.Writer.TryComplete();
        }
    }

    public void Publish(FeedItem item)
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
}
