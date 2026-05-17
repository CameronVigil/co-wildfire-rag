using CoWildfireApi.Data;
using CoWildfireApi.Models;
using H3;
using H3.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoWildfireApi.Services;

/// <summary>
/// RAG (Retrieval-Augmented Generation) query pipeline for the Colorado Wildfire Analyzer.
///
/// Pipeline per query:
///   1. Load H3 cell stats + current conditions from h3_cells (geographic context)
///   2. Load statewide aggregate conditions from h3_cells (top-risk regions, red flag coverage)
///   3. Fetch live active incidents from NIFC ArcGIS API
///   4. Embed the user's question with EmbeddingService (nomic-embed-text)
///   5. Dense vector search in Qdrant "wildfire_docs" collection (top-20, CO state filter)
///   6. Keyword re-rank retrieved documents with a BM25-inspired scorer
///   7. RRF (Reciprocal Rank Fusion) merge of semantic + keyword rank lists
///   8. Take top-5 chunks as context
///   9. Build structured system prompt with cell stats, conditions, aggregate context, retrieved docs
///  10. Call llama3.2 via Ollama chat API
///  11. Return structured QueryResponse matching the API spec
/// </summary>
public class RagService
{
    private const string CollectionName = "wildfire_docs";
    private const int    DenseTopK      = 20;
    private const int    FinalTopK      = 5;
    private const int    RrfK           = 60;

    // NIFC current wildland fire locations, Colorado only
    private const string NifcQueryUrl =
        "https://services3.arcgis.com/T4QMspbfLg3qTGWY/arcgis/rest/services/" +
        "Current_WildlandFire_Locations/FeatureServer/0/query" +
        "?where=POOState%20%3D%20%27US-CO%27" +
        "&outFields=UniqueFireIdentifier%2CIncidentName%2CDailyAcres%2CPercentContained%2CFireDiscoveryDateTime" +
        "&f=json&resultRecordCount=20";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly QdrantClient     _qdrant;
    private readonly EmbeddingService _embed;
    private readonly FeedService      _feed;
    private readonly IConfiguration   _config;
    private readonly ILogger<RagService> _logger;
    private readonly HttpClient       _http;

    public RagService(
        IDbContextFactory<AppDbContext> dbFactory,
        QdrantClient qdrant,
        EmbeddingService embed,
        FeedService feed,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<RagService> logger)
    {
        _dbFactory = dbFactory;
        _qdrant    = qdrant;
        _embed     = embed;
        _feed      = feed;
        _config    = config;
        _logger    = logger;
        _http      = httpFactory.CreateClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CoWildfireAnalyzer/1.0 (contact@cowildfire.dev)");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Resolve H3 cell
        var cell = await ResolveCellAsync(request, ct);

        // 2. Gather statewide aggregate context and live NIFC incidents in parallel
        var aggregateTask = GetAggregateContextAsync(ct);
        var nifcTask      = GetLiveIncidentsAsync(ct);

        // 3. Embed the question
        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embed.EmbedAsync(request.Question, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed query — returning minimal response");
            return BuildMinimalResponse(cell, sw.ElapsedMilliseconds, "embedding_failed");
        }

        // 4. Dense vector search in Qdrant
        var denseResults = await SearchQdrantAsync(queryEmbedding, ct);

        // 5. Keyword re-rank
        var keywordRanked = KeywordRank(denseResults, request.Question);

        // 6. RRF merge of dense + keyword rank lists
        var merged = ReciprocalRankFusion(denseResults, keywordRanked);

        // 7. Take top-5
        var topChunks = merged.Take(FinalTopK).ToList();

        // 8. Await background context tasks
        string aggregateContext = await aggregateTask;
        string liveIncidents    = await nifcTask;

        // 9. Build prompt with all available context
        string context      = BuildContext(topChunks);
        string systemPrompt = BuildSystemPrompt(cell, context, aggregateContext, liveIncidents);
        string modelName    = _config["Ollama:ChatModel"] ?? "llama3.2";

        // 10. Call LLM
        string answer;
        try
        {
            answer = await CallLlamaAsync(systemPrompt, request.Question, modelName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM inference failed");
            answer = "Unable to generate a response — the LLM service is currently unavailable. " +
                     "Please check that Ollama is running with `ollama serve` and that llama3.2 is pulled.";
        }

        sw.Stop();

        await _feed.PublishAsync(new LiveFeedEvent
        {
            Type     = "rag_query",
            Severity = "info",
            Source   = "RagService",
            H3Index  = cell?.H3Index,
            Detail   = $"RAG query answered in {sw.ElapsedMilliseconds} ms ({topChunks.Count} sources, NIFC: {(liveIncidents.Length > 10 ? "yes" : "none")})",
        }, ct);

        // 11. Assemble response
        return new QueryResponse
        {
            Answer            = answer,
            Sources           = topChunks.Select(BuildSourceDocument).ToList(),
            CellStats         = cell != null ? BuildCellStats(cell) : null,
            CurrentConditions = cell != null ? BuildCurrentConditions(cell) : null,
            ProcessingMs      = sw.ElapsedMilliseconds,
            ModelUsed         = modelName,
            ChunksRetrieved   = topChunks.Count,
        };
    }

    // ── Qdrant search ─────────────────────────────────────────────────────────

    private async Task<List<ScoredPoint>> SearchQdrantAsync(float[] embedding, CancellationToken ct)
    {
        try
        {
            // Geographic pre-filter: only retrieve Colorado documents
            var filter = new Filter();
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key   = "state",
                    Match = new Match { Keyword = "CO" }
                }
            });

