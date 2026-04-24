using System.Text.Json.Serialization;

namespace CoWildfireApi.Models;

/// <summary>
/// Envelope for a single SSE feed event published by FeedService.
/// Schema matches live-feed.md — camelCase on wire; nullable extras omitted when null.
/// </summary>
public record LiveFeedEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "info";  // 'info' | 'warning' | 'critical'

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("h3Index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? H3Index { get; init; }

    [JsonPropertyName("county")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? County { get; init; }

    [JsonPropertyName("originState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginState { get; init; }

    [JsonPropertyName("originStateName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginStateName { get; init; }

    [JsonPropertyName("impactedCounties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ImpactedCounties { get; init; }
}
