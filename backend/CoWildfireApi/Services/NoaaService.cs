using System.Net.Http.Json;
using System.Text.Json;
using CoWildfireApi.Data;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Retry;

namespace CoWildfireApi.Services;

/// <summary>
/// Fetches hourly wind speed, relative humidity, and precipitation probability from
/// NOAA Weather.gov for a given lat/lon. Also checks Colorado Red Flag Warnings.
///
/// NOAA API flow (two calls per cell):
///   1. GET /points/{lat},{lon}                          → resolves forecast office + grid coordinates
///   2. GET /gridpoints/{office}/{x},{y}/forecast/hourly → current-hour conditions
///
/// Cache: weather result per H3 index (1-hour TTL).
///        grid point URL per H3 index (permanent — doesn't change for a given lat/lon).
///        Red Flag Warning status (1-hour TTL, shared for all cells).
///
/// Gridpoint URLs are persisted to the database so they survive restarts, eliminating
/// the /points call for every cell after the first run.
///
/// User-Agent header is configured on the named "noaa" HttpClient in Program.cs.
/// NOAA requires this header — requests without it return 403.
/// </summary>
public class NoaaService
{
    private readonly HttpClient _http;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FeedService _feed;
    private readonly ILogger<NoaaService> _logger;

    // Grid point URL cache (permanent per lat/lon) — keyed by h3Index
    private readonly Dictionary<string, string> _pointsUrlCache = new();
    private readonly SemaphoreSlim _pointsLock = new(1, 1);

    // Weather result cache (1-hour TTL) — keyed by h3Index
    private readonly Dictionary<string, (NoaaWeather Weather, DateTimeOffset Expiry)> _weatherCache = new();
    private readonly SemaphoreSlim _weatherLock = new(1, 1);

    // Red Flag Warning (1-hour TTL, single value for all of Colorado)
    private bool _redFlagActive;
    private DateTimeOffset _redFlagExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _rfLock = new(1, 1);

    // Expanded fire weather alerts (Fire Weather Watch, Fire Warning, Extreme Fire Danger)
    private readonly HashSet<string> _publishedAlertIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _alertLock = new(1, 1);

    private static readonly TimeSpan WeatherCacheTtl = TimeSpan.FromHours(1);

