using CoWildfireApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace CoWildfireApi.Controllers;

/// <summary>
/// GET /api/smoke-plumes — latest NOAA HMS smoke plume polygons intersecting Colorado.
/// Returns a GeoJSON FeatureCollection so the frontend can add it directly as a MapLibre source.
/// </summary>
[ApiController]
[Route("api")]
public class SmokePlumesController : ControllerBase
{
    private readonly AppDbContext _db;

    public SmokePlumesController(AppDbContext db) => _db = db;

    [HttpGet("smoke-plumes")]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date = null, CancellationToken ct = default)
    {
        DateOnly target;
        if (date.HasValue)
        {
            target = date.Value;
        }
        else
        {
            target = await _db.SmokeEvents.AsNoTracking()
                .OrderByDescending(e => e.PlumeDate)
                .Select(e => e.PlumeDate)
                .FirstOrDefaultAsync(ct);

            if (target == default)
                return Ok(new { type = "FeatureCollection", features = Array.Empty<object>(), plumeDate = (DateOnly?)null });
        }

        var rows = await _db.SmokeEvents.AsNoTracking()
            .Where(e => e.PlumeDate == target)
            .ToListAsync(ct);

        var fc = new FeatureCollection();
        foreach (var row in rows)
        {
            var attrs = new AttributesTable
            {
                { "id",                row.Id },
                { "plumeDate",         row.PlumeDate.ToString("yyyy-MM-dd") },
                { "density",           row.Density },
                { "originState",       row.OriginState ?? "UNKNOWN" },
                { "originStateName",   row.OriginStateName ?? "Unknown" },
                { "isColoradoOrigin",  row.IsColoradoOrigin },
                { "coloradoCountiesAffected", row.ColoradoCountiesAffected },
                { "smokeDescription",  row.SmokeDescription ?? "" },
                { "source",            row.Source },
            };
            fc.Add(new Feature(row.Plume, attrs));
        }

        var writer = new GeoJsonWriter();
        string json = writer.Write(fc);

        return Content(json, "application/geo+json");
    }
}
