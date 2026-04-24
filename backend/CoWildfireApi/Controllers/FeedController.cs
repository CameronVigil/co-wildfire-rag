using System.Text.Json;
using CoWildfireApi.Models;
using CoWildfireApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CoWildfireApi.Controllers;

[ApiController]
[Route("api/feed")]
public class FeedController : ControllerBase
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

            await foreach (var item in reader.ReadAllAsync(ct))
                await WriteEventAsync(item, ct);
        }
        catch (OperationCanceledException) { }
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
