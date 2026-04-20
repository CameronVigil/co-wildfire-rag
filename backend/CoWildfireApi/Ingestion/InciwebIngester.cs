using AngleSharp.Html.Parser;
using CoWildfireApi.Data;
using CoWildfireApi.Models;
using CoWildfireApi.Services;
using Microsoft.EntityFrameworkCore;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace CoWildfireApi.Ingestion;

/// <summary>
/// Ingests InciWeb wildfire incident reports into the Qdrant wildfire_docs collection.
///
/// Pipeline per run:
///   1. Fetch InciWeb RSS feed → filter for Colorado incidents
///   2. For each incident not yet in ingestion_log:
///      a. Fetch HTML incident page with HttpClient
///      b. Parse narrative text with AngleSharp
///      c. Chunk into ~400-word segments with ~50-word overlap
///      d. Embed each chunk with EmbeddingService (nomic-embed-text, 768-dim)
///      e. Upsert to Qdrant collection "wildfire_docs" with full payload
///      f. Mark success in ingestion_log
///
/// Idempotent: each incident URL + pubDate is a unique ingestion_log key.
/// Qdrant point IDs are deterministic (SHA-256 of URL+chunk_index), so upsert is safe.
///
/// Qdrant payload schema per chunk:
///   chunk_id, document_title, source_type, state, year, county, source_url, text, ingested_at
/// </summary>
public class InciwebIngester
{
    private const string CollectionName  = "wildfire_docs";
    private const string RssFeedUrl      = "https://inciweb.nwcg.gov/feeds/rss/incidents/";
    private const int    TargetChunkChars = 1800; // ~400 words @ 4.5 chars/word
    private const int    OverlapChars     = 200;  // ~45-word overlap

    // Colorado bounding box
    private const double CoWest  = -109.06;
    private const double CoSouth =  36.99;
    private const double CoEast  = -102.04;
    private const double CoNorth =  41.00;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly QdrantClient     _qdrant;
    private readonly EmbeddingService _embed;
    private readonly HttpClient       _http;
    private readonly IConfiguration   _config;
    private readonly ILogger<InciwebIngester> _logger;

    public InciwebIngester(
        IDbContextFactory<AppDbContext> dbFactory,
        QdrantClient qdrant,
        EmbeddingService embed,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<InciwebIngester> logger)
    {
        _dbFactory = dbFactory;
        _qdrant    = qdrant;
        _embed     = embed;
        _http      = httpFactory.CreateClient();
        _config    = config;
        _logger    = logger;

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CoWildfireAnalyzer/1.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Run a full ingestion pass. Safe to call repeatedly — already-ingested incidents are skipped.
    /// </summary>
    public async Task IngestAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("InciWeb ingestion starting");

        var incidents = await FetchIncidentsAsync(ct);
        _logger.LogInformation("Found {Count} Colorado incidents in RSS feed", incidents.Count);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int ingested = 0, skipped = 0, errored = 0;

        foreach (var incident in incidents)
        {
            if (ct.IsCancellationRequested) break;

            string logKey = $"{incident.Url}|{incident.PubDate}";

            // Idempotency check
            var existing = await db.IngestionLogs
                .FirstOrDefaultAsync(l => l.Source == "INCIWEB" && l.DatasetKey == logKey, ct);
            if (existing?.Status == "success")
            {
                skipped++;
                continue;
            }

            var logEntry = existing ?? new IngestionLog { Source = "INCIWEB", DatasetKey = logKey };
            logEntry.Status    = "pending";
            logEntry.StartedAt = DateTimeOffset.UtcNow;
            logEntry.ErrorMessage = null;
            if (existing == null) db.IngestionLogs.Add(logEntry);
            await db.SaveChangesAsync(ct);

            try
            {
                int chunksUpserted = await IngestIncidentAsync(incident, ct);

                logEntry.Status       = "success";
                logEntry.RecordsLoaded = chunksUpserted;
                logEntry.CompletedAt  = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                _logger.LogInformation("Ingested '{Title}' → {Chunks} chunks", incident.Title, chunksUpserted);
                ingested++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest incident: {Title} ({Url})", incident.Title, incident.Url);
                logEntry.Status       = "failed";
                logEntry.ErrorMessage = ex.Message;
                logEntry.CompletedAt  = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                errored++;
            }
        }

        _logger.LogInformation("InciWeb ingestion complete: {Ingested} ingested, {Skipped} skipped, {Errored} errors",
            ingested, skipped, errored);
    }

