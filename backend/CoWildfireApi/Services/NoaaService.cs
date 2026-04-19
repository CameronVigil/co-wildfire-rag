namespace CoWildfireApi.Services;

/// <summary>
/// Phase 2 stub — NOAA Weather.gov integration.
///
/// TODO: NOAA Weather.gov API requires a User-Agent header:
///   User-Agent: CoWildfireAnalyzer/1.0 (contact@example.com)
/// Without it, requests are rejected (403). Set this in the HttpClient factory registration.
///
/// Key endpoints (data-sources.md):
///   GET https://api.weather.gov/points/{lat},{lon}
///   GET https://api.weather.gov/gridpoints/{office}/{X},{Y}/forecast/hourly
///   GET https://api.weather.gov/alerts/active?area=CO&amp;event=Red%20Flag%20Warning
///
/// Cache per H3-6 cell for 1 hour. Use Polly retry with exponential backoff.
/// </summary>
public class NoaaService
{
    private readonly ILogger<NoaaService> _logger;
    public NoaaService(ILogger<NoaaService> logger) => _logger = logger;
}
