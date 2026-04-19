using CoWildfireApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using System.Text.Json;

namespace CoWildfireApi.Controllers;

/// <summary>
/// GET /api/risk-grid   — H3 hex cells with risk scores as GeoJSON FeatureCollection
/// GET /api/cell-at-point — H3 cell properties for a lat/lon point
/// GET /api/risk-history/{h3Index} — Hourly risk score history
///
/// NOTE: risk fill layer uses type=fill (polygon), NOT type=heatmap (point).
/// Cache-Control: public, max-age=300 on risk-grid responses.
/// </summary>
[ApiController]
[Route("api")]
public class RiskController : ControllerBase
{
    private readonly AppDbContext _db;

    public RiskController(AppDbContext db) => _db = db;

    [HttpGet("risk-grid")]
    public async Task<IActionResult> GetRiskGrid(
        [FromQuery] int    resolution = 6,
        [FromQuery] string? bounds   = null,
        [FromQuery] double minRisk   = 0,
        CancellationToken ct = default)
    {
        var query = _db.H3Cells.AsNoTracking()
            .Where(c => c.Resolution == resolution);

        if (minRisk > 0)
            query = query.Where(c => c.CurrentRiskScore >= (decimal)minRisk);

        if (!string.IsNullOrWhiteSpace(bounds))
        {
            var bbox = ParseBbox(bounds);
            if (bbox != null)
                query = query.Where(c =>
                    c.CenterLon >= (decimal)bbox.MinX && c.CenterLon <= (decimal)bbox.MaxX &&
                    c.CenterLat >= (decimal)bbox.MinY && c.CenterLat <= (decimal)bbox.MaxY);
        }


        var cells = await query.ToListAsync(ct);

        var maxUpdated = cells.Max(c => c.RiskScoreUpdatedAt);

        // ETag based on latest score update time
        var etag = $"\"{maxUpdated?.Ticks ?? 0}\"";
        if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm == etag)
            return StatusCode(304);

        Response.Headers.Append("ETag", etag);
        Response.Headers.Append("Cache-Control", "public, max-age=300");

        var features = cells.Where(c => c.Boundary != null).Select(c => new
        {
            type     = "Feature",
            geometry = c.Boundary,
            properties = new
            {
                h3Index              = c.H3Index,
                resolution           = c.Resolution,
                centerLat            = c.CenterLat,
                centerLon            = c.CenterLon,
                riskScore            = c.CurrentRiskScore,
                riskCategory         = GetRiskCategory(c.CurrentRiskScore),
                redFlagWarning       = c.RedFlagWarning,
                firesLast20yr        = c.FiresLast20yr,
                totalAcresBurned     = c.TotalAcresBurned,
                avgBurnSeverity      = c.AvgBurnSeverity,
                yearsSinceLastFire   = c.YearsSinceLastFire,
                lastFireYear         = c.LastFireYear,
                windSpeedMph         = c.WindSpeedMph,
                relativeHumidityPct  = c.RelativeHumidityPct,
                fuelMoisturePct      = c.FuelMoisturePct,
                vegetationType       = c.VegetationType,
                slopeDegrees         = c.SlopeDegrees,
                riskScoreUpdatedAt   = c.RiskScoreUpdatedAt,
            }
        }).ToList();

        var result = new
        {
            type     = "FeatureCollection",
            metadata = new
            {
                resolution,
                cellCount          = features.Count,
                generatedAt        = DateTimeOffset.UtcNow,
                riskScoreUpdatedAt = maxUpdated,
            },
            features
        };

        return new JsonResult(result, GeoJsonSerializerOptions());
    }

    [HttpGet("cell-at-point")]
    public async Task<IActionResult> GetCellAtPoint(
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] int resolution = 6,
        CancellationToken ct = default)
    {
        // Find the H3 cell whose boundary contains this point
        var point = new GeometryFactory(new PrecisionModel(), 4326)
            .CreatePoint(new Coordinate(lon, lat));

        var cell = await _db.H3Cells.AsNoTracking()
            .Where(c => c.Resolution == resolution && c.Boundary != null &&
                c.Boundary.Contains(point))
            .FirstOrDefaultAsync(ct);

        if (cell == null)
            return NotFound(new { error = "No cell found at this location", code = "CELL_NOT_FOUND" });

        return Ok(new
        {
            h3Index              = cell.H3Index,
            resolution           = cell.Resolution,
            centerLat            = cell.CenterLat,
            centerLon            = cell.CenterLon,
            riskScore            = cell.CurrentRiskScore,
            riskCategory         = GetRiskCategory(cell.CurrentRiskScore),
            redFlagWarning       = cell.RedFlagWarning,
            firesLast20yr        = cell.FiresLast20yr,
            totalAcresBurned     = cell.TotalAcresBurned,
            avgBurnSeverity      = cell.AvgBurnSeverity,
            yearsSinceLastFire   = cell.YearsSinceLastFire,
            lastFireYear         = cell.LastFireYear,
            windSpeedMph         = cell.WindSpeedMph,
            relativeHumidityPct  = cell.RelativeHumidityPct,
            fuelMoisturePct      = cell.FuelMoisturePct,
            riskScoreUpdatedAt   = cell.RiskScoreUpdatedAt,
        });
    }

    [HttpGet("risk-history/{h3Index}")]
    public async Task<IActionResult> GetRiskHistory(
        string h3Index,
        [FromQuery] int hours = 168,
        CancellationToken ct = default)
    {
        hours = Math.Min(hours, 2160); // max 90 days
        var since = DateTimeOffset.UtcNow.AddHours(-hours);

        var history = await _db.H3RiskHistory.AsNoTracking()
            .Where(r => r.H3Index == h3Index && r.ScoredAt >= since)
            .OrderByDescending(r => r.ScoredAt)
            .ToListAsync(ct);

        if (!history.Any())
            return NotFound(new { error = "No history found for cell", code = "NO_HISTORY" });

        var resolution = history.First().Resolution;

        return Ok(new
        {
            h3Index,
            resolution,
            count      = history.Count,
            dataPoints = history.Select(r => new
            {
                scoredAt             = r.ScoredAt,
                riskScore            = r.RiskScore,
                riskCategory         = r.RiskCategory,
                windSpeedMph         = r.WindSpeedMph,
                relativeHumidityPct  = r.RelativeHumidityPct,
                fuelMoisturePct      = r.FuelMoisturePct,
                weatherSource        = r.WeatherSource,
            })
        });
    }

    private static string GetRiskCategory(decimal? score) => score switch
    {
        null          => "Unknown",
        < 2.0m        => "Very Low",
        < 4.0m        => "Low",
        < 6.0m        => "Moderate",
        < 8.0m        => "High",
        < 9.0m        => "Very High",
        _             => "Extreme"
    };

    private static Envelope? ParseBbox(string bounds)
    {
        var parts = bounds.Split(',');
        if (parts.Length != 4) return null;
        if (!double.TryParse(parts[0], out double w) ||
            !double.TryParse(parts[1], out double s) ||
            !double.TryParse(parts[2], out double e) ||
            !double.TryParse(parts[3], out double n)) return null;
        return new Envelope(w, e, s, n);
    }

    private static JsonSerializerOptions GeoJsonSerializerOptions()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        opts.Converters.Add(new GeoJsonConverterFactory());
        return opts;
    }
}
