using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoWildfireApi.Services;

/// <summary>
/// Generates 768-dimensional text embeddings using Ollama's nomic-embed-text model.
///
/// Ollama REST API:
///   POST {baseUrl}/api/embeddings
///   {"model": "nomic-embed-text", "prompt": "text to embed"}
///   → {"embedding": [float, ...]}   (768-dim for nomic-embed-text)
///
/// nomic-embed-text is optimized for retrieval tasks and outperforms OpenAI ada-002 on MTEB.
/// Requires Ollama running locally with: ollama pull nomic-embed-text
///
/// Used by: InciwebIngester (document chunks) and RagService (query).
/// </summary>
public class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<EmbeddingService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public EmbeddingService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<EmbeddingService> logger)
    {
        _http   = httpFactory.CreateClient();
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Returns a 768-dim embedding for the given text.
    /// Throws InvalidOperationException if Ollama is unreachable or returns an error.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[768];

        string baseUrl    = _config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        string modelName  = _config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

        var requestBody = new { model = modelName, prompt = text };
        var json        = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _http.PostAsync($"{baseUrl}/api/embeddings", content, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(
                    cancellationToken: ct);

                if (result?.Embedding == null || result.Embedding.Length == 0)
                    throw new InvalidOperationException("Ollama returned empty embedding");

                return result.Embedding;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Embedding attempt {Attempt}/{Max} failed, retrying...", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        throw new InvalidOperationException($"Failed to obtain embedding after {maxAttempts} attempts");
    }

    // ── Private types ──────────────────────────────────────────────────────────

    private record OllamaEmbeddingResponse(
        [property: JsonPropertyName("embedding")] float[]? Embedding
    );
}
