using CoWildfireApi.Data;
using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;

namespace CoWildfireApi.Services;

/// <summary>
/// Computes hourly wildfire risk scores for all H3 resolution-6 cells covering Colorado.
///
/// Formula (from risk-model.md):
///   risk_score = 10 × weighted_sum(
///     normalize(wind_speed)            × 0.22,   [0–60 mph]
///     normalize(1 − relative_humidity) × 0.18,   [inverted, 0–100%]
///     normalize(1 − fuel_moisture)     × 0.18,   [inverted, 0–35%]
///     fire_history_score               × 0.12,   [composite, see below]
///     normalize(slope)                 × 0.09,   [0–45°, Phase 5 placeholder]
///     normalize(vegetation_flam)       × 0.09,   [0–1, Phase 5 placeholder]
///     normalize(drought_index)         × 0.08,   [PDSI –4 to +4]
///     normalize(days_since_rain)       × 0.04    [0–30 days]
///   )
///
/// Fire history component:
///   fire_history_score = normalize(fires_last_20yr) × 0.4
///                      + normalize(avg_dnbr / years_since_recovery) × 0.6
///
/// Weather source priority: RAWS observed (within 50km) → NOAA gridded forecast.
///
/// After scoring:
///   - Persists current_risk_score + weather snapshot to h3_cells
///   - Inserts a row into h3_risk_history for time-series tracking
///
/// Phase 5 placeholders (slope=15°, vegetation=0.6) applied until LANDFIRE is integrated.
/// </summary>
public class RiskScoringService
{
    // Phase 5 defaults per risk-model.md §Placeholder Until Phase 5
    private const double DefaultSlopeDegrees           = 15.0;
    private const double DefaultVegetationFlammability = 0.60;

    // Normalization caps per risk-model.md §Input Variable Normalization Ranges
    private const double MaxWindMph         = 60.0;
    private const double MaxFuelMoisturePct = 35.0;
    private const double MaxSlopeDegrees    = 45.0;
    private const double MaxDnbrSeverity    = 750.0; // MTBS high-severity dNBR threshold
    private const double MaxFiresLast20yr   = 10.0;
    private const double MaxDaysSinceRain   = 30.0;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly NoaaService    _noaa;
    private readonly RawsService    _raws;
    private readonly DroughtService _drought;
    private readonly FeedService    _feed;
    private readonly ILogger<RiskScoringService> _logger;

    public RiskScoringService(
        IDbContextFactory<AppDbContext> dbFactory,
        NoaaService noaa,
        RawsService raws,
        DroughtService drought,
        FeedService feed,
        ILogger<RiskScoringService> logger)
    {
        _dbFactory = dbFactory;
        _noaa      = noaa;
        _raws      = raws;
        _drought   = drought;
        _feed      = feed;
        _logger    = logger;
    }

