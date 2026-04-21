using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace CoWildfireApi.Ingestion;

/// <summary>
/// Seeds <c>state_boundaries</c> and <c>co_counties</c> from Census TIGER/Line shapefiles.
/// Idempotent — skips work if the tables are already populated.
///
/// TIGER/Line files use NAD83 (EPSG:4269); we reproject to WGS84 (EPSG:4326) the same
/// way <see cref="MtbsIngester"/> does.
///
/// Expected files (configurable via <c>Ingestion:TigerStateShp</c> / <c>Ingestion:TigerCountyShp</c>):
///   data/tiger/tl_2023_us_state.shp   — national state polygons
///   data/tiger/tl_2023_us_county.shp  — national county polygons; filtered to STATEFP='08'
///
/// Download from https://www2.census.gov/geo/tiger/TIGER2023/STATE/ and .../COUNTY/.
/// If files are missing we log a warning and skip — OriginClassifierService will return
/// UNKNOWN for all classifications until seeded.
/// </summary>
public class TigerSeeder
{
    private const string Nad83Wkt =
        "GEOGCS[\"NAD83\",DATUM[\"North_American_Datum_1983\"," +
        "SPHEROID[\"GRS 1980\",6378137,298.257222101]]," +
        "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";

    private const string Wgs84Wkt =
        "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\"," +
        "SPHEROID[\"WGS 84\",6378137,298.257223563]]," +
        "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";

    private const string ColoradoFips = "08";

    private static readonly GeometryFactory GeoFactory = new(new PrecisionModel(), 4326);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TigerSeeder> _logger;

    public TigerSeeder(
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration config,
        ILogger<TigerSeeder> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    public async Task SeedIfEmptyAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        bool hasStates   = await db.StateBoundaries.AnyAsync(ct);
        bool hasCounties = await db.CoCounties.AnyAsync(ct);

        if (hasStates && hasCounties)
        {
            _logger.LogInformation("TIGER seeder: state_boundaries + co_counties already populated — skipping");
            return;
        }

        var transform = BuildTransform();

        if (!hasStates)
        {
            string path = _config["Ingestion:TigerStateShp"] ?? "data/tiger/tl_2023_us_state.shp";
            if (!File.Exists(path))
            {
                _logger.LogWarning(
                    "TIGER state shapefile not found at {Path} — skipping state_boundaries seed. " +
                    "Download tl_2023_us_state.zip from https://www2.census.gov/geo/tiger/TIGER2023/STATE/",
                    path);
            }
            else
            {
                int loaded = await LoadStatesAsync(db, path, transform, ct);
                _logger.LogInformation("TIGER seeder: loaded {Count} state boundaries", loaded);
            }
        }

        if (!hasCounties)
        {
            string path = _config["Ingestion:TigerCountyShp"] ?? "data/tiger/tl_2023_us_county.shp";
            if (!File.Exists(path))
            {
                _logger.LogWarning(
                    "TIGER county shapefile not found at {Path} — skipping co_counties seed. " +
                    "Download tl_2023_us_county.zip from https://www2.census.gov/geo/tiger/TIGER2023/COUNTY/",
                    path);
            }
            else
            {
                int loaded = await LoadColoradoCountiesAsync(db, path, transform, ct);
                _logger.LogInformation("TIGER seeder: loaded {Count} Colorado counties", loaded);
            }
        }
    }

    private static MathTransform BuildTransform()
    {
        var csFactory = new CoordinateSystemFactory();
        var ctFactory = new CoordinateTransformationFactory();
        var nad83 = csFactory.CreateFromWkt(Nad83Wkt);
        var wgs84 = csFactory.CreateFromWkt(Wgs84Wkt);
        return ctFactory.CreateFromCoordinateSystems(nad83, wgs84).MathTransform;
    }

