using System.Text;
using System.Text.Json;
using CoWildfireApi.Models;
using CoWildfireApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CoWildfireApi.Controllers;

/// <summary>
/// Server-Sent Events endpoint for the live feed. See live-feed.md.
/// Streams every LiveFeedEvent published to FeedService plus a heartbeat every 30s.
/// </summary>
[ApiController]
[Route("api/feed")]
public class FeedController : ControllerBase
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly FeedService _feed;
    private readonly ILogger<FeedController> _logger;

    public FeedController(FeedService feed) => _feed = feed;

    [HttpGet("recent")]
    public IActionResult GetRecent() => Ok(_feed.RecentEvents);

    [HttpGet]
    public async Task StreamAsync(CancellationToken ct)
        {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var reader = _feed.Subscribe();
            try
            {
            foreach (var item in _feed.RecentEvents)
                await WriteEventAsync(item, ct);

            await foreach (var item in reader.ReadAllAsync(ct))
                await WriteEventAsync(item, ct);
            }
            catch (OperationCanceledException) { }
        }, heartbeatCts.Token);

        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                await WriteEventAsync(evt, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SSE client disconnected");
        }
        finally
        {
            _feed.Unsubscribe(reader);
        }
    }

    private async Task WriteEventAsync(FeedItem item, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(item, _json);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