    /// <summary>
    /// Scores all H3-6 cells. Called hourly by RiskScoringBackgroundService.
    ///
    /// Performance strategy (3 optimisations):
    ///   1. Gridpoint URL persistence — after the first run, DB-cached URLs eliminate
    ///      all ~6,867 /points calls on subsequent runs.
    ///   2. Forecast deduplication — cells that share the same NOAA gridpoint URL
    ///      (which is common; one NWS office covers many H3 cells) only trigger one
    ///      /forecast/hourly fetch instead of one per cell.
    ///   3. Semaphore raised from 5 → 20 — NOAA allows reasonable concurrent load;
    ///      20 concurrent calls is well within their guidelines.
    ///
    /// Together these take a 60+ minute run (13,734 serial-equivalent calls) down to
    /// roughly 1 minute or less.
    /// </summary>
    public async Task ScoreAllCellsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Risk scoring run starting");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Fetch shared state (both are cached after first call)
        double pdsi  = await _drought.GetColoradoPdsiAsync(ct);
        bool redFlag = await _noaa.IsRedFlagActiveAsync(ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cells = await db.H3Cells
            .Where(c => c.Resolution == 6)
            .ToListAsync(ct);

        _logger.LogInformation("Fetching weather for {Count} H3-6 cells (RAWS-first, NOAA fallback)", cells.Count);

        // ── Phase 1: resolve RAWS and gridpoint URLs concurrently ─────────────────
        // Semaphore caps concurrent calls at 20 (up from 5).
        using var semaphore = new SemaphoreSlim(20);

        // Resolve RAWS data and gridpoint URLs for all cells in parallel.
        var resolvedTasks = cells
            .Select(cell => ResolveRawsAndGridpointAsync(cell, semaphore, ct))
            .ToList();
        var resolved = await Task.WhenAll(resolvedTasks);
        // resolved[i] = (raws, gridpointUrl?) for cells[i]

        // ── Phase 2: deduplicate forecast fetches ─────────────────────────────────
        // Group cells whose RAWS data is incomplete (need NOAA) by their gridpoint URL
        // so each unique NOAA endpoint is only hit once.
        var urlFetchTasks = new Dictionary<string, Task<NoaaWeather?>>(); // url → in-flight task

        for (int i = 0; i < cells.Count; i++)
        {
            var (raws, gridpointUrl) = resolved[i];

            // If RAWS covers both wind + RH we don't need NOAA at all
            if (raws?.WindSpeedMph != null && raws.RelativeHumidityPct != null)
                continue;

            if (gridpointUrl == null)
                continue; // couldn't resolve gridpoint — will be skipped later

            if (!urlFetchTasks.ContainsKey(gridpointUrl))
            {
                // First cell to claim this URL — start the fetch
                urlFetchTasks[gridpointUrl] = FetchForecastWithSemaphoreAsync(
                    cells[i].H3Index, gridpointUrl, semaphore, ct);
            }
        }

        // Await all unique forecast fetches
        await Task.WhenAll(urlFetchTasks.Values);

        var urlToWeather = urlFetchTasks.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Result); // all tasks are completed at this point

        _logger.LogInformation(
            "NOAA forecasts: {Unique} unique gridpoint URL(s) fetched for {Total} cells needing NOAA",
            urlFetchTasks.Count, cells.Count);

        // ── Phase 3: score and persist ────────────────────────────────────────────
        var historyBatch = new List<H3RiskHistory>(cells.Count);
        int scored = 0, skipped = 0;

        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var (raws, gridpointUrl) = resolved[i];

            // Look up the shared forecast result (null if gridpointUrl was null or fetch failed)
            NoaaWeather? noaa = gridpointUrl != null && urlToWeather.TryGetValue(gridpointUrl, out var w) ? w : null;

            if (noaa == null && raws == null) { skipped++; continue; }

            double windMph, rhPct, daysSinceRain;
            double? fuelMoisturePct;
            string weatherSource;
            string? rawsStationId;
            decimal? rawsWindMph, rawsRhPct;

            if (raws?.WindSpeedMph != null && raws.RelativeHumidityPct != null)
            {
                // Full RAWS reading — primary source
                windMph         = raws.WindSpeedMph.Value;
                rhPct           = raws.RelativeHumidityPct.Value;
                fuelMoisturePct = raws.FuelMoisturePct;
                daysSinceRain   = EstimateDaysSinceRain(rhPct, windMph);
                weatherSource   = "RAWS";
                rawsStationId   = raws.StationId;
                rawsWindMph     = (decimal?)raws.WindSpeedMph;
                rawsRhPct       = (decimal?)raws.RelativeHumidityPct;
            }
            else if (noaa != null)
            {
                // NOAA fallback; use RAWS fuel moisture if available even when NOAA is primary
                windMph         = noaa.WindSpeedMph;
                rhPct           = noaa.RelativeHumidityPct;
                fuelMoisturePct = raws?.FuelMoisturePct;
                daysSinceRain   = PrecipProbToDaysSinceRain(noaa.PrecipitationProbabilityPct);
                weatherSource   = "NOAA";
                rawsStationId   = raws?.StationId;
                rawsWindMph     = (decimal?)raws?.WindSpeedMph;
                rawsRhPct       = (decimal?)raws?.RelativeHumidityPct;
            }
            else
            {
                skipped++;
                continue;
            }

            decimal score   = ComputeRiskScore(
                windMph, rhPct, fuelMoisturePct,
                cell.FiresLast20yr, (double?)cell.AvgBurnSeverity, cell.YearsSinceLastFire,
                (double?)cell.SlopeDegrees ?? DefaultSlopeDegrees,
                DefaultVegetationFlammability,
                pdsi, daysSinceRain);

