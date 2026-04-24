using System.Globalization;
using CoWildfireApi.Models;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Polly;
using Polly.Retry;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls NASA FIRMS (VIIRS SNPP NRT) for active fire detections in an expanded bounding box
/// that covers Colorado plus ~3° of border buffer (-112,34,-99,44 / W,S,E,N).
///
/// API: GET https://firms.modaps.eosdis.nasa.gov/api/area/csv/{MAP_KEY}/VIIRS_SNPP_NRT/{bbox}/1
/// Free MAP_KEY at: firms.modaps.eosdis.nasa.gov/api/
/// Rate limit: ~60 req/min. This service polls once per 10 minutes.
///
/// Each new detection is published to FeedService. Out-of-state detections are published as
/// info/warning events (context only). In-Colorado detections are published as warning/critical.
/// Dedup: detections are keyed by (acq_date, acq_time, lat×3-decimal, lon×3-decimal).
/// </summary>
public class FirmsService
{
    private const string BboxExpanded = "-112,34,-99,44";
    private const string BaseUrl = "https://firms.modaps.eosdis.nasa.gov/api/area/csv";

    private readonly HttpClient _http;
    private readonly OriginClassifierService _origin;
    private readonly FeedService _feed;
    private readonly IConfiguration _config;
    private readonly ILogger<FirmsService> _logger;

    // Dedup set — keyed by canonical detection ID. Trimmed when > 10 000 entries.
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

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

    public FirmsService(
        IHttpClientFactory httpFactory,
        OriginClassifierService origin,
        FeedService feed,
        IConfiguration config,
        ILogger<FirmsService> logger)
    {
        _http   = httpFactory.CreateClient();
        _origin = origin;
        _feed   = feed;
        _config = config;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        var mapKey = _config["Firms:MapKey"];
        if (string.IsNullOrWhiteSpace(mapKey))
        {
            _logger.LogDebug("FIRMS MapKey not configured — skipping poll");
            return;
        }

        var url = $"{BaseUrl}/{mapKey}/VIIRS_SNPP_NRT/{BboxExpanded}/1";

        string csv;
        try
        {
            csv = await Retry.ExecuteAsync(async t =>
                await _http.GetStringAsync(url, t), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FIRMS poll failed");
            return;
        }

        var detections = ParseCsv(csv);
        int published  = 0;

        foreach (var d in detections)
        {
            var id = MakeId(d);
            if (!_seen.Add(id)) continue;

            bool inCo  = _origin.IsInColorado(d.Latitude, d.Longitude);
            string sev = d.Confidence == "high" ? "critical"
                       : d.Confidence == "nominal" ? "warning"
                       : "info";

            // Downgrade out-of-state detections by one severity level
            if (!inCo && sev == "critical") sev = "warning";
            if (!inCo && sev == "warning")  sev = "info";

            string region = _origin.GetRegionLabel(d.Latitude, d.Longitude);

            _feed.Publish(new FeedItem(
                Id:         id,
                EventType:  "fire-detection",
                Severity:   sev,
                Title:      $"Fire Detected — {region}",
                Detail:     $"VIIRS SNPP · {d.Satellite} · FRP {d.Frp:F1} MW · {d.Confidence} confidence",
                Lat:        d.Latitude,
                Lon:        d.Longitude,
                H3Index:    null,
                InColorado: inCo,
                DetectedAt: ParseAcqTime(d.AcqDate, d.AcqTime)));

            published++;
        }

        // Prevent unbounded growth — clear on overflow (detections rotate daily)
        if (_seen.Count > 10_000) _seen.Clear();

        if (published > 0 || detections.Count > 0)
            _logger.LogInformation("FIRMS: {Total} detections, {New} new", detections.Count, published);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static List<FirmsRow> ParseCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv) || csv.TrimStart().StartsWith('<'))
            return new();

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord  = true,
            MissingFieldFound = null,
            BadDataFound     = null,
        };
        using var reader    = new StringReader(csv);
        using var csvReader = new CsvReader(reader, cfg);
        try { return csvReader.GetRecords<FirmsRow>().ToList(); }
        catch { return new(); }
    }

    private static string MakeId(FirmsRow d)
        => $"{d.AcqDate}T{d.AcqTime}_{Math.Round(d.Latitude, 3):F3}_{Math.Round(d.Longitude, 3):F3}";

    private static DateTimeOffset ParseAcqTime(string date, string time)
    {
        // date = "YYYY-MM-DD", time = "HHMM"
        if (DateOnly.TryParseExact(date, "yyyy-MM-dd", null, DateTimeStyles.None, out var d)
            && time.Length == 4
            && int.TryParse(time[..2], out int h)
            && int.TryParse(time[2..], out int m))
        {
            return new DateTimeOffset(d.Year, d.Month, d.Day, h, m, 0, TimeSpan.Zero);
        }
        return DateTimeOffset.UtcNow;
    }

    // ── FIRMS CSV row ─────────────────────────────────────────────────────────

    private sealed class FirmsRow
    {
        [Name("latitude")]   public double Latitude   { get; set; }
        [Name("longitude")]  public double Longitude  { get; set; }
        [Name("bright_ti4")] public float  BrightTi4  { get; set; }
        [Name("acq_date")]   public string AcqDate    { get; set; } = "";
        [Name("acq_time")]   public string AcqTime    { get; set; } = "";
        [Name("satellite")]  public string Satellite  { get; set; } = "";
        [Name("confidence")] public string Confidence { get; set; } = "";
        [Name("frp")]        public float  Frp        { get; set; }
        [Name("daynight")]   public string DayNight   { get; set; } = "";
    }
}