    // ── RSS feed ──────────────────────────────────────────────────────────────

    private async Task<List<IncidentInfo>> FetchIncidentsAsync(CancellationToken ct)
    {
        try
        {
            string rss = await _http.GetStringAsync(RssFeedUrl, ct);
            var doc    = XDocument.Parse(rss);
            XNamespace geo = "http://www.w3.org/2003/01/geo/wgs84_pos#";

            return doc.Descendants("item")
                .Select(item =>
                {
                    string title   = item.Element("title")?.Value ?? "";
                    string link    = item.Element("link")?.Value ?? "";
                    string pubDate = item.Element("pubDate")?.Value ?? "";
                    string desc    = item.Element("description")?.Value ?? "";

                    bool latOk = double.TryParse(item.Element(geo + "lat")?.Value, out double lat);
                    bool lonOk = double.TryParse(item.Element(geo + "long")?.Value, out double lon);

                    return new IncidentInfo(title, link, pubDate, desc,
                        latOk ? lat : (double?)null,
                        lonOk ? lon : (double?)null);
                })
                .Where(IsColoradoIncident)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch InciWeb RSS feed");
            return new List<IncidentInfo>();
        }
    }

    private static bool IsColoradoIncident(IncidentInfo incident)
    {
        // Check by geo coordinates first (most reliable)
        if (incident.Lat.HasValue && incident.Lon.HasValue)
            if (incident.Lat >= CoSouth && incident.Lat <= CoNorth &&
                incident.Lon >= CoWest  && incident.Lon <= CoEast)
                return true;

        // Check title/description for Colorado indicators
        return incident.Title.Contains("Colorado", StringComparison.OrdinalIgnoreCase) ||
               incident.Description.Contains("Colorado", StringComparison.OrdinalIgnoreCase) ||
               // Match " CO " with word boundaries to avoid false positives like "account"
               System.Text.RegularExpressions.Regex.IsMatch(incident.Title, @"\bCO\b");
    }

    // ── Single incident ingestion ─────────────────────────────────────────────

    private async Task<int> IngestIncidentAsync(IncidentInfo incident, CancellationToken ct)
    {
        // Fetch + parse HTML
        string html = await _http.GetStringAsync(incident.Url, ct);
        string text = await ExtractTextAsync(html, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("No extractable text for {Url}", incident.Url);
            return 0;
        }

        // Extract year from pubDate or title
        int year = ExtractYear(incident.PubDate, incident.Title);

        // Chunk and embed
        var chunks  = ChunkText(text);
        var points  = new List<PointStruct>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string chunkText = chunks[i];

            float[] embedding = await _embed.EmbedAsync(
                $"{incident.Title}\n\n{chunkText}", ct);

            string chunkUuid = DeterministicUuid(incident.Url, i);

            var vec = new Vector();
            vec.Data.AddRange(embedding);

            var point = new PointStruct
            {
                Id      = new PointId { Uuid = chunkUuid },
                Vectors = new Vectors { Vector = vec },
            };

            point.Payload["chunk_id"]       = new Value { StringValue = chunkUuid };
            point.Payload["document_title"] = new Value { StringValue = incident.Title };
            point.Payload["source_type"]    = new Value { StringValue = "inciweb" };
            point.Payload["state"]          = new Value { StringValue = "CO" };
            point.Payload["year"]           = new Value { IntegerValue = year };
            point.Payload["source_url"]     = new Value { StringValue = incident.Url };
            point.Payload["text"]           = new Value { StringValue = chunkText };
            point.Payload["chunk_index"]    = new Value { IntegerValue = i };
            point.Payload["ingested_at"]    = new Value { StringValue = DateTimeOffset.UtcNow.ToString("O") };

            points.Add(point);
        }