    // Polly v8: retry 3 times with exponential backoff on transient failures
    private static readonly ResiliencePipeline RetryPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay             = TimeSpan.FromSeconds(2),
            BackoffType       = DelayBackoffType.Exponential,
            ShouldHandle      = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>()
                .Handle<JsonException>(),
        })
        .Build();

    public NoaaService(
        IHttpClientFactory httpFactory,
        IDbContextFactory<AppDbContext> dbFactory,
        FeedService feed,
        ILogger<NoaaService> logger)
    {
        _http      = httpFactory.CreateClient("noaa");
        _dbFactory = dbFactory;
        _feed      = feed;
        _logger    = logger;
    }

    /// <summary>
    /// Returns hourly weather for the given H3 cell center. Cached 1 hour per cell.
    /// Returns null if NOAA is unreachable after retries.
    /// </summary>
    public async Task<NoaaWeather?> GetWeatherAsync(
        string h3Index, double lat, double lon, CancellationToken ct = default)
    {
        await _weatherLock.WaitAsync(ct);
        try
        {
            if (_weatherCache.TryGetValue(h3Index, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
                return cached.Weather;
        }
        finally { _weatherLock.Release(); }

        try
        {
            string forecastHourlyUrl = await GetForecastHourlyUrlAsync(h3Index, lat, lon, ct);
            return await GetWeatherFromUrlAsync(h3Index, forecastHourlyUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NOAA weather fetch failed for {H3} ({Lat:F4},{Lon:F4})", h3Index, lat, lon);
            return null;
        }
    }

    /// <summary>
    /// Fetches weather given an already-resolved forecastHourly URL, bypassing the /points lookup.
    /// Writes the result to the in-memory weather cache. Returns null on failure.
    /// </summary>
    internal async Task<NoaaWeather?> GetWeatherFromUrlAsync(
        string h3Index, string forecastHourlyUrl, CancellationToken ct = default)
    {
        // Check weather cache first
        await _weatherLock.WaitAsync(ct);
        try
        {
            if (_weatherCache.TryGetValue(h3Index, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
                return cached.Weather;
        }
        finally { _weatherLock.Release(); }

        try
        {
            var forecastJson = await RetryPipeline.ExecuteAsync(
                async token => await FetchJsonAsync(forecastHourlyUrl, token), ct);

            if (forecastJson == null) return null;

            var periods = forecastJson.Value.GetProperty("properties").GetProperty("periods");
            if (periods.GetArrayLength() == 0) return null;

            var period = periods[0]; // first period = current hour

            double windMph = ParseWindSpeed(
                period.TryGetProperty("windSpeed", out var ws)
                    ? ws.GetString() ?? "0 mph"
                    : "0 mph");

            double rh = period.TryGetProperty("relativeHumidity", out var rhProp) &&
                        rhProp.TryGetProperty("value", out var rhVal) &&
                        rhVal.ValueKind != JsonValueKind.Null
                        ? rhVal.GetDouble()
                        : 35.0; // Colorado dry-season average when unavailable

            double precipProb = period.TryGetProperty("probabilityOfPrecipitation", out var precProp) &&
                                 precProp.TryGetProperty("value", out var precVal) &&
                                 precVal.ValueKind != JsonValueKind.Null
                                 ? precVal.GetDouble()
                                 : 5.0;

            bool rf = await IsRedFlagActiveAsync(ct);
            var weather = new NoaaWeather(windMph, rh, precipProb, rf);

            await _weatherLock.WaitAsync(ct);
            try { _weatherCache[h3Index] = (weather, DateTimeOffset.UtcNow.Add(WeatherCacheTtl)); }
            finally { _weatherLock.Release(); }

            return weather;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NOAA forecast fetch failed for {H3} url={Url}", h3Index, forecastHourlyUrl);
            return null;
        }
    }

    /// <summary>
    /// Returns the NOAA forecastHourly URL for a given cell.
    /// Checks in-memory cache first, then DB, then falls back to a live /points call.
    /// Persists newly resolved URLs to the database so they survive restarts.
    /// </summary>
    internal async Task<string> GetForecastHourlyUrlAsync(
        string h3Index, double lat, double lon, CancellationToken ct)
    {
        // 1. In-memory cache
        await _pointsLock.WaitAsync(ct);
        try
        {
            if (_pointsUrlCache.TryGetValue(h3Index, out var cached))
                return cached;
        }
        finally { _pointsLock.Release(); }

        // 2. Database cache
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var dbUrl = await db.H3Cells
                .Where(c => c.H3Index == h3Index && c.NoaaGridpointUrl != null)
                .Select(c => c.NoaaGridpointUrl)
                .FirstOrDefaultAsync(ct);

            if (dbUrl != null)
            {
                await _pointsLock.WaitAsync(ct);
                try { _pointsUrlCache[h3Index] = dbUrl; }
                finally { _pointsLock.Release(); }
                return dbUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB lookup for NoaaGridpointUrl failed for {H3}, falling back to /points call", h3Index);
        }

        // 3. Live /points call
        var pointsJson = await RetryPipeline.ExecuteAsync(
            async token => await FetchJsonAsync(
                $"https://api.weather.gov/points/{lat:F4},{lon:F4}", token),
            ct);

        if (pointsJson == null)
            throw new InvalidOperationException(
                $"NOAA /points returned null for ({lat:F4},{lon:F4})");

        string forecastUrl = pointsJson.Value
            .GetProperty("properties")
            .GetProperty("forecastHourly")
            .GetString()
            ?? throw new InvalidOperationException("forecastHourly URL missing from NOAA response");

        // Write to in-memory cache
        await _pointsLock.WaitAsync(ct);
        try { _pointsUrlCache[h3Index] = forecastUrl; }
        finally { _pointsLock.Release(); }

        // Persist to DB (fire-and-forget; don't block the scoring run)
        _ = PersistGridpointUrlAsync(h3Index, forecastUrl);

        return forecastUrl;
    }

    /// <summary>
    /// Returns true if any Red Flag Warning is currently active in Colorado. Cached 1 hour.
    /// </summary>
    public async Task<bool> IsRedFlagActiveAsync(CancellationToken ct = default)
    {
        await _rfLock.WaitAsync(ct);
        try
        {
            if (_redFlagExpiry > DateTimeOffset.UtcNow)
                return _redFlagActive;
        }
        finally { _rfLock.Release(); }

        try
        {
            var alertJson = await RetryPipeline.ExecuteAsync(
                async token => await FetchJsonAsync(
                    "https://api.weather.gov/alerts/active?area=CO&event=Red%20Flag%20Warning", token),
                ct);

            int count = alertJson?.GetProperty("features").GetArrayLength() ?? 0;

            bool previouslyActive;
            await _rfLock.WaitAsync(ct);
            try
            {
                previouslyActive = _redFlagActive;
                _redFlagActive   = count > 0;
                _redFlagExpiry   = DateTimeOffset.UtcNow.AddHours(1);
            }
            finally { _rfLock.Release(); }

            if (count > 0)
                _logger.LogInformation("Red Flag Warning active in Colorado ({Count} zones)", count);

            if (_redFlagActive && !previouslyActive)
            {
                await _feed.PublishAsync(new Models.LiveFeedEvent
                {
                    Type      = "alert",
                    Severity  = "critical",
                    Source    = "NOAA",
                    Detail    = $"Red Flag Warning active in Colorado ({count} zones)",
                    SourceUrl = "https://www.weather.gov/alerts/co",
                }, ct);
            }

            return _redFlagActive;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Red Flag Warning check failed");
            await _rfLock.WaitAsync(ct);
            try { _redFlagExpiry = DateTimeOffset.UtcNow.AddMinutes(15); }
            finally { _rfLock.Release(); }
            return false;
        }
    }

    /// <summary>
    /// Polls NWS active alerts for Colorado and emits feed events for newly-active
    /// Fire Weather Watch, Fire Warning, and Extreme Fire Danger alerts.
    /// Does not re-emit alerts that were already published in a prior poll.
    /// </summary>
    public async Task PollExpandedAlertsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await RetryPipeline.ExecuteAsync(
                async token => await FetchJsonAsync(
                    "https://api.weather.gov/alerts/active?area=CO&status=actual", token),
                ct);

            if (json == null) return;

            var features   = json.Value.GetProperty("features");
            var currentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < features.GetArrayLength(); i++)
            {
                var feature = features[i];

                var featureId = feature.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? ""
                    : "";
                if (string.IsNullOrEmpty(featureId)) continue;
                currentIds.Add(featureId);

                var props     = feature.GetProperty("properties");
                var eventType = props.TryGetProperty("event", out var evtEl) && evtEl.ValueKind == JsonValueKind.String
                    ? evtEl.GetString() ?? ""
                    : "";

                string? feedType = eventType switch
                {
                    "Fire Weather Watch"  => "fire-weather-watch",
                    "Fire Warning"        => "fire-warning",
                    "Extreme Fire Danger" => "fire-weather-watch",
                    _ => null,
                };
                if (feedType == null) continue;

                await _alertLock.WaitAsync(ct);
                bool isNew;
                try { isNew = !_publishedAlertIds.Contains(featureId); }
                finally { _alertLock.Release(); }

                if (!isNew) continue;

                var headline = props.TryGetProperty("headline", out var hl) && hl.ValueKind == JsonValueKind.String
                    ? hl.GetString()
                    : null;

                await _feed.PublishAsync(new Models.LiveFeedEvent
                {
                    Type      = feedType,
                    Severity  = feedType == "fire-warning" ? "critical" : "warning",
                    Source    = "NWS",
                    Detail    = headline ?? $"{eventType} issued for Colorado",
                    SourceUrl = "https://www.weather.gov/alerts/co",
                }, ct);

                _logger.LogInformation("Fire weather alert published: {Event}", eventType);
            }

            // Prune expired alert IDs so the set doesn't grow unbounded
            await _alertLock.WaitAsync(ct);
            try
            {
                _publishedAlertIds.IntersectWith(currentIds);
                foreach (var id in currentIds) _publishedAlertIds.Add(id);
            }
            finally { _alertLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Expanded NWS alert poll failed");
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a newly resolved gridpoint URL to the database in the background.
    /// Uses a fresh DbContext to avoid threading issues with the singleton.
    /// </summary>
    private async Task PersistGridpointUrlAsync(string h3Index, string forecastUrl)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.H3Cells
                .Where(c => c.H3Index == h3Index)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.NoaaGridpointUrl, forecastUrl));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist NoaaGridpointUrl for {H3}", h3Index);
        }
    }

    private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    /// <summary>
    /// Parses NOAA wind speed strings: "5 mph", "10 to 15 mph", "Calm".
    /// For ranges ("10 to 15 mph"), returns the upper bound (conservative).
    /// </summary>
    private static double ParseWindSpeed(string windSpeed)
    {
        if (string.IsNullOrWhiteSpace(windSpeed) ||
            windSpeed.Equals("Calm", StringComparison.OrdinalIgnoreCase))
            return 0;

        var parts = windSpeed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && double.TryParse(parts[0], out double low))
        {
            if (parts.Length >= 3 &&
                parts[1].Equals("to", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(parts[2], out double high))
                return high;
            return low;
        }

        return 0;
    }
}

/// <summary>
/// Weather snapshot from NOAA for a single H3 cell center.
/// </summary>
public record NoaaWeather(
    double WindSpeedMph,
    double RelativeHumidityPct,
    double PrecipitationProbabilityPct,
    bool   RedFlagWarning
);
