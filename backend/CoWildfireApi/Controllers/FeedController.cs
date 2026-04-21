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
[Route("api")]
public class FeedController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly FeedService _feed;
    private readonly ILogger<FeedController> _logger;

    public FeedController(FeedService feed, ILogger<FeedController> logger)
    {
        _feed = feed;
        _logger = logger;
    }

    [HttpGet("feed")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Connection"] = "keep-alive";

        var (reader, subscription) = _feed.Subscribe();
        using var _sub = subscription;

        // Initial hello + heartbeat so clients know the stream is live immediately.
        await WriteEventAsync(new LiveFeedEvent
        {
            Type = "heartbeat",
            Severity = "info",
            Detail = "connected",
        }, ct);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, heartbeatCts.Token);
                    await WriteEventAsync(new LiveFeedEvent
                    {
                        Type = "heartbeat",
                        Severity = "info",
                        Detail = "alive",
                    }, heartbeatCts.Token);
                }
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
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }
        }
    }

    private async Task WriteEventAsync(LiveFeedEvent evt, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(evt, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"event: {evt.Type}\ndata: {payload}\n\n");
        await Response.Body.WriteAsync(bytes, ct);
        await Response.Body.FlushAsync(ct);
    }
}