            var results = await _qdrant.SearchAsync(
                collectionName:  CollectionName,
                vector:          new ReadOnlyMemory<float>(embedding),
                filter:          filter,
                limit:           (ulong)DenseTopK,
                payloadSelector: true,
                cancellationToken: ct);

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant search failed — returning empty results");
            return new List<ScoredPoint>();
        }
    }

    // ── Statewide aggregate context ───────────────────────────────────────────

    /// <summary>
    /// Queries h3_cells for statewide summary stats and top high-risk regions.
    /// Returns a formatted string injected into the system prompt.
    /// </summary>
    private async Task<string> GetAggregateContextAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var scored = db.H3Cells.AsNoTracking().Where(c => c.CurrentRiskScore.HasValue);

            // Statewide scalar summary
            var summary = await scored
                .GroupBy(_ => true)
                .Select(g => new
                {
                    TotalCells    = g.Count(),
                    AvgRisk       = g.Average(c => c.CurrentRiskScore),
                    MaxRisk       = g.Max(c => c.CurrentRiskScore),
                    RedFlagCount  = g.Count(c => c.RedFlagWarning),
                    SmokeCount    = g.Count(c => c.SmokePresent),
                    AvgWind       = g.Average(c => c.WindSpeedMph),
                    AvgRh         = g.Average(c => c.RelativeHumidityPct),
                })
                .FirstOrDefaultAsync(ct);

            if (summary == null) return string.Empty;

            // Top 5 highest-risk cells
            var topCells = await scored
                .OrderByDescending(c => c.CurrentRiskScore)
                .Select(c => new
                {
                    c.H3Index,
                    c.CurrentRiskScore,
                    c.WindSpeedMph,
                    c.RelativeHumidityPct,
                    c.RedFlagWarning,
                })
                .Take(5)
                .ToListAsync(ct);

            var sb = new StringBuilder();
            sb.AppendLine($"Scored cells: {summary.TotalCells:N0}");
            sb.AppendLine($"Statewide average risk score: {summary.AvgRisk:F2}/10");
            sb.AppendLine($"Highest single-cell risk score: {summary.MaxRisk:F2}/10");
            sb.AppendLine($"Cells under Red Flag Warning: {summary.RedFlagCount:N0} of {summary.TotalCells:N0} ({(summary.TotalCells > 0 ? 100.0 * summary.RedFlagCount / summary.TotalCells : 0):F0}%)");
            if (summary.SmokeCount > 0)
                sb.AppendLine($"Cells with smoke detected: {summary.SmokeCount:N0}");
            if (summary.AvgWind.HasValue)
                sb.AppendLine($"Statewide average wind speed: {summary.AvgWind:F1} mph");
            if (summary.AvgRh.HasValue)
                sb.AppendLine($"Statewide average relative humidity: {summary.AvgRh:F1}%");

            if (topCells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Top highest-risk H3 cells (current scoring):");
                foreach (var c in topCells)
                {
                    string cat = c.CurrentRiskScore.HasValue ? GetRiskCategory(c.CurrentRiskScore.Value) : "Unknown";
                    string rfFlag = c.RedFlagWarning ? " [RED FLAG]" : "";
                    sb.AppendLine($"  {c.H3Index}: {c.CurrentRiskScore:F2}/10 ({cat}){rfFlag}" +
                                  (c.WindSpeedMph.HasValue ? $" Wind {c.WindSpeedMph} mph" : "") +
                                  (c.RelativeHumidityPct.HasValue ? $" RH {c.RelativeHumidityPct}%" : ""));
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build aggregate context");
            return string.Empty;
        }
    }

    // ── Live NIFC incident fetch ───────────────────────────────────────────────

    /// <summary>
    /// Fetches currently active Colorado wildland fire incidents from the NIFC ArcGIS API.
    /// Returns a formatted string ready for prompt injection. Empty string if no incidents or on failure.
    /// </summary>
    private async Task<string> GetLiveIncidentsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonElement>(NifcQueryUrl, ct);
            if (!json.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < features.GetArrayLength(); i++)
            {
                var attrs = features[i].GetProperty("attributes");
                string name = GetNifcStr(attrs, "IncidentName") ?? "Unknown Fire";
                double? acres = GetNifcDouble(attrs, "DailyAcres");
                double? pct   = GetNifcDouble(attrs, "PercentContained");
                long?   discovered = GetNifcLong(attrs, "FireDiscoveryDateTime");

                string discoveredStr = discovered.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(discovered.Value).UtcDateTime.ToString("yyyy-MM-dd")
                    : "unknown date";

                sb.Append($"  • {name}: discovered {discoveredStr}");
                if (acres.HasValue) sb.Append($", {acres:N0} acres");
                if (pct.HasValue)   sb.Append($", {pct:N0}% contained");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NIFC live incident fetch failed (non-fatal)");
            return string.Empty;
        }
    }

    private static string? GetNifcStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetNifcDouble(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? GetNifcLong(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    // ── Keyword ranking ───────────────────────────────────────────────────────

    /// <summary>
    /// BM25-inspired keyword scoring over retrieved chunks.
    /// Scores each point by term-frequency overlap between the query and chunk text.
    /// Returns points sorted by keyword relevance (descending).
    /// </summary>
    private static List<ScoredPoint> KeywordRank(List<ScoredPoint> points, string queryText)
    {
        if (points.Count == 0) return points;

        var queryTokens = Tokenize(queryText);
        if (queryTokens.Length == 0) return points;

        return points
            .Select(p => (point: p, kScore: BM25Score(queryTokens, GetPayloadText(p))))
            .OrderByDescending(x => x.kScore)
            .Select(x => x.point)
            .ToList();
    }

    private static float BM25Score(string[] queryTokens, string docText)
    {
        const float k1 = 1.5f, b = 0.75f;
        const float avgDocLen = 350f; // approximate tokens per chunk

        string[] docTokens = Tokenize(docText);
        if (docTokens.Length == 0) return 0;

        var tf = docTokens.GroupBy(t => t).ToDictionary(g => g.Key, g => (float)g.Count());

        float score = 0;
        foreach (var term in queryTokens.Distinct())
        {
            if (!tf.TryGetValue(term, out float termFreq)) continue;
            // Simplified IDF = 1.0 (no corpus statistics available at query time)
            float tfNorm = termFreq * (k1 + 1) / (termFreq + k1 * (1 - b + b * docTokens.Length / avgDocLen));
            score += tfNorm;
        }
        return score;
    }

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        // Lowercase, split on non-alpha, remove stopwords and short tokens
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', '!', '?', ';', ':', '"', '\'' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !Stopwords.Contains(t))
            .ToArray();
    }

    // ── RRF merge ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reciprocal Rank Fusion of two ranked lists.
    /// RRF(d) = Σ 1 / (rank_in_list + k)
    /// k=60 is the standard value from the original RRF paper (Cormack et al. 2009).
    /// </summary>
    private static List<ScoredPoint> ReciprocalRankFusion(
        List<ScoredPoint> denseRanked,
        List<ScoredPoint> keywordRanked)
    {
        var scores = new Dictionary<string, (ScoredPoint point, double score)>(StringComparer.Ordinal);

        void AddRankList(List<ScoredPoint> list)
        {
            for (int rank = 0; rank < list.Count; rank++)
            {
                string id = list[rank].Id.Uuid;
                double rrf = 1.0 / (rank + 1 + RrfK);
                if (scores.TryGetValue(id, out var existing))
                    scores[id] = (existing.point, existing.score + rrf);
                else
                    scores[id] = (list[rank], rrf);
            }
        }

        AddRankList(denseRanked);
        AddRankList(keywordRanked);

        return scores.Values
            .OrderByDescending(x => x.score)
            .Select(x => x.point)
            .ToList();
    }

    // ── LLM call ──────────────────────────────────────────────────────────────

    private async Task<string> CallLlamaAsync(
        string systemPrompt, string userQuestion, string modelName, CancellationToken ct)
    {
        string baseUrl = _config["Ollama:BaseUrl"] ?? "http://localhost:11434";

        var requestBody = new
        {
            model    = modelName,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userQuestion },
            },
            stream  = false,
            options = new { temperature = 0.1, num_predict = 600 },
        };

        string json = JsonSerializer.Serialize(requestBody);
        using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
        using var http    = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        var response = await http.PostAsync($"{baseUrl}/api/chat", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: ct);
        return result?.Message?.Content?.Trim()
            ?? "No response generated. Check that llama3.2 is available in Ollama.";
    }

    // ── Prompt building ───────────────────────────────────────────────────────

    private static string BuildSystemPrompt(
        H3Cell? cell,
        string retrievedContext,
        string aggregateContext,
        string liveIncidents)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a Colorado wildfire risk analyst providing answers grounded solely in the data below.");
        sb.AppendLine("RULES:");
        sb.AppendLine("  1. ONLY use facts explicitly present in the sections below. Do NOT draw on training knowledge.");
        sb.AppendLine("  2. If the data does not contain enough information to answer precisely, say exactly what IS known and what is NOT available in the current dataset.");
        sb.AppendLine("  3. Always cite specific numbers (scores, wind speed, humidity, acreage, containment) from the data.");
        sb.AppendLine("  4. Keep your response under 300 words. Lead with the direct answer, then supporting data.");
        sb.AppendLine("  5. Never fabricate fire names, dates, or acreage not present below.");
        sb.AppendLine();

        // ── Selected cell ──
        if (cell != null)
        {
            sb.AppendLine("== SELECTED CELL CONDITIONS ==");
            sb.AppendLine($"H3 Cell: {cell.H3Index}");
            sb.AppendLine($"Risk Score: {cell.CurrentRiskScore?.ToString("F2") ?? "not yet scored"}/10" +
                          (cell.CurrentRiskScore.HasValue
                              ? $" ({GetRiskCategory(cell.CurrentRiskScore.Value)})"
                              : ""));
            sb.AppendLine($"Historical fires in last 20 years (MTBS): {cell.FiresLast20yr}");
            sb.AppendLine($"Total acres burned historically: {cell.TotalAcresBurned:N0}");
            if (cell.AvgBurnSeverity.HasValue)
                sb.AppendLine($"Average burn severity (dNBR): {cell.AvgBurnSeverity:F0}");
            if (cell.YearsSinceLastFire.HasValue)
                sb.AppendLine($"Years since last fire: {cell.YearsSinceLastFire}");
            else
                sb.AppendLine("Years since last fire: no historical fire recorded in this cell");
            sb.AppendLine();
            sb.AppendLine("== CURRENT WEATHER (this cell) ==");
            if (cell.WindSpeedMph.HasValue)       sb.AppendLine($"Wind speed: {cell.WindSpeedMph} mph");
            if (cell.RelativeHumidityPct.HasValue) sb.AppendLine($"Relative humidity: {cell.RelativeHumidityPct}%");
            if (cell.FuelMoisturePct.HasValue)     sb.AppendLine($"Fuel moisture (1-hr): {cell.FuelMoisturePct}%");
            if (cell.DroughtIndex.HasValue)        sb.AppendLine($"Drought index (PDSI): {cell.DroughtIndex:F1}");
            if (cell.DaysSinceRain.HasValue)       sb.AppendLine($"Days since rain: {cell.DaysSinceRain}");
            sb.AppendLine($"Red Flag Warning active: {(cell.RedFlagWarning ? "YES" : "No")}");
            sb.AppendLine($"Weather source: {cell.WeatherSource}");
        }

        // ── Statewide aggregate ──
        if (!string.IsNullOrWhiteSpace(aggregateContext))
        {
            sb.AppendLine();
            sb.AppendLine("== STATEWIDE CONDITIONS (all scored cells) ==");
            sb.AppendLine(aggregateContext);
        }

        // ── Live active incidents ──
        if (!string.IsNullOrWhiteSpace(liveIncidents))
        {
            sb.AppendLine();
            sb.AppendLine("== ACTIVE COLORADO INCIDENTS (NIFC, live) ==");
            sb.AppendLine(liveIncidents);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("== ACTIVE INCIDENTS ==");
            sb.AppendLine("No active wildfire incidents reported by NIFC for Colorado at this time.");
        }

        // ── Retrieved documents ──
        if (!string.IsNullOrWhiteSpace(retrievedContext))
        {
            sb.AppendLine();
            sb.AppendLine("== RETRIEVED INCIDENT REPORTS (Qdrant) ==");
            sb.AppendLine(retrievedContext);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("== RETRIEVED INCIDENT REPORTS ==");
            sb.AppendLine("No historical incident documents retrieved. Do not substitute training knowledge for missing documents.");
        }

        return sb.ToString();
    }

    private static string BuildContext(List<ScoredPoint> chunks)
    {
        if (chunks.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            string title = GetPayloadString(chunk, "document_title");
            string text  = GetPayloadText(chunk);
            string url   = GetPayloadString(chunk, "source_url");

            sb.AppendLine($"[{i + 1}] {title}");
            sb.AppendLine($"Source: {url}");
            sb.AppendLine($"Relevance: {chunk.Score:F2}");
            sb.AppendLine(text.Length > 600 ? text[..600] + "…" : text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── Response assembly ─────────────────────────────────────────────────────

    private static SourceDocument BuildSourceDocument(ScoredPoint p) => new()
    {
        ChunkId       = GetPayloadString(p, "chunk_id"),
        DocumentTitle = GetPayloadString(p, "document_title"),
        Excerpt       = TruncateExcerpt(GetPayloadText(p), 200),
        Similarity    = p.Score,
        SourceUrl     = GetPayloadString(p, "source_url"),
    };

    private static CellStats BuildCellStats(H3Cell cell) => new()
    {
        H3Index           = cell.H3Index,
        RiskScore         = cell.CurrentRiskScore,
        RiskCategory      = cell.CurrentRiskScore.HasValue
                            ? GetRiskCategory(cell.CurrentRiskScore.Value)
                            : "Unknown",
        FiresLast20yr     = cell.FiresLast20yr,
        TotalAcresBurned  = cell.TotalAcresBurned,
        AvgBurnSeverity   = cell.AvgBurnSeverity,
        YearsSinceLastFire = cell.YearsSinceLastFire,
    };

    private static CurrentConditions BuildCurrentConditions(H3Cell cell) => new()
    {
        WindSpeedMph        = cell.WindSpeedMph,
        RelativeHumidityPct = cell.RelativeHumidityPct,
        FuelMoisturePct     = cell.FuelMoisturePct,
        DroughtIndex        = cell.DroughtIndex,
        DaysSinceRain       = cell.DaysSinceRain,
        RedFlagWarning      = cell.RedFlagWarning,
        ForecastSummary     = BuildForecastSummary(cell),
        DataSource          = cell.WeatherSource == "RAWS"
                              ? "MesoWest/Synoptic RAWS Station"
                              : "NOAA Weather.gov",
        RetrievedAt         = cell.RiskScoreUpdatedAt ?? DateTimeOffset.UtcNow,
    };

    private static string BuildForecastSummary(H3Cell cell)
    {
        var parts = new List<string>();
        if (cell.WindSpeedMph.HasValue)        parts.Add($"Wind {cell.WindSpeedMph:F0} mph");
        if (cell.RelativeHumidityPct.HasValue) parts.Add($"RH {cell.RelativeHumidityPct:F0}%");
        if (cell.FuelMoisturePct.HasValue)     parts.Add($"fuel moisture {cell.FuelMoisturePct:F0}%");
        if (cell.RedFlagWarning)               parts.Add("Red Flag Warning active");
        return parts.Count > 0 ? string.Join(", ", parts) : "Conditions data not yet available.";
    }

    private static QueryResponse BuildMinimalResponse(H3Cell? cell, long ms, string errorCode) => new()
    {
        Answer           = $"Unable to process query ({errorCode}). Please try again.",
        Sources          = new(),
        CellStats        = cell != null ? BuildCellStats(cell) : null,
        CurrentConditions = cell != null ? BuildCurrentConditions(cell) : null,
        ProcessingMs     = ms,
        ModelUsed        = "none",
        ChunksRetrieved  = 0,
    };

    // ── Cell resolution ───────────────────────────────────────────────────────

    private async Task<H3Cell?> ResolveCellAsync(QueryRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (!string.IsNullOrWhiteSpace(request.H3Index))
        {
            return await db.H3Cells.AsNoTracking()
                .FirstOrDefaultAsync(c => c.H3Index == request.H3Index, ct);
        }

        if (request.Location != null)
        {
            // Compute H3 index from lat/lon using pocketken.H3 v4
            // NTS Coordinate: X=lon, Y=lat — LatLng.FromCoordinate reads them accordingly
            var latLng = LatLng.FromCoordinate(new Coordinate(request.Location.Lon, request.Location.Lat));
            string h3Str = H3Index.FromLatLng(latLng, request.Resolution).ToString();

            return await db.H3Cells.AsNoTracking()
                .FirstOrDefaultAsync(c => c.H3Index == h3Str && c.Resolution == request.Resolution, ct);
        }

        return null;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string GetPayloadText(ScoredPoint p)
        => p.Payload.TryGetValue("text", out var v) ? v.StringValue : "";

    private static string GetPayloadString(ScoredPoint p, string key)
        => p.Payload.TryGetValue(key, out var v) ? v.StringValue : "";

    private static string TruncateExcerpt(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen].TrimEnd() + "…";

    private static string GetRiskCategory(decimal score) => score switch
    {
        < 2.0m => "Very Low",
        < 4.0m => "Low",
        < 6.0m => "Moderate",
        < 8.0m => "High",
        < 9.0m => "Very High",
        _      => "Extreme"
    };

    // English stopwords — excluded from keyword scoring
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "has", "had", "not", "but", "this", "that",
        "from", "with", "have", "they", "been", "were", "will", "can", "its", "all",
        "due", "per", "via", "use", "used", "also", "been", "their", "there", "into",
        "more", "than", "when", "where", "which", "who", "what", "how", "about",
        "after", "over", "under", "fire", "fires", "area", "areas", "colorado"
    };

    // ── Private types ─────────────────────────────────────────────────────────

    private record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaMessage? Message,
        [property: JsonPropertyName("done")]    bool Done
    );

    private record OllamaMessage(
        [property: JsonPropertyName("role")]    string Role,
        [property: JsonPropertyName("content")] string Content
    );
}
