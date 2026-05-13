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

            using var timer = new PeriodicTimer(HeartbeatInterval);
            var timerTask  = timer.WaitForNextTickAsync(ct).AsTask();
            var readerTask = reader.WaitToReadAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                var winner = await Task.WhenAny(timerTask, readerTask);

                if (winner == timerTask)
                {
                    await timerTask;
                    var hbJson = JsonSerializer.Serialize(
                        new { type = "heartbeat", timestamp = DateTimeOffset.UtcNow }, _json);
                    await Response.WriteAsync($"event: heartbeat\ndata: {hbJson}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    timerTask = timer.WaitForNextTickAsync(ct).AsTask();
                }
                else
                {
                    if (!await readerTask) break;
                    while (reader.TryRead(out var item))
                        await WriteEventAsync(item, ct);
                    readerTask = reader.WaitToReadAsync(ct).AsTask();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _feed.Unsubscribe(reader);
        }
    }

    private async Task WriteEventAsync(LiveFeedEvent item, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(item, _json);
        await Response.WriteAsync($"event: {item.Type}\ndata: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
