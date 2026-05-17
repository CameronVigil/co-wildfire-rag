using System.Net.Http.Json;
using System.Text.Json;
using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;

namespace CoWildfireApi.Services;

/// <summary>
/// Monitors RAWS stations for sudden wind spikes or RH drops on cells scoring >= 6.0.
/// Uses fresh MesoWest data (5-min cache) rather than the 1-hour scoring cache.
///
/// Thresholds (per spec):
///   wind jump >= 15 mph  → severity "warning"
///   rh drop  >= 10 pp   → severity "warning"
///   both simultaneously  → severity "critical"
///
/// Per-cell 30-minute cooldown prevents alert fatigue during active weather events.
/// No-ops silently if MesoWest token is not configured.
/// </summary>
public class RawsAlertPoller
{
    private const double WindJumpMph  = 15.0;
    private const double RhDropPct    = 10.0;
    private const double MinRiskScore = 6.0;
    private const double RadiusMiles  = 31.1;  // ~50 km

    private static readonly TimeSpan Cooldown  = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DataCache = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PruneAge  = TimeSpan.FromHours(2);

    private readonly HttpClient _http;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly FeedService _feed;
    private readonly ILogger<RawsAlertPoller> _logger;

    // h3Index → (wind mph, rh %, fetched at)
    private readonly Dictionary<string, (double? Wind, double? Rh, DateTimeOffset FetchedAt)> _prev = new();
    // h3Index → last alert emitted
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new();

    public RawsAlertPoller(
        IHttpClientFactory httpFactory,
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration config,
        FeedService feed,
        ILogger<RawsAlertPoller> logger)
    {
        _http      = httpFactory.CreateClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "CoWildfireAnalyzer/1.0 (contact@cowildfire.dev)");
        _dbFactory = dbFactory;
        _config    = config;
        _feed      = feed;
        _logger    = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        var token = _config["MesoWest:Token"];
        if (string.IsNullOrWhiteSpace(token)) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var highRiskCells = await db.H3Cells
            .Where(c => c.Resolution == 6 && c.CurrentRiskScore >= (decimal)MinRiskScore)
            .Select(c => new { c.H3Index, Lat = (double)c.CenterLat, Lon = (double)c.CenterLon })
            .ToListAsync(ct);

        foreach (var cell in highRiskCells)
        {
            try { await CheckCellAsync(cell.H3Index, cell.Lat, cell.Lon, token, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RAWS alert check failed for {H3}", cell.H3Index);
            }
        }

        PruneStaleEntries();
    }

    private async Task CheckCellAsync(
        string h3Index, double lat, double lon, string token, CancellationToken ct)
    {
        // Skip if we have a fresh reading
        if (_prev.TryGetValue(h3Index, out var existing) &&
            DateTimeOffset.UtcNow - existing.FetchedAt < DataCache)
            return;

        var (wind, rh) = await FetchRawsAsync(lat, lon, token, ct);

        if (_prev.TryGetValue(h3Index, out var prev) && (wind != null || rh != null))
        {
            bool windJump = prev.Wind != null && wind != null
                            && (wind.Value - prev.Wind.Value) >= WindJumpMph;
            bool rhDrop   = prev.Rh   != null && rh   != null
                            && (prev.Rh.Value - rh.Value) >= RhDropPct;

            if ((windJump || rhDrop) && CanAlert(h3Index))
            {
                string severity = (windJump && rhDrop) ? "critical" : "warning";
                string detail   = BuildDetail(wind, rh, prev.Wind, prev.Rh, windJump, rhDrop);

                await _feed.PublishAsync(new LiveFeedEvent
                {
                    Type     = "raws-alert",
                    Severity = severity,
                    Source   = "RAWS",
                    Detail   = detail,
                    H3Index  = h3Index,
                }, ct);

                _lastAlert[h3Index] = DateTimeOffset.UtcNow;
                _logger.LogInformation("RAWS alert ({Severity}) for {H3}: {Detail}",
                    severity, h3Index, detail);
            }
        }

        _prev[h3Index] = (wind, rh, DateTimeOffset.UtcNow);
    }

    private bool CanAlert(string h3Index) =>
        !_lastAlert.TryGetValue(h3Index, out var last) ||
        DateTimeOffset.UtcNow - last >= Cooldown;

    private static string BuildDetail(
        double? wind, double? rh, double? prevWind, double? prevRh,
        bool windJump, bool rhDrop)
    {
        var parts = new List<string>();
        if (windJump && wind != null && prevWind != null)
            parts.Add($"Wind jumped +{wind.Value - prevWind.Value:F0} mph → {wind.Value:F0} mph");
        if (rhDrop && rh != null && prevRh != null)
            parts.Add($"RH dropped -{prevRh.Value - rh.Value:F0}pp → {rh.Value:F0}%");
        return string.Join("; ", parts);
    }

    private async Task<(double? Wind, double? Rh)> FetchRawsAsync(
        double lat, double lon, string token, CancellationToken ct)
    {
        var url = "https://api.synopticdata.com/v2/stations/timeseries" +
                  $"?token={token}" +
                  $"&radius={lat:F4},{lon:F4},{RadiusMiles:F1}" +
                  $"&recent=60" + // last 60 min for fresher data than the 1-hr scoring cache
                  $"&vars=wind_speed,relative_humidity" +
                  $"&units=english&obtimezone=utc&limit=1";

        var resp = await _http.GetFromJsonAsync<JsonElement>(url, ct);
        var summary = resp.GetProperty("SUMMARY");
        if (summary.GetProperty("NUMBER_OF_OBJECTS").GetInt32() == 0) return (null, null);

        var station = resp.GetProperty("STATION")[0];
        if (!station.TryGetProperty("OBSERVATIONS", out var obs)) return (null, null);

        return (GetLatest(obs, "wind_speed_set_1"), GetLatest(obs, "relative_humidity_set_1"));
    }

    private static double? GetLatest(JsonElement obs, string key)
    {
        if (!obs.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        for (int i = arr.GetArrayLength() - 1; i >= 0; i--)
        {
            var e = arr[i];
            if (e.ValueKind != JsonValueKind.Null && e.TryGetDouble(out double v)) return v;
        }
        return null;
    }

    private void PruneStaleEntries()
    {
        var cutoff = DateTimeOffset.UtcNow - PruneAge;
        foreach (var k in _prev.Where(kv => kv.Value.FetchedAt < cutoff)
                                .Select(kv => kv.Key).ToList())
        {
            _prev.Remove(k);
            _lastAlert.Remove(k);
        }
    }
}
