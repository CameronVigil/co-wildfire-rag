using CoWildfireApi.Data;
using CoWildfireApi.Models;
using H3;
using H3.Algorithms;
using H3.Extensions;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Services;

/// <summary>
/// Generates the H3 hexagonal grid covering Colorado and seeds h3_cells.
/// Run once on startup when h3_cells is empty.
///
/// Colorado bounding box: west=-109.06, south=36.99, east=-102.04, north=41.00
///
/// pocketken.H3 v4 is NTS-native:
///   - Polyfill.Fill(NTS Geometry, resolution) → IEnumerable&lt;H3Index&gt;
///   - H3GeometryExtensions.GetCellBoundary(cell, geoFactory) → Polygon (NTS, [lng,lat])
///   - H3Index.ToLatLng() → LatLng; then .ToCoordinate() → NTS Coordinate (X=lng, Y=lat)
/// </summary>
public class H3GridService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<H3GridService> _logger;

    private static readonly GeometryFactory GeoFactory = new(new PrecisionModel(), 4326);

    // Colorado bounding box as a closed ring polygon (NTS: X=lng, Y=lat)
    private static readonly Geometry ColoradoBbox = GeoFactory.CreatePolygon(new[]
    {
        new Coordinate(-109.06, 36.99),
        new Coordinate(-102.04, 36.99),
        new Coordinate(-102.04, 41.00),
        new Coordinate(-109.06, 41.00),
        new Coordinate(-109.06, 36.99),  // close the ring
    });

    public H3GridService(IDbContextFactory<AppDbContext> dbFactory, ILogger<H3GridService> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public async Task SeedGridIfEmptyAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.H3Cells.AnyAsync(ct))
        {
            _logger.LogInformation("H3 grid already seeded — skipping");
            return;
        }

        _logger.LogInformation("Seeding H3 grid for Colorado (res 6 + 8)...");
        await SeedResolutionAsync(db, 6, ct);
        await SeedResolutionAsync(db, 8, ct);
        _logger.LogInformation("H3 grid seeding complete");
    }

    private async Task SeedResolutionAsync(AppDbContext db, int resolution, CancellationToken ct)
    {
        // pocketken.H3 v4: Polyfill.Fill takes an NTS Geometry
        var cells = Polyfill.Fill(ColoradoBbox, resolution).ToList();
        _logger.LogInformation("Resolution {Res}: {Count} cells to seed", resolution, cells.Count);

        var batch = new List<H3Cell>(100);
        int saved = 0;

        foreach (var cell in cells)
        {
            batch.Add(BuildH3Cell(cell, resolution));

            if (batch.Count >= 100)
            {
                await db.H3Cells.AddRangeAsync(batch, ct);
                await db.SaveChangesAsync(ct);
                saved += batch.Count;
                batch.Clear();
                if (saved % 500 == 0)
                    _logger.LogInformation("  {Saved}/{Total} res-{Res} cells saved",
                        saved, cells.Count, resolution);
            }
        }

        if (batch.Count > 0)
        {
            await db.H3Cells.AddRangeAsync(batch, ct);
            await db.SaveChangesAsync(ct);
            saved += batch.Count;
        }

        _logger.LogInformation("Resolution {Res}: seeded {Count} cells", resolution, saved);
    }

    private static H3Cell BuildH3Cell(H3Index cell, int resolution)
    {
        // GetCellBoundary returns an NTS Polygon in [lng, lat] order (GeoJSON-correct)
        var boundary = cell.GetCellBoundary(GeoFactory);

        // ToLatLng() center; .ToCoordinate() → NTS Coordinate where X=lng, Y=lat
        var center = cell.ToLatLng().ToCoordinate();

        return new H3Cell
        {
            H3Index          = cell.ToString(),
            Resolution       = (short)resolution,
            CenterLat        = (decimal)center.Y,   // NTS: Y = latitude
            CenterLon        = (decimal)center.X,   // NTS: X = longitude
            Boundary         = boundary,
            FiresLast20yr    = 0,
            TotalAcresBurned = 0,
            SmokePresent     = false,
            SmokeInferred    = false,
            RedFlagWarning   = false,
            WeatherSource    = "NOAA",
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        };
    }
}
