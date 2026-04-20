using System.Net.Http.Json;
using System.Text.Json;

namespace CoWildfireApi.Services;

/// <summary>
/// Fetches observed weather from the nearest RAWS station via MesoWest/Synoptic Data API.
/// RAWS stations report wind speed, relative humidity, and (where available) fuel moisture.
///
/// Used as the primary weather source when a station is within 50km.
/// NOAA gridded forecast is the fallback when no RAWS station is nearby.
///
/// Register a free token at synopticdata.com. Add to appsettings:
///   "MesoWest": { "Token": "YOUR_TOKEN" }
///
/// If no token is configured, this service returns null for all lookups (NOAA fallback used).
///
/// API endpoint:
///   GET https://api.synopticdata.com/v2/stations/timeseries
///     ?token={token}&radius={lat},{lon},{radius_miles}&recent=120
///     &vars=wind_speed,relative_humidity,fuel_moisture&units=english&limit=5
/// </summary>
public class RawsService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<RawsService> _logger;

    // In-memory cache: h3Index → (RawsData?, expiry)
    private readonly Dictionary<string, (RawsData? Data, DateTimeOffset Expiry)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private const double SearchRadiusKm    = 50.0;
    private const double KmToMiles         = 0.621371;
    private const double MilesToKm         = 1.60934;
    private const int    RecentWindowMins  = 120; // observations from last 2 hours

    public RawsService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<RawsService> logger)
    {
        _http   = httpFactory.CreateClient();
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Returns the nearest RAWS station observation for the given H3 cell center.
    /// Cached per H3 index for 1 hour. Returns null if no token is configured,
    /// no station is within 50km, or the API call fails.
    /// </summary>
    public async Task<RawsData?> GetNearestStationAsync(
        string h3Index, double lat, double lon, CancellationToken ct = default)
    {
        var token = _config["MesoWest:Token"];
        if (string.IsNullOrWhiteSpace(token))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(h3Index, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
                return cached.Data;
        }
        finally { _lock.Release(); }

        double radiusMiles = SearchRadiusKm * KmToMiles;

        string url = "https://api.synopticdata.com/v2/stations/timeseries" +
                     $"?token={token}" +
                     $"&radius={lat:F4},{lon:F4},{radiusMiles:F1}" +
                     $"&recent={RecentWindowMins}" +
                     $"&vars=wind_speed,relative_humidity,fuel_moisture" +
                     $"&units=english" + // wind in mph, distance in miles
                     $"&obtimezone=utc" +
                     $"&limit=5";

        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>(url, ct);
            var summary = resp.GetProperty("SUMMARY");

            if (summary.GetProperty("NUMBER_OF_OBJECTS").GetInt32() == 0)
            {
                await SetCache(h3Index, null);
                return null;
            }

            var stations = resp.GetProperty("STATION");
            RawsData? best = null;

            foreach (var station in stations.EnumerateArray())
            {
                var data = ParseStation(station);
                if (data == null) continue;
                // Prefer stations with fuel moisture data, then closest
                bool bestHasFm   = best?.FuelMoisturePct != null;
                bool dataHasFm   = data.FuelMoisturePct != null;
                bool closer      = data.DistanceKm < (best?.DistanceKm ?? double.MaxValue);

                if (best == null || (!bestHasFm && dataHasFm) || (bestHasFm == dataHasFm && closer))
                    best = data;
            }

            await SetCache(h3Index, best);

            if (best != null)
                _logger.LogDebug("RAWS: {Station} at {Dist:F1}km for {H3} — wind={Wind} rh={RH} fm={FM}",
                    best.StationId, best.DistanceKm, h3Index,
                    best.WindSpeedMph?.ToString("F1") ?? "n/a",
                    best.RelativeHumidityPct?.ToString("F1") ?? "n/a",
                    best.FuelMoisturePct?.ToString("F1") ?? "n/a");

            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MesoWest fetch failed for {H3} ({Lat:F4},{Lon:F4})", h3Index, lat, lon);
            await SetCache(h3Index, null);
            return null;
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────────

    private static RawsData? ParseStation(JsonElement station)
    {
        try
        {
            string stationId = station.TryGetProperty("STID", out var stid)
                ? stid.GetString() ?? "unknown"
                : "unknown";

            // DISTANCE is in miles when units=english
            double distKm = station.TryGetProperty("DISTANCE", out var dist)
                ? dist.GetDouble() * MilesToKm
                : 999;

            if (!station.TryGetProperty("OBSERVATIONS", out var obs))
                return null;

            double? wind = GetLatestObs(obs, "wind_speed_set_1");
            double? rh   = GetLatestObs(obs, "relative_humidity_set_1");

            // Try both fuel moisture variable names (station-dependent)
            double? fm = GetLatestObs(obs, "fuel_moisture_set_1")
                      ?? GetLatestObs(obs, "fuel_moisture_set_2");

            // Need at least wind OR rh to be useful
            if (wind == null && rh == null) return null;

            return new RawsData(stationId, distKm, wind, rh, fm);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the last (most recent) non-null value from a MesoWest observation array.
    /// Arrays are ordered oldest-first; last element is most recent.
    /// </summary>
    private static double? GetLatestObs(JsonElement obs, string key)
    {
        if (!obs.TryGetProperty(key, out var arr)) return null;
        if (arr.ValueKind != JsonValueKind.Array) return null;

        // Scan from the end to find the most recent non-null observation
        for (int i = arr.GetArrayLength() - 1; i >= 0; i--)
        {
            var elem = arr[i];
            if (elem.ValueKind == JsonValueKind.Null) continue;
            if (elem.TryGetDouble(out double v)) return v;
        }

        return null;
    }

    private async Task SetCache(string h3Index, RawsData? data)
    {
        await _lock.WaitAsync();
        try { _cache[h3Index] = (data, DateTimeOffset.UtcNow.Add(CacheTtl)); }
        finally { _lock.Release(); }
    }
}

/// <summary>
/// Observed weather from the nearest RAWS station to an H3 cell center.
/// </summary>
public record RawsData(
    string  StationId,
    double  DistanceKm,
    double? WindSpeedMph,
    double? RelativeHumidityPct,
    double? FuelMoisturePct
);