            string category = GetRiskCategory(score);

            // Update cell — EF change tracker generates the UPDATE statements
            cell.CurrentRiskScore        = score;
            cell.RiskScoreUpdatedAt      = DateTimeOffset.UtcNow;
            cell.WindSpeedMph            = (decimal?)windMph;
            cell.RelativeHumidityPct     = (decimal?)rhPct;
            cell.FuelMoisturePct         = (decimal?)fuelMoisturePct;
            cell.DroughtIndex            = (decimal?)pdsi;
            cell.DaysSinceRain           = (short?)(int)Math.Min(MaxDaysSinceRain, daysSinceRain);
            cell.RedFlagWarning          = redFlag;
            cell.WeatherSource           = weatherSource;
            cell.RawsStationId           = rawsStationId;
            cell.RawsWindSpeedMph        = rawsWindMph;
            cell.RawsRelativeHumidityPct = rawsRhPct;
            cell.UpdatedAt               = DateTimeOffset.UtcNow;

            historyBatch.Add(new H3RiskHistory
            {
                H3Index             = cell.H3Index,
                Resolution          = cell.Resolution,
                RiskScore           = score,
                RiskCategory        = category,
                WindSpeedMph        = (decimal?)windMph,
                RelativeHumidityPct = (decimal?)rhPct,
                FuelMoisturePct     = (decimal?)fuelMoisturePct,
                DroughtIndex        = (decimal?)pdsi,
                WeatherSource       = weatherSource,
                ScoredAt            = DateTimeOffset.UtcNow,
            });

