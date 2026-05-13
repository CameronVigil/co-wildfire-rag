using CoWildfireApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoWildfireApi.Controllers;

[ApiController]
[Route("api/county-bounds")]
public class CountyBoundsController : ControllerBase
{
    private readonly AppDbContext _db;

    public CountyBoundsController(AppDbContext db) => _db = db;

    [HttpGet]
    [ResponseCache(Duration = 86400)] // county borders don't change
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var counties = await _db.CoCounties
            .AsNoTracking()
            .Select(c => new
            {
                c.Boundary,
                c.CountyName,
                c.CountyFips,
            })
            .ToListAsync(ct);

        var features = counties.Select(c => new
        {
            type       = "Feature",
            geometry   = c.Boundary,
            properties = new { name = c.CountyName, fips = c.CountyFips },
        });

        return Ok(new { type = "FeatureCollection", features });
    }
}
