using System.Text.Json;
using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Polly;
using Polly.Retry;

namespace CoWildfireApi.Services;

/// <summary>
/// NOAA HMS daily smoke plume GeoJSON ingestion (Phase 5).
/// Fetches GeoJSON, keeps only plumes that intersect Colorado, classifies plume origin,
/// and publishes <c>out_of_state_smoke</c> events for non-CO-origin plumes.
/// Daily semantic — the run does a delete-then-insert of rows for the target plume_date.
/// </summary>
public class HmsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IOriginClassifierService _classifier;
    private readonly FeedService _feed;
    private readonly HttpClient _http;
    private readonly ILogger<HmsService> _logger;

    private static readonly GeometryFactory GeoFactory = new(new PrecisionModel(), 4326);

    private static readonly ResiliencePipeline RetryPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay            = TimeSpan.FromSeconds(2),
            BackoffType      = DelayBackoffType.Exponential,
            ShouldHandle     = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TaskCanceledException>(),
        })
        .Build();

    public HmsService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOriginClassifierService classifier,
        FeedService feed,
        IHttpClientFactory httpFactory,
        ILogger<HmsService> logger)
    {
        _dbFactory  = dbFactory;
        _classifier = classifier;
        _feed       = feed;
        _http       = httpFactory.CreateClient();
        _logger     = logger;
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task IngestAsync(DateOnly? date = null, CancellationToken ct = default)
    {
        var target = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        string datasetKey = $"HMS|{target:yyyy-MM-dd}";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.IngestionLogs
            .FirstOrDefaultAsync(l => l.Source == "HMS" && l.DatasetKey == datasetKey, ct);
        if (existing?.Status == "success")
        {
            _logger.LogInformation("HMS {Date} already ingested — skipping", target);
            return;
        }

        var log = existing ?? new IngestionLog { Source = "HMS", DatasetKey = datasetKey };
        log.Status    = "pending";
        log.StartedAt = DateTimeOffset.UtcNow;
        log.ErrorMessage = null;
        if (existing == null) db.IngestionLogs.Add(log);
        await db.SaveChangesAsync(ct);

        await _feed.PublishAsync(new LiveFeedEvent
        {
            Type = "data_fetch", Severity = "info", Source = "NOAA HMS",
            Detail = $"Fetching HMS smoke plumes for {target:yyyy-MM-dd}…",
        }, ct);

        try
        {
            await _classifier.EnsureLoadedAsync(ct);

            string url = $"https://satepsanone.nesdis.noaa.gov/pub/FIRE/web/HMS/Smoke_Polygons/GeoJSON/" +
                         $"{target:yyyy}/{target:yyyy_MM_dd}.json";

            string json;
            try
            {
                json = await RetryPipeline.ExecuteAsync(
                    async token => await _http.GetStringAsync(url, token), ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("HMS fetch failed (likely no plumes for {Date}): {Msg}", target, ex.Message);
                log.Status       = "success";
                log.RecordsLoaded = 0;
                log.CompletedAt  = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var features = ParseFeatures(json);
            _logger.LogInformation("HMS: parsed {Count} plume features", features.Count);

            // Delete existing rows for this date (delete-then-insert)
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM smoke_events WHERE plume_date = {0}", target);

            int stored = 0;
            var outOfStateEvents = new List<(SmokeEvent Evt, OriginClassification Cls)>();

            foreach (var feat in features)
            {
                ct.ThrowIfCancellationRequested();

                var geom = feat.Geometry;
                if (geom == null || geom.IsEmpty) continue;

                MultiPolygon mp = geom switch
                {
                    MultiPolygon m => m,
                    Polygon p      => GeoFactory.CreateMultiPolygon(new[] { p }),
                    _              => null!,
                };
                if (mp == null) continue;

                // Only store plumes that touch Colorado
                if (!PlumeIntersectsColorado(mp)) continue;

                string density = NormalizeDensity(
                    feat.Attributes.Exists("Density") ? feat.Attributes["Density"]?.ToString() :
                    feat.Attributes.Exists("density") ? feat.Attributes["density"]?.ToString() : null);

                var cls = await _classifier.ClassifyPlumeAsync(mp, ct);
                var counties = cls.IsColorado
                    ? Array.Empty<string>()
                    : (await _classifier.GetAffectedColoradoCountiesAsync(mp, ct)).ToArray();

                var desc = cls.IsColorado
                    ? $"{density} smoke plume originating in Colorado"
                    : $"{density} smoke plume from {cls.OriginStateName}";

                var evt = new SmokeEvent
                {
                    PlumeDate        = target,
                    Density          = density,
                    Plume            = mp,
                    OriginState      = cls.OriginState == "UNKNOWN" ? null : cls.OriginState,
                    OriginStateName  = cls.OriginStateName,
                    IsColoradoOrigin = cls.IsColorado,
                    ColoradoCountiesAffected = counties,
                    SmokeDescription = desc,
                    Source           = "NOAA_HMS",
                    FetchedAt        = DateTimeOffset.UtcNow,
                };

                db.SmokeEvents.Add(evt);
                stored++;
                if (!cls.IsColorado) outOfStateEvents.Add((evt, cls));
            }

            await db.SaveChangesAsync(ct);

            foreach (var (evt, cls) in outOfStateEvents)
            {
                string sev = evt.Density switch
                {
                    "heavy"  => "warning",
                    "medium" => "warning",
                    _        => "info",
                };
                await _feed.PublishAsync(new LiveFeedEvent
                {
                    Type             = "out_of_state_smoke",
                    Severity         = sev,
                    Source           = "NOAA HMS",
                    OriginState      = cls.OriginState,
                    OriginStateName  = cls.OriginStateName,
                    ImpactedCounties = evt.ColoradoCountiesAffected,
                    Detail           = evt.SmokeDescription ?? "Smoke plume detected",
                }, ct);
            }

            log.Status        = "success";
            log.RecordsLoaded = stored;
            log.CompletedAt   = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("HMS ingestion complete: {Stored} plumes stored for {Date}", stored, target);
        }
        catch (Exception ex)
        {
            log.Status       = "failed";
            log.ErrorMessage = ex.Message;
            log.CompletedAt  = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogError(ex, "HMS ingestion failed");
            throw;
        }
    }

    private static List<IFeature> ParseFeatures(string json)
    {
        var reader = new GeoJsonReader();
        var fc = reader.Read<FeatureCollection>(json);
        return fc?.ToList() ?? new List<IFeature>();
    }

    private bool PlumeIntersectsColorado(MultiPolygon plume)
    {
        // The classifier exposes neither the CO boundary nor a public intersect method,
        // so we fall back to a lightweight DB-level check via bbox pre-filter + ST_Intersects.
        // The HMS daily feed is small (< ~100 features) so this is acceptable.
        using var db = _dbFactory.CreateDbContext();
        var co = db.StateBoundaries.AsNoTracking().FirstOrDefault(s => s.StateAbbr == "CO");
        if (co == null) return false;
        return co.Boundary.Intersects(plume);
    }

    private static string NormalizeDensity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "coarse";
        return raw.Trim().ToLowerInvariant() switch
        {
            "light"  => "coarse",
            "medium" => "medium",
            "heavy"  => "heavy",
            "coarse" => "coarse",
            _        => "coarse",
        };
    }
}
