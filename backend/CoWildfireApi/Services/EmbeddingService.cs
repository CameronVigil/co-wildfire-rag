namespace CoWildfireApi.Services;

/// <summary>
/// Phase 3 stub — embedding service wrapping Ollama nomic-embed-text (768-dim).
/// Used by InciwebIngester and RagService to embed text chunks and queries.
/// </summary>
public class EmbeddingService
{
    private readonly ILogger<EmbeddingService> _logger;
    public EmbeddingService(ILogger<EmbeddingService> logger) => _logger = logger;
}