    private async Task<int> LoadStatesAsync(
        AppDbContext db, string shapePath, MathTransform transform, CancellationToken ct)
    {
        string datasetKey = $"TIGER_STATE|{Path.GetFileName(shapePath)}|{new FileInfo(shapePath).Length}";
        var log = new IngestionLog { Source = "TIGER_STATE", DatasetKey = datasetKey, Status = "pending" };
        db.IngestionLogs.Add(log);
        await db.SaveChangesAsync(ct);

        try
        {
            using var reader = new ShapefileDataReader(shapePath, GeoFactory);
            var header = reader.DbaseHeader;
            int idxFips = IndexOf(header, "STATEFP");
            int idxAbbr = IndexOf(header, "STUSPS");
            int idxName = IndexOf(header, "NAME");

            int count = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var geom = reader.Geometry;
                if (geom == null || geom.IsEmpty) continue;

                string fips = reader.GetString(idxFips)?.Trim() ?? "";
                string abbr = reader.GetString(idxAbbr)?.Trim() ?? "";
                string name = reader.GetString(idxName)?.Trim() ?? "";
                if (fips.Length == 0 || abbr.Length == 0) continue;

                // Skip territories — keep only the 50 states + DC (FIPS <= 56)
                if (int.TryParse(fips, out int f) && f > 56) continue;

                var mp = ToMultiPolygon(Reproject(geom, transform));
                if (mp == null) continue;

                db.StateBoundaries.Add(new StateBoundary
                {
                    StateFips = fips,
                    StateAbbr = abbr,
                    StateName = name,
                    Boundary  = mp,
                });
                count++;
            }

            await db.SaveChangesAsync(ct);

            log.Status = "success";
            log.RecordsLoaded = count;
            log.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return count;
        }
        catch (Exception ex)
        {
            log.Status = "failed";
            log.ErrorMessage = ex.Message;
            log.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<int> LoadColoradoCountiesAsync(
        AppDbContext db, string shapePath, MathTransform transform, CancellationToken ct)
    {
        string datasetKey = $"TIGER_COUNTY|{Path.GetFileName(shapePath)}|{new FileInfo(shapePath).Length}";
        var log = new IngestionLog { Source = "TIGER_COUNTY", DatasetKey = datasetKey, Status = "pending" };
        db.IngestionLogs.Add(log);
        await db.SaveChangesAsync(ct);

        try
        {
            using var reader = new ShapefileDataReader(shapePath, GeoFactory);
            var header = reader.DbaseHeader;
            int idxFipsSt = IndexOf(header, "STATEFP");
            int idxGeoid  = IndexOf(header, "GEOID");
            int idxName   = IndexOf(header, "NAME");

            int count = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                string stFips = reader.GetString(idxFipsSt)?.Trim() ?? "";
                if (stFips != ColoradoFips) continue;

                var geom = reader.Geometry;
                if (geom == null || geom.IsEmpty) continue;

                string geoid = reader.GetString(idxGeoid)?.Trim() ?? "";
                string name  = reader.GetString(idxName)?.Trim() ?? "";

                var mp = ToMultiPolygon(Reproject(geom, transform));
                if (mp == null) continue;

                db.CoCounties.Add(new CoCounty
                {
                    CountyFips = geoid,
                    CountyName = name,
                    StateFips  = ColoradoFips,
                    Boundary   = mp,
                });
                count++;
            }

            await db.SaveChangesAsync(ct);

            log.Status = "success";
            log.RecordsLoaded = count;
            log.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return count;
        }
        catch (Exception ex)
        {
            log.Status = "failed";
            log.ErrorMessage = ex.Message;
            log.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private static MultiPolygon? ToMultiPolygon(Geometry g) => g switch
    {
        MultiPolygon mp => mp,
        Polygon p       => GeoFactory.CreateMultiPolygon(new[] { p }),
        _               => null,
    };

    private static Geometry Reproject(Geometry g, MathTransform transform)
    {
        var copy = (Geometry)g.Copy();
        copy.Apply(new CoordinateTransformFilter(transform));
        copy.GeometryChanged();
        copy.SRID = 4326;
        return copy;
    }

    private static int IndexOf(DbaseFileHeader header, string fieldName)
    {
        for (int i = 0; i < header.NumFields; i++)
            if (string.Equals(header.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        return -1;
    }

    private class CoordinateTransformFilter : ICoordinateSequenceFilter
    {
        private readonly MathTransform _transform;
        public CoordinateTransformFilter(MathTransform t) => _transform = t;
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
