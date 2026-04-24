using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Polly;
using Polly.Retry;

namespace CoWildfireApi.Services;

/// <summary>
/// Fetches current AQI observations for Colorado from the EPA AirNow API.
///
/// API: GET https://www.airnowapi.org/aq/observation/latLong/current/
///      ?format=application/json&latitude=39.0&longitude=-105.5&distance=300&API_KEY={key}
/// Free key at: docs.airnowapi.org
///
/// Publishes PM2.5 / Ozone readings at AQI ≥ 101 ("Unhealthy for Sensitive Groups") to FeedService.
/// Cache: 30-minute TTL (AirNow updates observations hourly).
/// </summary>
public class AirNowService
{
    private const string BaseUrl = "https://www.airnowapi.org/aq/observation/latLong/current/";

    // Centered on Colorado; 300 miles captures all in-state monitoring stations
    private const double QueryLat = 39.0;
    private const double QueryLon = -105.5;
    private const int    QueryMiles = 300;

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly IConfiguration _config;
    private readonly ILogger<AirNowService> _logger;

    private (List<AirNowObs> Data, DateTimeOffset Expiry) _cache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    // Track which station+parameter+hour combos have been published to avoid duplicate feed entries
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);

    private static readonly ResiliencePipeline Retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay            = TimeSpan.FromSeconds(5),
            BackoffType      = DelayBackoffType.Exponential,
            ShouldHandle     = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>(),
        })
        .Build();

    public AirNowService(
        IDbContextFactory<AppDbContext> dbFactory,
        FeedService feed,
        IHttpClientFactory httpFactory,
        FeedService feed,
        IConfiguration config,
        ILogger<AirNowService> logger)
    {
        _http   = httpFactory.CreateClient();
        _feed   = feed;
        _config = config;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        var apiKey = _config["AirNow:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("AirNow ApiKey not configured — skipping poll");
            return;
        }

        var obs = await FetchObservationsAsync(apiKey, ct);
        int published = 0;

        foreach (var o in obs)
        {
            if (o.Aqi < 101) continue; // below "Unhealthy for Sensitive Groups"

            var key = $"{o.ReportingArea}|{o.ParameterName}|{o.DateObserved}T{o.HourObserved:D2}";
            if (!_published.Add(key)) continue;

            string sev = o.Aqi >= 201 ? "critical"
                       : o.Aqi >= 151 ? "warning"
                       : "info";

            string categoryName = GetCategoryName(o.Aqi);

            _feed.Publish(new FeedItem(
                Id:         key,
                EventType:  "air-quality",
                Severity:   sev,
                Title:      $"Air Quality — {o.ReportingArea}",
                Detail:     $"{o.ParameterName} · AQI {o.Aqi} ({categoryName})",
                Lat:        o.Latitude,
                Lon:        o.Longitude,
                H3Index:    null,
                InColorado: o.StateCode == "CO",
                DetectedAt: DateTimeOffset.UtcNow));

            published++;
        }

        if (_published.Count > 5_000) _published.Clear();

        if (published > 0)
            _logger.LogInformation("AirNow: {Published} elevated AQI events published", published);
                }
                finally { sem.Release(); }
            }).ToList();

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task<List<AirNowObs>> FetchObservationsAsync(string apiKey, CancellationToken ct)
            {
        await _cacheLock.WaitAsync(ct);
        try
                {
            if (_cache.Data != null && _cache.Expiry > DateTimeOffset.UtcNow)
                return _cache.Data;

            var url = $"{BaseUrl}?format=application/json" +
                      $"&latitude={QueryLat}&longitude={QueryLon}" +
                      $"&distance={QueryMiles}&API_KEY={apiKey}";

            List<AirNowObs>? data = null;
            try
            {
                data = await Retry.ExecuteAsync(async t =>
                    await _http.GetFromJsonAsync<List<AirNowObs>>(url, t), ct);
        }
        catch (Exception ex)
        {
                _logger.LogWarning(ex, "AirNow fetch failed");
                return _cache.Data ?? new();
    }

            data ??= new();
            // Filter to PM2.5 and Ozone only
            data = data
                .Where(o => o.ParameterName is "PM2.5" or "OZONE")
                .ToList();

            _cache = (data, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30));
            return data;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static string GetCategoryName(int aqi) => aqi switch
    {
        <= 50  => "Good",
        <= 100 => "Moderate",
        <= 150 => "Unhealthy for Sensitive Groups",
        <= 200 => "Unhealthy",
        <= 300 => "Very Unhealthy",
        _      => "Hazardous",
    };

    // ── DTO ───────────────────────────────────────────────────────────────────

    private sealed class AirNowObs
    {
        public string DateObserved  { get; set; } = "";
        public int    HourObserved  { get; set; }
        public string ReportingArea { get; set; } = "";
        public string StateCode     { get; set; } = "";
        public double Latitude      { get; set; }
        public double Longitude     { get; set; }
        public string ParameterName { get; set; } = "";
        public int    Aqi           { get; set; }
    }
}
