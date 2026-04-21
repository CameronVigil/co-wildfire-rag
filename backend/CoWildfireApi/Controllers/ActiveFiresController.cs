using CoWildfireApi.Data;
using CoWildfireApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoWildfireApi.Controllers;

/// <summary>
/// GET /api/active-fires — most-recent NASA FIRMS detections, split by origin.
/// Out-of-state detections render on a separate purple layer on the frontend and
/// MUST include the spec-mandated disclaimer text.
/// </summary>
[ApiController]
[Route("api")]
public class ActiveFiresController : ControllerBase
{
    // Disclaimer required by out-of-state-classification.md for every OOS detection.
    private const string OutOfStateDisclaimer =
        "Out-of-state fire detection. Classification is informational only and does " +
        "NOT affect Colorado risk scores.";

    private readonly AppDbContext _db;
    private readonly IOriginClassifierService _classifier;

    public ActiveFiresController(AppDbContext db, IOriginClassifierService classifier)
    {
        _db = db;
        _classifier = classifier;
    }

    [HttpGet("active-fires")]
    public async Task<IActionResult> Get([FromQuery] int hoursBack = 24, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 72));

        var rows = await _db.ActiveFireDetections.AsNoTracking()
            .Where(d => d.AcquiredAt >= cutoff)
            .OrderByDescending(d => d.AcquiredAt)
            .ToListAsync(ct);

        await _classifier.EnsureLoadedAsync(ct);

        var inState = rows.Where(r => r.IsColorado)
            .Select(r => new
            {
                id = r.Id,
                latitude = r.Latitude,
                longitude = r.Longitude,
                brightness = r.Brightness,
                frp = r.Frp,
                confidence = r.Confidence,
                satellite = r.Satellite,
                acquiredAt = r.AcquiredAt,
                dayNight = r.DayNight,
            })
            .ToList();

        var outOfState = rows.Where(r => !r.IsColorado)
            .Select(r => new
            {
                id = r.Id,
                latitude = r.Latitude,
                longitude = r.Longitude,
                frp = r.Frp,
                confidence = r.Confidence,
                satellite = r.Satellite,
                acquiredAt = r.AcquiredAt,
                originState = r.OriginState,
                originStateName = _classifier.GetStateName(r.OriginState),
                impactType = r.ImpactType,
                disclaimer = OutOfStateDisclaimer,
            })
            .ToList();

        return Ok(new
        {
            retrievedAt = DateTimeOffset.UtcNow,
            windowHours = Math.Clamp(hoursBack, 1, 72),
            inState,
            outOfState,
        });
    }
}
