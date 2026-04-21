using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;

namespace CoWildfireApi.Services;

/// <summary>
/// EPA AirNow per-H3-6-cell AQI ingestion (Phase 5).
/// Does NOT affect risk_score (spec §Risk Score Impact Rules — AQI is informational only).
/// </summary>
public class AirNowService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FeedService _feed;
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<AirNowService> _logger;

    public AirNowService(
        IDbContextFactory<AppDbContext> dbFactory,
        FeedService feed,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<AirNowService> logger)
    {
        _dbFactory = dbFactory;
        _feed      = feed;
        _http      = httpFactory.CreateClient();
        _config    = config;
        _logger    = logger;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task IngestAsync(CancellationToken ct = default)
    {
        string? apiKey = _config["AirNow:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("AirNow:ApiKey not configured — skipping AirNow ingestion");
            return;
        }

        string datasetKey = $"AIRNOW|{DateTime.UtcNow:yyyyMMddHH}";
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.IngestionLogs
            .FirstOrDefaultAsync(l => l.Source == "AIRNOW" && l.DatasetKey == datasetKey, ct);
        if (existing?.Status == "success")
        {
            _logger.LogInformation("AirNow {Key} already ingested — skipping", datasetKey);
            return;
        }

        var log = existing ?? new IngestionLog { Source = "AIRNOW", DatasetKey = datasetKey };
        log.Status    = "pending";
        log.StartedAt = DateTimeOffset.UtcNow;
        log.ErrorMessage = null;
        if (existing == null) db.IngestionLogs.Add(log);
        await db.SaveChangesAsync(ct);

        await _feed.PublishAsync(new LiveFeedEvent
        {
            Type = "data_fetch", Severity = "info", Source = "EPA AirNow",
            Detail = "Fetching AirNow AQI observations…",
        }, ct);

        try
        {
            var cells = await db.H3Cells.AsNoTracking()
                .Where(c => c.Resolution == 6)
                .Select(c => new { c.H3Index, c.CenterLat, c.CenterLon })
                .ToListAsync(ct);

            _logger.LogInformation("AirNow: querying {Count} H3-6 cells", cells.Count);

            var observed = DateTimeOffset.UtcNow;
            int good = 0, moderate = 0, unhealthy = 0, misses = 0;

            using var sem = new SemaphoreSlim(5);
            var tasks = cells.Select(async cell =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var obs = await FetchCellAsync(cell.H3Index, (double)cell.CenterLat,
                        (double)cell.CenterLon, apiKey, observed, ct);
                    return obs;
                }
                finally { sem.Release(); }
            }).ToList();

            var results = (await Task.WhenAll(tasks)).Where(r => r != null).Cast<AqiObservation>().ToList();

            // Upsert each
            foreach (var r in results)
            {
                await db.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO aqi_observations (h3_index, observed_at, aqi, pm25, category, smoke_inferred)
                    VALUES ({0},{1},{2},{3},{4},FALSE)
                    ON CONFLICT (h3_index, observed_at) DO UPDATE
                       SET aqi = EXCLUDED.aqi, pm25 = EXCLUDED.pm25, category = EXCLUDED.category",
                    r.H3Index, r.ObservedAt, (object?)r.Aqi ?? DBNull.Value,
                    (object?)r.Pm25 ?? DBNull.Value, (object?)r.Category ?? DBNull.Value);

                if (r.Aqi.HasValue)
                {
                    if (r.Aqi < 51)       good++;
                    else if (r.Aqi < 101) moderate++;
                    else                  unhealthy++;
                }
                else misses++;
            }

            // Smoke-inferred heuristic (spec: aqi>100 AND pm25>35.4 AND no nearby in-state fire)
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE aqi_observations a SET smoke_inferred = TRUE
                WHERE a.aqi > 100 AND a.pm25 > 35.4
                  AND NOT EXISTS (
                    SELECT 1 FROM active_fire_detections d, h3_cells h
                    WHERE h.h3_index = a.h3_index AND d.is_colorado = TRUE
                      AND ST_DWithin(d.location::geography,
                                     ST_SetSRID(ST_MakePoint(h.center_lon, h.center_lat),4326)::geography,
                                     80467)
                      AND d.acquired_at > NOW() - INTERVAL '24 hours'
                  )");

            // Update h3_cells.smoke_present / smoke_inferred from latest AQI
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE h3_cells h SET
                    smoke_present  = COALESCE((a.aqi >= 100), FALSE),
                    smoke_inferred = COALESCE(a.smoke_inferred, FALSE),
                    updated_at     = NOW()
                FROM (
                    SELECT DISTINCT ON (h3_index) h3_index, aqi, smoke_inferred
                    FROM aqi_observations
                    ORDER BY h3_index, observed_at DESC
                ) a
                WHERE h.h3_index = a.h3_index");

            log.Status        = "success";
            log.RecordsLoaded = results.Count;
            log.CompletedAt   = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await _feed.PublishAsync(new LiveFeedEvent
            {
                Type = "data_fetch", Severity = "info", Source = "EPA AirNow",
                Detail = $"AirNow: {good} good, {moderate} moderate, {unhealthy} unhealthy, {misses} no-data",
            }, ct);

            _logger.LogInformation("AirNow ingestion complete: {Count} observations", results.Count);
        }
        catch (Exception ex)
        {
            log.Status       = "failed";
            log.ErrorMessage = ex.Message;
            log.CompletedAt  = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogError(ex, "AirNow ingestion failed");
            throw;
        }
    }

    private async Task<AqiObservation?> FetchCellAsync(
        string h3Index, double lat, double lon, string apiKey,
        DateTimeOffset observedAt, CancellationToken ct)
    {
        string url = "https://www.airnowapi.org/aq/observation/latLong/current/" +
                     $"?latitude={lat.ToString("F5", CultureInfo.InvariantCulture)}" +
                     $"&longitude={lon.ToString("F5", CultureInfo.InvariantCulture)}" +
                     $"&distance=25&format=application/json&API_KEY={apiKey}";

        try
        {
            var arr = await _http.GetFromJsonAsync<List<AirNowRecord>>(url, ct);
            if (arr == null || arr.Count == 0) return null;

            int maxAqi = arr.Where(r => r.AQI.HasValue).Select(r => r.AQI!.Value).DefaultIfEmpty(-1).Max();
            if (maxAqi < 0) return null;

            var pm25 = arr.FirstOrDefault(r => string.Equals(r.ParameterName, "PM2.5", StringComparison.OrdinalIgnoreCase));
            string? category = arr.FirstOrDefault(r => r.AQI == maxAqi)?.Category?.Name;

            return new AqiObservation
            {
                H3Index     = h3Index,
                ObservedAt  = observedAt,
                Aqi         = (short)maxAqi,
                Pm25        = pm25?.AQI.HasValue == true ? (decimal?)pm25.Value : null,
                Category    = category,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AirNow fetch failed for {H3}", h3Index);
            return null;
        }
    }

    private record AirNowRecord
    {
        [JsonPropertyName("ParameterName")] public string? ParameterName { get; init; }
        [JsonPropertyName("AQI")]           public int?    AQI           { get; init; }
        [JsonPropertyName("Value")]         public double? Value         { get; init; }
        [JsonPropertyName("Category")]      public AirNowCategory? Category { get; init; }
    }

    private record AirNowCategory
    {
        [JsonPropertyName("Number")] public int?    Number { get; init; }
        [JsonPropertyName("Name")]   public string? Name   { get; init; }
    }
}
