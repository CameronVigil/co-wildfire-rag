using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace CoWildfireApi.Ingestion;

/// <summary>
/// Ingests MTBS Burned Area Boundaries Shapefile into fire_events and computes
/// H3 cell intersection metrics on h3_cells.
///
/// MTBS Shapefile uses NAD83 (EPSG:4269). All geometry is reprojected to WGS84
/// (EPSG:4326) via ProjNet before PostGIS insert.
///
/// Download the Shapefile from: https://www.mtbs.gov/direct-download
/// Place mtbs_perimeter_data.shp (and sibling files) in the path configured by
/// Ingestion:MtbsShapefilePath in appsettings.
///
/// Idempotent: checks ingestion_log before processing. Re-running is safe.
/// </summary>
public class MtbsIngester
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<MtbsIngester> _logger;

    // Colorado bounding box for pre-filter
    private const double CoWest  = -109.06;
    private const double CoSouth =   36.99;
    private const double CoEast  = -102.04;
    private const double CoNorth =   41.00;

    // NAD83 WKT (EPSG:4269)
    private const string Nad83Wkt =
        "GEOGCS[\"NAD83\",DATUM[\"North_American_Datum_1983\"," +
        "SPHEROID[\"GRS 1980\",6378137,298.257222101]]," +
        "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";

    // WGS84 WKT (EPSG:4326)
    private const string Wgs84Wkt =
        "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\"," +
        "SPHEROID[\"WGS 84\",6378137,298.257223563]]," +
        "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";

    private static readonly GeometryFactory GeoFactory = new(new PrecisionModel(), 4326);

    public MtbsIngester(
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration config,
        ILogger<MtbsIngester> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    public async Task IngestAsync(CancellationToken ct = default)
    {
        var shapePath = _config["Ingestion:MtbsShapefilePath"]
            ?? "data/mtbs/mtbs_perimeter_data.shp";

        if (!File.Exists(shapePath))
        {
            _logger.LogWarning("MTBS Shapefile not found at {Path}. " +
                "Download from https://www.mtbs.gov/direct-download and extract to that path.",
                shapePath);
            return;
        }

        var datasetKey = Path.GetFileName(shapePath) + "_" + new FileInfo(shapePath).Length;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Idempotency check
        var existing = await db.IngestionLogs
            .FirstOrDefaultAsync(l => l.Source == "MTBS" && l.DatasetKey == datasetKey, ct);
        if (existing?.Status == "success")
        {
            _logger.LogInformation("MTBS dataset {Key} already ingested — skipping", datasetKey);
            return;
        }

        // Insert/update pending log entry
        var logEntry = existing ?? new IngestionLog { Source = "MTBS", DatasetKey = datasetKey };
        logEntry.Status = "pending";
        logEntry.StartedAt = DateTimeOffset.UtcNow;
        logEntry.ErrorMessage = null;
        if (existing == null) db.IngestionLogs.Add(logEntry);
        await db.SaveChangesAsync(ct);

        try
        {
            var count = await IngestShapefileAsync(db, shapePath, ct);
            await UpdateH3CellMetricsAsync(db, ct);

            logEntry.Status = "success";
            logEntry.RecordsLoaded = count;
            logEntry.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("MTBS ingestion complete: {Count} fire events loaded", count);
        }
        catch (Exception ex)
        {
            logEntry.Status = "failed";
            logEntry.ErrorMessage = ex.Message;
            logEntry.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogError(ex, "MTBS ingestion failed");
            throw;
        }
    }

    private async Task<int> IngestShapefileAsync(AppDbContext db, string shapePath, CancellationToken ct)
    {
        // Build NAD83 → WGS84 transform
        var csFactory = new CoordinateSystemFactory();
        var ctFactory = new CoordinateTransformationFactory();
        var nad83 = csFactory.CreateFromWkt(Nad83Wkt);
        var wgs84 = csFactory.CreateFromWkt(Wgs84Wkt);
        var transform = ctFactory.CreateFromCoordinateSystems(nad83, wgs84);
        var mathTransform = transform.MathTransform;

        int count = 0;
        using var reader = new ShapefileDataReader(shapePath, GeoFactory);
        var header = reader.DbaseHeader;

        // Map field indices (mtbs_perims_DD schema)
        int idxFireId   = IndexOf(header, "event_id");
        int idxFireName = IndexOf(header, "incid_name");
        int idxIgDate   = IndexOf(header, "ig_date");
        int idxAcres    = IndexOf(header, "burnbndac");

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var geom = reader.Geometry;
            if (geom == null || geom.IsEmpty) continue;

            // Bounding box pre-filter
            var env = geom.EnvelopeInternal;
            if (env.MinX > CoEast || env.MaxX < CoWest ||
                env.MinY > CoNorth || env.MaxY < CoSouth)
                continue;

            // Read fields
            string fireId    = reader.GetString(idxFireId)?.Trim() ?? Guid.NewGuid().ToString("N");
            string fireName  = reader.GetString(idxFireName)?.Trim() ?? "Unknown";
            string igDateStr = reader.IsDBNull(idxIgDate) ? "" : reader.GetString(idxIgDate)?.Trim() ?? "";
            decimal acres    = reader.IsDBNull(idxAcres) ? 0m : Convert.ToDecimal(reader.GetValue(idxAcres));

            DateOnly? startDate = DateOnly.TryParse(igDateStr, out var d) ? d : null;
            short year = startDate.HasValue ? (short)startDate.Value.Year : (short)0;

            // Reproject NAD83 → WGS84
            var reprojected = ReprojectGeometry(geom, mathTransform);
            if (reprojected is not MultiPolygon mp)
            {
                if (reprojected is Polygon p)
                    mp = GeoFactory.CreateMultiPolygon(new[] { p });
                else
                    continue;
            }

            // Skip if outside Colorado after reprojection
            if (!ColoRadoBbox().Intersects(mp.EnvelopeInternal)) continue;

            var fireEvent = new FireEvent
            {
                FireId    = fireId,
                FireName  = fireName,
                Year      = year,
                StartDate = startDate,
                AcresBurned = acres,
                Source    = "MTBS",
                State     = "CO",
                Perimeter = mp,
            };

            // ON CONFLICT DO NOTHING via try/catch on unique violation
            try
            {
                db.FireEvents.Add(fireEvent);
                await db.SaveChangesAsync(ct);
                count++;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                db.ChangeTracker.Clear();
                _logger.LogDebug("Skipping duplicate fire_id: {Id}", fireId);
            }
        }

        return count;
    }

    private async Task UpdateH3CellMetricsAsync(AppDbContext db, CancellationToken ct)
    {
        _logger.LogInformation("Computing fire-to-H3 intersections...");

        // Batch intersections using raw SQL for performance
        await db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO fire_event_h3_intersections (fire_event_id, h3_cell_id, overlap_pct)
            SELECT
                fe.id,
                h.id,
                ROUND(CAST(
                    ST_Area(ST_Intersection(fe.perimeter::geometry, h.boundary::geometry))
                    / NULLIF(ST_Area(h.boundary::geometry), 0) * 100
                AS numeric), 2)
            FROM fire_events fe
            JOIN h3_cells h ON ST_Intersects(fe.perimeter, h.boundary)
            ON CONFLICT DO NOTHING
        ", ct);

        _logger.LogInformation("Updating h3_cells fire history aggregates...");

        int currentYear = DateTime.UtcNow.Year;

        await db.Database.ExecuteSqlRawAsync($@"
            UPDATE h3_cells h
            SET
                fires_last_20yr       = agg.fire_count,
                total_acres_burned    = agg.total_acres,
                avg_burn_severity     = agg.avg_dnbr,
                last_fire_year        = agg.max_year,
                years_since_last_fire = {currentYear} - agg.max_year,
                updated_at            = NOW()
            FROM (
                SELECT
                    i.h3_cell_id,
                    COUNT(DISTINCT fe.id)::smallint       AS fire_count,
                    COALESCE(SUM(fe.acres_burned), 0)     AS total_acres,
                    AVG(fe.avg_dnbr)                      AS avg_dnbr,
                    MAX(fe.year)::smallint                 AS max_year
                FROM fire_event_h3_intersections i
                JOIN fire_events fe ON fe.id = i.fire_event_id
                WHERE fe.year >= {currentYear - 20}
                GROUP BY i.h3_cell_id
            ) agg
            WHERE h.id = agg.h3_cell_id
        ", ct);

        _logger.LogInformation("H3 cell fire history metrics updated");
    }

    private static Geometry ReprojectGeometry(Geometry geom, ProjNet.CoordinateSystems.Transformations.MathTransform transform)
    {
        var reprojected = (Geometry)geom.Copy();
        reprojected.Apply(new CoordinateTransformFilter(transform));
        reprojected.GeometryChanged();
        return reprojected;
    }

    private static int IndexOf(DbaseFileHeader header, string fieldName)
    {
        for (int i = 0; i < header.NumFields; i++)
            if (string.Equals(header.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                return i + 1; // ShapefileDataReader field access is 1-based
        return -1;
    }

    private static Envelope ColoRadoBbox() =>
        new(CoWest, CoEast, CoSouth, CoNorth);

    private class CoordinateTransformFilter : ICoordinateSequenceFilter
    {
        private readonly ProjNet.CoordinateSystems.Transformations.MathTransform _transform;

        public CoordinateTransformFilter(ProjNet.CoordinateSystems.Transformations.MathTransform transform)
            => _transform = transform;

        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            double x = seq.GetX(i);
            double y = seq.GetY(i);
            double[] pt = _transform.Transform(new[] { x, y });
            seq.SetX(i, pt[0]);
            seq.SetY(i, pt[1]);
        }
    }
}
