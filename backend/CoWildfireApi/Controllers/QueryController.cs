using CoWildfireApi.Ingestion;
using CoWildfireApi.Models;
using CoWildfireApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CoWildfireApi.Controllers;

/// <summary>
/// POST /api/query  — RAG-powered wildfire question answering
/// POST /api/query/ingest — trigger InciWeb ingestion run (dev/admin use)
/// </summary>
[ApiController]
[Route("api/query")]
public class QueryController : ControllerBase
{
    private readonly RagService           _rag;
    private readonly InciwebIngester      _ingester;
    private readonly ILogger<QueryController> _logger;

    public QueryController(
        RagService rag,
        InciwebIngester ingester,
        ILogger<QueryController> logger)
    {
        _rag      = rag;
        _ingester = ingester;
        _logger   = logger;
    }

    /// <summary>
    /// Ask a natural language question about Colorado wildfire risk.
    /// Optionally provide a location (lat/lon or H3 index) to include real-time cell stats.
    /// </summary>
    /// <remarks>
    /// Example request:
    ///
    ///     POST /api/query
    ///     {
    ///       "question": "What is the fire risk near Fort Collins?",
    ///       "location": { "lat": 40.585, "lon": -105.084 },
    ///       "resolution": 6
    ///     }
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(QueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QueryResponse>> Query(
        [FromBody] QueryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        if (request.Question.Length > 1000)
            return BadRequest(new { error = "Question must be 1000 characters or fewer." });

        _logger.LogInformation("RAG query: {Question}", request.Question);

        var response = await _rag.QueryAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Trigger an InciWeb ingestion run. Idempotent — already-ingested incidents are skipped.
    /// Intended for development/admin use; add auth middleware before exposing publicly.
    /// </summary>
    [HttpPost("ingest")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerIngest(CancellationToken ct)
    {
        // Fire-and-forget (ingestion is a long-running I/O operation)
        _ = Task.Run(async () =>
        {
            try   { await _ingester.IngestAsync(ct); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background InciWeb ingestion failed");
            }
        }, ct);

        return Accepted(new { message = "InciWeb ingestion started in background." });
    }
}