            scored++;
        }

        // Single SaveChanges: UPDATEs for h3_cells + INSERTs for h3_risk_history
        db.H3RiskHistory.AddRange(historyBatch);
        await db.SaveChangesAsync(ct);

        sw.Stop();
        _logger.LogInformation(
            "Risk scoring complete: {Scored} scored, {Skipped} skipped (no weather data) in {Elapsed}ms",
            scored, skipped, sw.ElapsedMilliseconds);

        await _feed.PublishAsync(new Models.LiveFeedEvent
        {
            Type     = "risk_score",
            Severity = redFlag ? "warning" : "info",
            Source   = "RiskScoringService",
            Detail   = $"Hourly risk scoring: {scored} cells scored, {skipped} skipped" +
                       (redFlag ? " (Red Flag Warning active)" : ""),
        }, ct);
    }

    // ── Formula ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pure risk score calculation — static and accessible for unit tests.
    /// All inputs normalized to [0,1] before weighting. Result scaled to [0,10].
    /// </summary>
    internal static decimal ComputeRiskScore(
        double  windMph,
        double  rhPct,
        double? fuelMoisturePct,
        short   firesLast20yr,
        double? avgDnbr,
        short?  yearsSinceLast,
        double  slopeDegrees,
        double  vegFlammability,
        double  pdsi,
        double  daysSinceRain)
    {
        double nWind    = Clamp(windMph, 0, MaxWindMph) / MaxWindMph;
        double nRhInv   = 1.0 - Clamp(rhPct, 0, 100) / 100.0;
        double nFmInv   = fuelMoisturePct.HasValue
                          ? 1.0 - Clamp(fuelMoisturePct.Value, 0, MaxFuelMoisturePct) / MaxFuelMoisturePct
                          : 0.50; // default: moderate fuel dryness when sensor data unavailable
        double nFire    = ComputeFireHistoryScore(firesLast20yr, avgDnbr, yearsSinceLast);
        double nSlope   = Clamp(slopeDegrees, 0, MaxSlopeDegrees) / MaxSlopeDegrees;
        double nVeg     = Clamp(vegFlammability, 0, 1);
        double nDrought = Clamp(pdsi + 4.0, 0, 8) / 8.0; // rescale [-4,+4] → [0,1]
        double nRain    = Clamp(daysSinceRain, 0, MaxDaysSinceRain) / MaxDaysSinceRain;

        double weighted =
            nWind    * 0.22 +
            nRhInv   * 0.18 +
            nFmInv   * 0.18 +
            nFire    * 0.12 +
            nSlope   * 0.09 +
            nVeg     * 0.09 +
            nDrought * 0.08 +
            nRain    * 0.04;

        return (decimal)Math.Round(Clamp(weighted * 10.0, 0, 10), 2);
    }

    /// <summary>
    /// Fire history composite score in [0,1].
    ///   = normalize(fires_last_20yr) × 0.4
    ///   + normalize(avg_dnbr / years_since_recovery) × 0.6
    ///
    /// avg_dnbr is the raw dNBR value from MTBS stored in h3_cells.avg_burn_severity.
    /// MaxDnbrSeverity (750) = high-severity threshold per MTBS classification.
    /// years_since_recovery is floored at 1 to avoid division by zero.
    /// </summary>
    private static double ComputeFireHistoryScore(short firesLast20yr, double? avgDnbr, short? yearsSinceLast)
    {
        double nFires = Clamp(firesLast20yr, 0, MaxFiresLast20yr) / MaxFiresLast20yr;

        double severity      = avgDnbr ?? 200.0; // default: low-moderate dNBR when no MTBS data
        double yearsRecovery = Math.Max(1, (int)(yearsSinceLast ?? 20));

        // Normalized so high-severity (750 dNBR) burned 1 year ago = 1.0
        double nSeverityRecovery = Clamp(severity / (yearsRecovery * MaxDnbrSeverity), 0, 1);

        return nFires * 0.4 + nSeverityRecovery * 0.6;
    }

    // ── Weather helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates days since rain from RH + wind when no precipitation sensor is available.
    /// </summary>
    private static double EstimateDaysSinceRain(double rhPct, double windMph)
    {
        if (rhPct < 10) return 25;
        if (rhPct < 20) return 18;
        if (rhPct < 30) return 12;
        if (rhPct < 45) return 7;
        if (rhPct < 60) return 3;
        return 1;
    }

    /// <summary>
    /// Converts NOAA precipitation probability (%) to estimated days since last rain.
    /// </summary>
    private static double PrecipProbToDaysSinceRain(double precipProbPct)
    {
        if (precipProbPct >= 70) return 0;
        if (precipProbPct >= 40) return 2;
        if (precipProbPct >= 20) return 7;
        if (precipProbPct >= 10) return 14;
        return 21;
    }

    // ── Risk category ──────────────────────────────────────────────────────────────

    internal static string GetRiskCategory(decimal score) => score switch
    {
        < 2.0m => "Very Low",
        < 4.0m => "Low",
        < 6.0m => "Moderate",
        < 8.0m => "High",
        < 9.0m => "Very High",
        _      => "Extreme"
    };

    // ── Private helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves RAWS data and the NOAA gridpoint URL for a cell, respecting the semaphore.
    /// Does NOT fetch the forecast — that is deduped across cells in ScoreAllCellsAsync.
    /// </summary>
    private async Task<(RawsData? Raws, string? GridpointUrl)> ResolveRawsAndGridpointAsync(
        H3Cell cell, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            double lat = (double)cell.CenterLat;
            double lon = (double)cell.CenterLon;

            var raws = await _raws.GetNearestStationAsync(cell.H3Index, lat, lon, ct);

            // If RAWS covers both wind + RH, we don't need NOAA at all
            if (raws?.WindSpeedMph != null && raws.RelativeHumidityPct != null)
                return (raws, null);

            // Resolve the gridpoint URL (in-memory → DB → live /points call)
            try
            {
                string url = await _noaa.GetForecastHourlyUrlAsync(cell.H3Index, lat, lon, ct);
                return (raws, url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve NOAA gridpoint URL for {H3}", cell.H3Index);
                return (raws, null);
            }
        }
        finally { semaphore.Release(); }
    }

    /// <summary>
    /// Fetches NOAA forecast from an already-known gridpoint URL, respecting the semaphore.
    /// </summary>
    private async Task<NoaaWeather?> FetchForecastWithSemaphoreAsync(
        string h3Index, string gridpointUrl, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try { return await _noaa.GetWeatherFromUrlAsync(h3Index, gridpointUrl, ct); }
        finally { semaphore.Release(); }
    }

    private static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;
}
