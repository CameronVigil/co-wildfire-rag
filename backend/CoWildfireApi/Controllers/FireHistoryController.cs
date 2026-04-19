using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using System.Text.Json;

namespace CoWildfireApi.Controllers;

[ApiController]
[Route("api")]
public class FireHistoryController : ControllerBase
{
    private readonly AppDbContext _db;

    public FireHistoryController(AppDbContext db) => _db = db;

    /// <summary>
    /// GET /api/fire-history
    /// Returns historical fire perimeters as GeoJSON FeatureCollection.
    /// Query params: bounds (west,south,east,north), startYear, endYear, minAcres, source
    /// </summary>
    [HttpGet("fire-history")]
    public async Task<IActionResult> GetFireHistory(
        [FromQuery] string? bounds    = null,
        [FromQuery] int  startYear  = 1984,
        [FromQuery] int? endYear    = null,
        [FromQuery] double minAcres = 0,
        [FromQuery] string? source  = null,
        CancellationToken ct = default)
    {
        int end = endYear ?? DateTime.UtcNow.Year;

        var query = _db.FireEvents.AsNoTracking()
            .Where(f => f.Year >= startYear && f.Year <= end);

        if (minAcres > 0)
            query = query.Where(f => f.AcresBurned >= (decimal)minAcres);

        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(f => f.Source == source.ToUpper());

        if (!string.IsNullOrWhiteSpace(bounds))
        {
            var bbox = ParseBbox(bounds);
            if (bbox != null)
            {
                var factory = new GeometryFactory(new PrecisionModel(), 4326);
                var bboxGeom = factory.ToGeometry(bbox);
                query = query.Where(f => f.Perimeter != null &&
                    f.Perimeter.Intersects(bboxGeom));
            }
        }

        var fires = await query.ToListAsync(ct);

        var features = fires
            .Where(f => f.Perimeter != null)
            .Select(f => BuildFeature(f))
            .ToList();

        var sources = fires.Select(f => f.Source).Distinct().Order().ToList();
        int? minYear = fires.Any() ? fires.Min(f => (int?)f.Year) : null;
        int? maxYear = fires.Any() ? fires.Max(f => (int?)f.Year) : null;

        var result = new
        {
            type = "FeatureCollection",
            metadata = new
            {
                totalFires       = fires.Count,
                totalAcresBurned = fires.Sum(f => f.AcresBurned ?? 0),
                yearRange        = minYear.HasValue ? new[] { minYear.Value, maxYear!.Value } : Array.Empty<int>(),
                source           = sources
            },
            features
        };

        return new JsonResult(result, GeoJsonSerializerOptions());
    }

    private static object BuildFeature(FireEvent f) => new
    {
        type     = "Feature",
        geometry = f.Perimeter,
        properties = new
        {
            fireId       = f.FireId,
            fireName     = f.FireName,
            year         = f.Year,
            startDate    = f.StartDate?.ToString("yyyy-MM-dd"),
            endDate      = f.EndDate?.ToString("yyyy-MM-dd"),
            acresBurned  = f.AcresBurned,
            avgDnbr      = f.AvgDnbr,
            maxDnbr      = f.MaxDnbr,
            source       = f.Source,
            county       = f.County,
            state        = f.State,
        }
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