        if (points.Count > 0)
            await _qdrant.UpsertAsync(CollectionName, points, cancellationToken: ct);

        return points.Count;
    }

    // ── HTML parsing ──────────────────────────────────────────────────────────

    private async Task<string> ExtractTextAsync(string html, CancellationToken ct)
    {
        try
        {
            var parser   = new HtmlParser();
            using var doc = await parser.ParseDocumentAsync(html, ct);

            // InciWeb uses different layouts across incident types; try selectors in order
            string[] selectors =
            {
                "article p",
                ".field-items p",
                ".incident-description p",
                "main p",
                "#content p",
                "p",
            };

            foreach (var selector in selectors)
            {
                var elements = doc.QuerySelectorAll(selector);
                if (elements.Length == 0) continue;

                var paragraphs = elements
                    .Select(el => el.TextContent.Trim())
                    .Where(t => t.Length >= 40)       // skip nav/footer snippets
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (paragraphs.Count > 0)
                    return string.Join("\n\n", paragraphs);
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AngleSharp parsing failed");
            return string.Empty;
        }
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits text into overlapping chunks at paragraph boundaries.
    /// Target: ~400 words (1800 chars). Overlap: ~45 words (200 chars).
    /// </summary>
    private static List<string> ChunkText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        text = text.Trim();
        if (text.Length <= TargetChunkChars) return [text];

        var chunks    = new List<string>();
        var sb        = new StringBuilder();

        var paragraphs = text.Split(
            new[] { "\n\n", "\r\n\r\n" },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in paragraphs)
        {
            string para = raw.Trim();
            if (para.Length < 20) continue;

            if (sb.Length + para.Length > TargetChunkChars && sb.Length > 0)
            {
                string chunk = sb.ToString().Trim();
                if (chunk.Length >= 80) chunks.Add(chunk);

                // Carry over overlap from the end of the current chunk
                string carry = OverlapFrom(chunk);
                sb.Clear();
                if (carry.Length > 0)
                {
                    sb.Append(carry);
                    sb.Append("\n\n");
                }
            }

            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(para);
        }

        if (sb.Length >= 80)
            chunks.Add(sb.ToString().Trim());

        return chunks;
    }

    /// <summary>
    /// Returns the last ~OverlapChars of a chunk, trimmed to a sentence start.
    /// </summary>
    private static string OverlapFrom(string chunk)
    {
        if (chunk.Length <= OverlapChars) return chunk;

        string tail = chunk[^OverlapChars..];
        // Trim to the first complete sentence start in the tail
        int sentStart = tail.IndexOf(". ", StringComparison.Ordinal);
        if (sentStart >= 0 && sentStart < tail.Length - 2)
            return tail[(sentStart + 2)..].Trim();

        return tail.Trim();
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string DeterministicUuid(string url, int chunkIndex)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{url}|{chunkIndex}"));
        // Format first 16 bytes as UUID v4 (variant bits not fully RFC4122 but sufficient for Qdrant)
        var g = new Guid(hash[..16]);
        return g.ToString("D");
    }

    private static int ExtractYear(string pubDate, string title)
    {
        // Try pubDate first
        if (DateTimeOffset.TryParse(pubDate, out var dto))
            return dto.Year;

        // Try to find a 4-digit year in the title
        var match = System.Text.RegularExpressions.Regex.Match(title, @"\b(20\d{2})\b");
        if (match.Success && int.TryParse(match.Value, out int year))
            return year;

        return DateTimeOffset.UtcNow.Year;
    }

    // ── Private types ─────────────────────────────────────────────────────────

    private record IncidentInfo(
        string  Title,
        string  Url,
        string  PubDate,
        string  Description,
        double? Lat,
        double? Lon
    );
}
