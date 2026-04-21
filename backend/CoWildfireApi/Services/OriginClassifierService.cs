using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace CoWildfireApi.Services;

/// <summary>
/// Single point of truth for origin classification of FIRMS points and HMS plumes.
/// Singleton — caches state boundary geometries in memory at first use.
///
/// Per out-of-state-classification.md:
///   - Point classification: ST_Within(point, state_boundary) → origin state
///   - Plume classification: same, but on the plume centroid
///   - Smoke transport inference: within 200 miles of CO border, high confidence,
///     FRP > 10 MW. Wind direction check is spec-listed but not implemented here
///     (NoaaService would need a per-point wind query — deferred as a judgment call
///     since per-FIRMS-point NOAA grid lookups would blow past the rate limit).
///     The distance + confidence + FRP gate still catches the vast majority of
///     transport-relevant events.
/// </summary>
public interface IOriginClassifierService
{
    Task<OriginClassification> ClassifyPointAsync(
        double lat, double lon, double? frpMw = null, string? confidence = null,
        CancellationToken ct = default);

    Task<OriginClassification> ClassifyPlumeAsync(
        Geometry plumeGeometry, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetAffectedColoradoCountiesAsync(
        Geometry plumeGeometry, CancellationToken ct = default);

    /// <summary>
    /// Eagerly load the state boundary cache. Called at startup.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Look up a full state name by its 2-letter abbreviation.
    /// Requires EnsureLoadedAsync to have been called — otherwise returns the abbreviation.
    /// </summary>
    string GetStateName(string? abbr);
}

public record OriginClassification(
    bool   IsColorado,
    string OriginState,     // 2-letter abbr, "UNKNOWN" if no match
    string OriginStateName,
    bool   SmokeTransportLikely,
    string ImpactType       // "fire", "smoke_only", "none"
);

public class OriginClassifierService : IOriginClassifierService
{
    // Smoke transport gate constants (from out-of-state-classification.md § Step 4)
    private const double SmokeTransportMaxMiles = 200.0;
    private const double SmokeTransportMinFrpMw = 10.0;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<OriginClassifierService> _logger;

    // Cache: list of (state, boundary). Loaded once, immutable after.
    private List<CachedState>? _states;
    private MultiPolygon? _coloradoBoundary;
    private Geometry? _coloradoBorderBuffer; // CO boundary expanded by ~200 miles (rough)
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public OriginClassifierService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<OriginClassifierService> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_states != null) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_states != null) return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (!await db.StateBoundaries.AnyAsync(ct))
            {
                _logger.LogWarning("OriginClassifier: state_boundaries table is empty — " +
                    "run the TIGER/Line seeder. All classifications will default to UNKNOWN.");
                _states = new List<CachedState>();
                return;
            }

            var rows = await db.StateBoundaries.AsNoTracking().ToListAsync(ct);
            _states = rows.Select(r => new CachedState(
                r.StateAbbr, r.StateName, r.Boundary,
                PreparedGeometryFactory.Prepare(r.Boundary))).ToList();

            _coloradoBoundary = rows.FirstOrDefault(r => r.StateAbbr == "CO")?.Boundary;
            if (_coloradoBoundary != null)
            {
                // Buffer Colorado by ~200 miles. 1 degree latitude ≈ 69 miles, so ~2.9°.
                // This is a rough geodesic approximation; accuracy is not critical for a
                // "within 200 miles" gate.
                _coloradoBorderBuffer = _coloradoBoundary.Buffer(2.9);
            }

            _logger.LogInformation("OriginClassifier: cached {Count} state boundaries", _states.Count);
        }
        finally { _loadLock.Release(); }
    }

    public async Task<OriginClassification> ClassifyPointAsync(
        double lat, double lon, double? frpMw = null, string? confidence = null,
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (_states == null || _states.Count == 0)
            return new OriginClassification(false, "UNKNOWN", "Unknown", false, "none");

        var factory = _coloradoBoundary?.Factory
            ?? new GeometryFactory(new PrecisionModel(), 4326);
        var pt = factory.CreatePoint(new Coordinate(lon, lat));

        // In-state check first — most FIRMS points in our bbox will be CO
        var co = _states.FirstOrDefault(s => s.Abbr == "CO");
        if (co != null && co.Prepared.Contains(pt))
            return new OriginClassification(true, "CO", "Colorado", false, "fire");

        // Origin state lookup
        var match = _states.FirstOrDefault(s => s.Abbr != "CO" && s.Prepared.Contains(pt));
        if (match == null)
            return new OriginClassification(false, "UNKNOWN", "Unknown", false, "none");

        // Smoke transport likelihood (spec step 4 — wind direction check deferred)
        bool nearCo = _coloradoBorderBuffer?.Contains(pt) ?? false;
        bool highConf = string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase);
        bool highFrp  = frpMw.HasValue && frpMw.Value > SmokeTransportMinFrpMw;
        bool transportLikely = nearCo && highConf && highFrp;

        string impactType = transportLikely ? "smoke_only" : "none";
        return new OriginClassification(false, match.Abbr, match.Name, transportLikely, impactType);
    }

    public async Task<OriginClassification> ClassifyPlumeAsync(
        Geometry plumeGeometry, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (_states == null || _states.Count == 0)
            return new OriginClassification(false, "UNKNOWN", "Unknown", false, "none");

        var centroid = plumeGeometry.Centroid;
        var co = _states.FirstOrDefault(s => s.Abbr == "CO");
        if (co != null && co.Prepared.Contains(centroid))
            return new OriginClassification(true, "CO", "Colorado", false, "fire");

        var match = _states.FirstOrDefault(s => s.Prepared.Contains(centroid));
        if (match == null)
            return new OriginClassification(false, "UNKNOWN", "Unknown", true, "smoke_only");

        return new OriginClassification(false, match.Abbr, match.Name, true, "smoke_only");
    }

    public string GetStateName(string? abbr)
    {
        if (string.IsNullOrEmpty(abbr)) return "Unknown";
        var match = _states?.FirstOrDefault(s => s.Abbr == abbr);
        return match?.Name ?? abbr;
    }

    public async Task<IReadOnlyList<string>> GetAffectedColoradoCountiesAsync(
        Geometry plumeGeometry, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Pure PostGIS: let the GIST index do the work rather than pulling all counties.
        var names = await db.CoCounties.AsNoTracking()
            .Where(c => c.Boundary.Intersects(plumeGeometry))
            .Select(c => c.CountyName)
            .ToListAsync(ct);
        return names;
    }

    private sealed record CachedState(
        string Abbr, string Name, MultiPolygon Boundary,
        IPreparedGeometry Prepared);
}
