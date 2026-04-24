namespace CoWildfireApi.Models;

public record FeedItem(
    string Id,
    string EventType,    // "fire-detection" | "smoke-alert" | "air-quality" | "red-flag"
    string Severity,     // "info" | "warning" | "critical"
    string Title,
    string Detail,
    double Lat,
    double Lon,
    string? H3Index,
    bool InColorado,
    DateTimeOffset DetectedAt
);
