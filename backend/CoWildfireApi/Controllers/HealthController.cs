using CoWildfireApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Qdrant.Client;

namespace CoWildfireApi.Controllers;

[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly QdrantClient _qdrant;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;

    public HealthController(
        AppDbContext db,
        QdrantClient qdrant,
        IConfiguration config,
        IHttpClientFactory httpFactory)
    {
        _db       = db;
        _qdrant   = qdrant;
        _config   = config;
        _httpFactory = httpFactory;
    }

    [HttpGet("health")]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
    {
        var postgres = await CheckPostgresAsync(ct);
        var qdrant   = await CheckQdrantAsync(ct);
        var ollama   = await CheckOllamaAsync(ct);

        bool allHealthy = postgres == "healthy" && qdrant == "healthy";
        int statusCode  = allHealthy ? 200 : 503;

        return StatusCode(statusCode, new
        {
            status    = allHealthy ? "healthy" : "degraded",
            timestamp = DateTimeOffset.UtcNow,
            dependencies = new
            {
                postgres,
                qdrant,
                ollama,
            },
            version = "1.0.0-phase1"
        });
    }

    private async Task<string> CheckPostgresAsync(CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return "healthy";
        }
        catch { return "unhealthy"; }
    }

    private async Task<string> CheckQdrantAsync(CancellationToken ct)
    {
        try
        {
            await _qdrant.ListCollectionsAsync(ct);
            return "healthy";
        }
        catch { return "unhealthy"; }
    }

    private async Task<string> CheckOllamaAsync(CancellationToken ct)
    {
        try
        {
            var ollamaBase = _config["Ollama:BaseUrl"] ?? "http://localhost:11434";
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var resp = await client.GetAsync($"{ollamaBase}/api/tags", ct);
            return resp.IsSuccessStatusCode ? "healthy" : "unhealthy";
        }
        catch { return "unreachable"; }
    }
}
