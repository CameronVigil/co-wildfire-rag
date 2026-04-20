using CoWildfireApi.Data;
using CoWildfireApi.Models;
using H3;
using H3.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoWildfireApi.Services;

/// <summary>
/// RAG (Retrieval-Augmented Generation) query pipeline for the Colorado Wildfire Analyzer.
///
/// Pipeline per query:
///   1. Load H3 cell stats + current conditions from h3_cells (geographic context)
///   2. Embed the user's question with EmbeddingService (nomic-embed-text)
///   3. Dense vector search in Qdrant "wildfire_docs" collection (top-20, CO state filter)
///   4. Keyword re-rank retrieved documents with a BM25-inspired scorer
///   5. RRF (Reciprocal Rank Fusion) merge of semantic + keyword rank lists
///   6. Take top-5 chunks as context
///   7. Build structured system prompt with cell stats, conditions, and retrieved context
///   8. Call llama3.2 via Ollama chat API
///   9. Return structured QueryResponse matching the API spec
///
/// The keyword re-ranking (step 4) supplements dense search for fire-name lookups
/// like "Cameron Peak Fire" that pure semantic search may miss.
///
/// Note: Ollama llama3.2 must be running locally. Fails gracefully if unavailable.
/// </summary>
public class RagService
{
    private const string CollectionName = "wildfire_docs";
    private const int    DenseTopK      = 20;  // retrieve this many before re-ranking
    private const int    FinalTopK      = 5;   // pass this many to the LLM
    private const int    RrfK           = 60;  // RRF constant (standard value)

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly QdrantClient     _qdrant;
    private readonly EmbeddingService _embed;
    private readonly IConfiguration   _config;
    private readonly ILogger<RagService> _logger;

    public RagService(
        IDbContextFactory<AppDbContext> dbFactory,
        QdrantClient qdrant,
        EmbeddingService embed,
        IConfiguration config,
        ILogger<RagService> logger)
    {
        _dbFactory = dbFactory;
        _qdrant    = qdrant;
        _embed     = embed;
        _config    = config;
        _logger    = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Resolve H3 cell
        var cell = await ResolveCellAsync(request, ct);

        // 2. Embed the question
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

        // 3. Dense vector search in Qdrant
        var denseResults = await SearchQdrantAsync(queryEmbedding, ct);

        // 4. Keyword re-rank
        var keywordRanked = KeywordRank(denseResults, request.Question);

        // 5. RRF merge of dense + keyword rank lists
        var merged = ReciprocalRankFusion(denseResults, keywordRanked);

        // 6. Take top-5
        var topChunks = merged.Take(FinalTopK).ToList();

        // 7. Build prompt
        string context       = BuildContext(topChunks);
        string systemPrompt  = BuildSystemPrompt(cell, context);
        string modelName     = _config["Ollama:ChatModel"] ?? "llama3.2";

        // 8. Call LLM
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

        // 9. Assemble response
        return new QueryResponse
        {
            Answer         = answer,
            Sources        = topChunks.Select(BuildSourceDocument).ToList(),
            CellStats      = cell != null ? BuildCellStats(cell) : null,
            CurrentConditions = cell != null ? BuildCurrentConditions(cell) : null,
            ProcessingMs   = sw.ElapsedMilliseconds,
            ModelUsed      = modelName,
            ChunksRetrieved = topChunks.Count,
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

    private static string BuildSystemPrompt(H3Cell? cell, string retrievedContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Colorado wildfire risk analyst. Answer questions accurately " +
                      "based on the data below. Cite specific fire names, years, and conditions " +
                      "where available. Be concise and actionable. Keep your response under 350 words.");
        sb.AppendLine();

        if (cell != null)
        {
            sb.AppendLine("== CURRENT CELL CONDITIONS ==");
            sb.AppendLine($"H3 Cell: {cell.H3Index} (Resolution {cell.Resolution})");
            sb.AppendLine($"Risk Score: {cell.CurrentRiskScore?.ToString("F2") ?? "not yet scored"}/10" +
                          (cell.CurrentRiskScore.HasValue
                              ? $" ({GetRiskCategory(cell.CurrentRiskScore.Value)})"
                              : ""));
            sb.AppendLine($"Fires in last 20 years: {cell.FiresLast20yr}");
            sb.AppendLine($"Total acres burned: {cell.TotalAcresBurned:N0}");
            if (cell.AvgBurnSeverity.HasValue)
                sb.AppendLine($"Avg burn severity (dNBR): {cell.AvgBurnSeverity:F0}");
            if (cell.YearsSinceLastFire.HasValue)
                sb.AppendLine($"Years since last fire: {cell.YearsSinceLastFire}");
            sb.AppendLine();
            sb.AppendLine("== WEATHER CONDITIONS ==");
            if (cell.WindSpeedMph.HasValue)
                sb.AppendLine($"Wind speed: {cell.WindSpeedMph} mph");
            if (cell.RelativeHumidityPct.HasValue)
                sb.AppendLine($"Relative humidity: {cell.RelativeHumidityPct}%");
            if (cell.FuelMoisturePct.HasValue)
                sb.AppendLine($"Fuel moisture (1-hr): {cell.FuelMoisturePct}%");
            if (cell.DroughtIndex.HasValue)
                sb.AppendLine($"Drought index (PDSI): {cell.DroughtIndex:F1}");
            if (cell.DaysSinceRain.HasValue)
                sb.AppendLine($"Days since rain: {cell.DaysSinceRain}");
            sb.AppendLine($"Red Flag Warning: {(cell.RedFlagWarning ? "YES — extreme fire danger" : "No")}");
            sb.AppendLine($"Weather data source: {cell.WeatherSource}");
        }
        else
        {
            sb.AppendLine("== NOTE ==");
            sb.AppendLine("No specific cell selected. Providing general Colorado wildfire context.");
        }

        if (!string.IsNullOrWhiteSpace(retrievedContext))
        {
            sb.AppendLine();
            sb.AppendLine("== RETRIEVED INCIDENT REPORTS & HISTORICAL RECORDS ==");
            sb.AppendLine(retrievedContext);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("== NOTE ==");
            sb.AppendLine("No incident report documents found for this query. " +
                          "Answer based on the cell conditions above and general wildfire knowledge.");
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
