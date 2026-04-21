using System.Globalization;
using CoWildfireApi.Data;
using CoWildfireApi.Models;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Retry;

namespace CoWildfireApi.Services;

/// <summary>
/// NASA FIRMS active fire detection ingestion (Phase 5).
/// Fetches the last N days of VIIRS/MODIS detections in the expanded bbox covering
/// Colorado plus bordering states. Every detection is classified by
/// <see cref="IOriginClassifierService"/>. Out-of-state detections never affect
/// risk score — they are only surfaced via the feed + /api/active-fires.
/// </summary>
public class FirmsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IOriginClassifierService _classifier;
    private readonly FeedService _feed;
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<FirmsService> _logger;

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

    public FirmsService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOriginClassifierService classifier,
        FeedService feed,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<FirmsService> logger)
    {
        _dbFactory  = dbFactory;
        _classifier = classifier;
        _feed       = feed;
        _http       = httpFactory.CreateClient();
        _config     = config;
        _logger     = logger;
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task IngestAsync(CancellationToken ct = default)
    {
        string? apiKey = _config["Firms:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("FIRMS:ApiKey not configured — skipping FIRMS ingestion");
            return;
        }

        string bbox   = _config["Firms:ExpandedBbox"] ?? "-112,34,-99,44";
        string source = _config["Firms:Source"]       ?? "VIIRS_SNPP_NRT";
        int    days   = int.TryParse(_config["Firms:DaysBack"], out var d) ? d : 1;

        string datasetKey = $"FIRMS|{DateTime.UtcNow:yyyyMMddHHmmss}";
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var log = new IngestionLog { Source = "FIRMS", DatasetKey = datasetKey, Status = "pending" };
        db.IngestionLogs.Add(log);
        await db.SaveChangesAsync(ct);

        await _feed.PublishAsync(new LiveFeedEvent
        {
            Type     = "data_fetch",
            Severity = "info",
            Source   = "NASA FIRMS",
            Detail   = "Fetching FIRMS detections…",
        }, ct);

        try
        {
            await _classifier.EnsureLoadedAsync(ct);

            var url = $"https://firms.modaps.eosdis.nasa.gov/api/area/csv/{apiKey}/{source}/{bbox}/{days}";
            string csv = await RetryPipeline.ExecuteAsync(
                async token => await _http.GetStringAsync(url, token), ct);

            var rows = ParseCsv(csv).ToList();
            _logger.LogInformation("FIRMS: parsed {Count} raw rows", rows.Count);

            int inserted = 0, inCo = 0, outCo = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var publishedEventKeys = new HashSet<string>(StringComparer.Ordinal);
            var newOutOfState = new List<(FirmsRow Row, OriginClassification Class)>();

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                string dedupeKey = $"{row.Lat:F4}|{row.Lon:F4}|{row.AcquiredAt:o}|{row.Satellite}";
                if (!seen.Add(dedupeKey)) continue;

                var cls = await _classifier.ClassifyPointAsync(
                    row.Lat, row.Lon, row.Frp, row.Confidence, ct);

                var det = new ActiveFireDetection
                {
                    Latitude    = (decimal)row.Lat,
                    Longitude   = (decimal)row.Lon,
                    Brightness  = row.Brightness.HasValue ? (decimal?)row.Brightness.Value : null,
                    Frp         = row.Frp.HasValue ? (decimal?)row.Frp.Value : null,
                    Confidence  = row.Confidence,
                    Satellite   = row.Satellite,
                    AcquiredAt  = row.AcquiredAt,
                    DayNight    = row.DayNight,
                    IsColorado  = cls.IsColorado,
                    OriginState = cls.OriginState == "UNKNOWN" ? null : cls.OriginState,
                    ImpactType  = cls.ImpactType,
                };

                db.ActiveFireDetections.Add(det);
                inserted++;
                if (cls.IsColorado) inCo++;
                else
                {
                    outCo++;
                    if (cls.ImpactType == "smoke_only")
                    {
                        // Dedupe to ~0.1° tile to avoid spamming the feed
                        string evKey = $"{cls.OriginState}|{Math.Round(row.Lat, 1)}|{Math.Round(row.Lon, 1)}";
                        if (publishedEventKeys.Add(evKey))
                            newOutOfState.Add((row, cls));
                    }
                }
            }

            await db.SaveChangesAsync(ct);

            // Trim stale detections (>72h)
            var cutoff = DateTimeOffset.UtcNow.AddHours(-72);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM active_fire_detections WHERE acquired_at < {0}", cutoff);

            // Publish out-of-state fire events
            foreach (var (row, cls) in newOutOfState)
            {
                await _feed.PublishAsync(new LiveFeedEvent
                {
                    Type            = "out_of_state_fire",
                    Severity        = cls.SmokeTransportLikely ? "warning" : "info",
                    Source          = "NASA FIRMS",
                    OriginState     = cls.OriginState,
                    OriginStateName = cls.OriginStateName,
                    Detail          = $"Out-of-state fire detected in {cls.OriginStateName}" +
                                      (cls.SmokeTransportLikely ? " — smoke transport likely" : ""),
                }, ct);
            }

            log.Status        = "success";
            log.RecordsLoaded = inserted;
            log.CompletedAt   = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await _feed.PublishAsync(new LiveFeedEvent
            {
                Type     = "data_fetch",
                Severity = "info",
                Source   = "NASA FIRMS",
                Detail   = $"Fetched {inserted} detections ({inCo} in-state, {outCo} out-of-state)",
            }, ct);

            _logger.LogInformation(
                "FIRMS ingestion complete: {Inserted} detections ({InCo} CO, {OutCo} out-of-state)",
                inserted, inCo, outCo);
        }
        catch (Exception ex)
        {
            log.Status       = "failed";
            log.ErrorMessage = ex.Message;
            log.CompletedAt  = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogError(ex, "FIRMS ingestion failed");
            throw;
        }
    }

    private static IEnumerable<FirmsRow> ParseCsv(string csv)
    {
        using var reader = new StringReader(csv);
        using var parser = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound     = null,
        });

        if (!parser.Read()) yield break;
        parser.ReadHeader();
        var header = parser.HeaderRecord ?? Array.Empty<string>();

        bool hasBrightTi4 = header.Contains("bright_ti4", StringComparer.OrdinalIgnoreCase);
        bool hasBrightness = header.Contains("brightness", StringComparer.OrdinalIgnoreCase);

        while (parser.Read())
        {
            if (!double.TryParse(parser.GetField("latitude"),  NumberStyles.Any, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(parser.GetField("longitude"), NumberStyles.Any, CultureInfo.InvariantCulture, out var lon)) continue;

            double? bright = null;
            if (hasBrightTi4 && double.TryParse(parser.GetField("bright_ti4"), NumberStyles.Any, CultureInfo.InvariantCulture, out var b1)) bright = b1;
            else if (hasBrightness && double.TryParse(parser.GetField("brightness"), NumberStyles.Any, CultureInfo.InvariantCulture, out var b2)) bright = b2;

            double? frp = null;
            if (double.TryParse(parser.GetField("frp"), NumberStyles.Any, CultureInfo.InvariantCulture, out var frpVal))
                frp = frpVal;

            string? confRaw = parser.GetField("confidence")?.Trim();
            string? confidence = NormalizeConfidence(confRaw);
            string? satellite  = parser.GetField("satellite")?.Trim();
            string? dayNight   = parser.GetField("daynight")?.Trim();
            if (dayNight != null && dayNight.Length > 1) dayNight = dayNight[..1];

            string acqDate = parser.GetField("acq_date") ?? "";
            string acqTime = parser.GetField("acq_time") ?? "0";
            if (!DateTime.TryParseExact(acqDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var date))
                continue;

            // acq_time is HHmm (e.g. "1345" for 13:45 UTC); may have leading zero dropped
            int hhmm = int.TryParse(acqTime, out var t) ? t : 0;
            int hh = hhmm / 100, mm = hhmm % 100;
            var acquiredAt = new DateTimeOffset(date.Year, date.Month, date.Day, hh, mm, 0, TimeSpan.Zero);

            yield return new FirmsRow(lat, lon, bright, frp, confidence, satellite, acquiredAt, dayNight);
        }
    }

    private static string? NormalizeConfidence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // VIIRS uses "l"/"n"/"h"; MODIS uses 0-100 integer
        if (int.TryParse(raw, out int num))
        {
            if (num < 30)  return "low";
            if (num < 80)  return "nominal";
            return "high";
        }
        return raw.ToLowerInvariant() switch
        {
            "l" or "low"     => "low",
            "n" or "nominal" => "nominal",
            "h" or "high"    => "high",
            _                => raw.ToLowerInvariant(),
        };
    }

    private record FirmsRow(
        double Lat,
        double Lon,
        double? Brightness,
        double? Frp,
        string? Confidence,
        string? Satellite,
        DateTimeOffset AcquiredAt,
        string? DayNight);
}
