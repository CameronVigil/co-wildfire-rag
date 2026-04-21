using System.Threading.Channels;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Singleton pub/sub hub for the /api/feed SSE stream.
///
/// Producers call <see cref="PublishAsync"/> from any lifetime scope.
/// The FeedController SSE loop consumes via <see cref="Subscribe"/>, which returns a
/// per-connection Channel reader so multiple concurrent clients each receive every event.
///
/// Implementation: maintains a list of subscriber Channels. PublishAsync writes each event
/// to every live subscriber's channel. If a subscriber's channel is full or closed, it is
/// removed (slow consumer protection).
///
/// Per live-feed.md: FeedService MUST be a singleton — injected into every event-producing
/// service (NoaaService, FirmsService, RiskScoringService, RagService, InciwebIngester,
/// HmsService, AirNowService).
/// </summary>
public class FeedService
{
    private readonly ILogger<FeedService> _logger;
    private readonly List<Channel<LiveFeedEvent>> _subscribers = new();
    private readonly object _lock = new();

    // Per-subscriber buffer: drop oldest if reader is too slow so producers never block.
    private const int SubscriberBufferSize = 256;

    public FeedService(ILogger<FeedService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Publish an event to every currently connected SSE subscriber.
    /// Never throws; failures are logged but do not propagate to producers.
    /// </summary>
    public ValueTask PublishAsync(LiveFeedEvent evt, CancellationToken ct = default)
    {
        Channel<LiveFeedEvent>[] snapshot;
        lock (_lock)
        {
            snapshot = _subscribers.ToArray();
        }

        foreach (var ch in snapshot)
        {
            if (!ch.Writer.TryWrite(evt))
            {
                // Channel is full (slow consumer) or closed — drop oldest and retry once
                if (ch.Reader.TryRead(out _))
                    ch.Writer.TryWrite(evt);
            }
        }

        _logger.LogDebug("FeedService published {Type} ({Severity}) to {Count} subscribers",
            evt.Type, evt.Severity, snapshot.Length);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by FeedController when a new SSE client connects.
    /// Returns a reader that receives all subsequently published events.
    /// Caller MUST invoke the returned IDisposable when the SSE connection ends.
    /// </summary>
    public (ChannelReader<LiveFeedEvent> Reader, IDisposable Subscription) Subscribe()
    {
        var ch = Channel.CreateBounded<LiveFeedEvent>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            FullMode      = BoundedChannelFullMode.DropOldest,
            SingleReader  = true,
            SingleWriter  = false,
        });

        lock (_lock) _subscribers.Add(ch);
        _logger.LogInformation("FeedService: new SSE subscriber (total={Count})", _subscribers.Count);

        return (ch.Reader, new Subscription(this, ch));
    }

    private void Unsubscribe(Channel<LiveFeedEvent> ch)
    {
        lock (_lock) _subscribers.Remove(ch);
        ch.Writer.TryComplete();
        _logger.LogInformation("FeedService: SSE subscriber disconnected (remaining={Count})",
            _subscribers.Count);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FeedService _parent;
        private readonly Channel<LiveFeedEvent> _channel;
        private bool _disposed;

        public Subscription(FeedService parent, Channel<LiveFeedEvent> channel)
        {
            _parent  = parent;
            _channel = channel;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _parent.Unsubscribe(_channel);
        }
    }
}
