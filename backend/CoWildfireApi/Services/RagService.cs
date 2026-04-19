namespace CoWildfireApi.Services;

/// <summary>
/// Phase 3 stub — RAG query engine.
/// Will implement: embed question → geographic pre-filter → Qdrant hybrid BM25+vector search
/// → re-rank with RRF → build context → prompt llama3.2 → return structured response.
/// Collection: wildfire_docs (768-dim nomic-embed-text, cosine, sparse vectors for BM25).
/// </summary>
public class RagService
{
    private readonly ILogger<RagService> _logger;
    public RagService(ILogger<RagService> logger) => _logger = logger;
}
