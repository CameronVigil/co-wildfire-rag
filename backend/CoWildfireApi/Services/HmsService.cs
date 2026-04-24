using System.IO.Compression;
using CoWildfireApi.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Polly;
using Polly.Retry;

namespace CoWildfireApi.Services;

public class HmsService
{
    private const string BaseUrl =
        "https://satepsanone.nesdis.noaa.gov/pub/FIRE/web/HMS/Smoke_Polygons/Shapefile";

    // Colorado bbox: W, S, E, N (WGS84)
    private const double CoW = -109.06;
    private const double CoS =  36.99;
    private const double CoE = -102.04;
    private const double CoN =  41.00;

    private static readonly GeometryFactory GeoFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private readonly HttpClient _http;
    private readonly OriginClassifierService _origin;
    private readonly FeedService _feed;
    private readonly IConfiguration _config;
    private readonly ILogger<HmsService> _logger;

    // Dedup set — keyed by {date}_{lat:F2}_{lon:F2}. Trimmed when > 10 000 entries.
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

    public HmsService(
        IHttpClientFactory httpFactory,
        OriginClassifierService origin,
        FeedService feed,
        IConfiguration config,
        ILogger<HmsService> logger)
    {
        _http   = httpFactory.CreateClient();
        _origin = origin;
        _feed   = feed;
        _config = config;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        int maxAgeDays = _config.GetValue<int>("Hms:MaxAgeDays", 1);

        // Try today, then fall back day by day up to maxAgeDays
        DateOnly date = DateOnly.FromDateTime(DateTime.UtcNow);
        byte[]? zipBytes = null;
        DateOnly fileDate = date;

        for (int offset = 0; offset <= maxAgeDays; offset++)
        {
            fileDate = date.AddDays(-offset);
            var url = BuildUrl(fileDate);
            try
            {
                zipBytes = await Retry.ExecuteAsync(async t =>
                {
                    var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, t);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return null;
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync(t);
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HMS fetch failed for {Date}", fileDate);
                return;
            }

            if (zipBytes != null) break;
        }

        if (zipBytes == null)
        {
            _logger.LogDebug("HMS: no file available for the past {Days} day(s) — skipping", maxAgeDays + 1);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"hms_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await ExtractZipAsync(zipBytes, tempDir, ct);
            int published = ReadAndPublish(fileDate, tempDir);

            if (_seen.Count > 10_000) _seen.Clear();

            if (published > 0)
                _logger.LogInformation("HMS: {Published} smoke plume event(s) published for {Date}", published, fileDate);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "HMS temp dir cleanup failed: {Dir}", tempDir); }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildUrl(DateOnly d)
    {
        string yyyy   = d.Year.ToString("D4");
        string yyyymd = d.ToString("yyyyMMdd");
        return $"{BaseUrl}/{yyyy}/hms_smoke{yyyymd}.zip";
    }

    private static Task ExtractZipAsync(byte[] zipBytes, string destDir, CancellationToken ct)
    {
        using var ms      = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(destDir, Path.GetFileName(entry.FullName));
            entry.ExtractToFile(dest, overwrite: true);
        }
        return Task.CompletedTask;
    }

    private int ReadAndPublish(DateOnly fileDate, string tempDir)
    {
        // Locate the .shp file — name is hms_smoke{YYYYMMDD}.shp
        var shpFile = Directory.GetFiles(tempDir, "*.shp").FirstOrDefault();
        if (shpFile is null)
        {
            _logger.LogWarning("HMS: no .shp file found in extracted archive for {Date}", fileDate);
            return 0;
        }

        var coloBbox = new Envelope(CoW, CoE, CoS, CoN);
        int published = 0;
        string dateStr = fileDate.ToString("yyyy-MM-dd");

        using var reader = new ShapefileDataReader(shpFile, GeoFactory);
        var header = reader.DbaseHeader;
        int densityIdx = IndexOf(header, "Density");

        while (reader.Read())
        {
            var geom = reader.Geometry;
            if (geom is null || geom.IsEmpty) continue;

            // Pre-filter: skip polygons whose envelope doesn't touch Colorado
            if (!geom.EnvelopeInternal.Intersects(coloBbox)) continue;

            // Read density (may be string or int — normalise to label)
            string density = "Light";
            if (densityIdx > 0)
            {
                var raw = reader.IsDBNull(densityIdx) ? null : reader.GetValue(densityIdx);
                density = ParseDensity(raw);
            }

            string severity = density switch
            {
                "Heavy"  => "critical",
                "Medium" => "warning",
                _        => "info",
            };

            // Centroid
            var centroid = geom.Centroid;
            double lat = centroid.Y;
            double lon = centroid.X;

            string id = $"{dateStr}_{lat:F2}_{lon:F2}";
            if (!_seen.Add(id)) continue;

            bool inCo   = _origin.IsInColorado(lat, lon);
            string where = inCo ? "Colorado" : _origin.GetRegionLabel(lat, lon);

            _feed.Publish(new FeedItem(
                Id:         id,
                EventType:  "smoke-alert",
                Severity:   severity,
                Title:      $"Smoke Plume — {density} Density",
                Detail:     $"HMS · {dateStr} · {where}",
                Lat:        lat,
                Lon:        lon,
                H3Index:    null,
                InColorado: inCo,
                DetectedAt: DateTimeOffset.UtcNow));

            published++;
        }

        return published;
    }

    private static string ParseDensity(object? raw) => raw switch
    {
        int    i when i >= 3 => "Heavy",
        int    i when i == 2 => "Medium",
        int    _             => "Light",
        double d when d >= 3 => "Heavy",
        double d when d >= 2 => "Medium",
        double _             => "Light",
        string s when s.StartsWith("H", StringComparison.OrdinalIgnoreCase) => "Heavy",
        string s when s.StartsWith("M", StringComparison.OrdinalIgnoreCase) => "Medium",
        _ => "Light",
    };

    private static int IndexOf(DbaseFileHeader header, string fieldName)
    {
        for (int i = 0; i < header.NumFields; i++)
            if (string.Equals(header.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                return i + 1; // ShapefileDataReader field access is 1-based
        return -1;
    }
}
