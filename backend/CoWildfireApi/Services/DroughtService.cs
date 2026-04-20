using System.Net.Http.Json;
using System.Text.Json;

namespace CoWildfireApi.Services;

/// <summary>
/// Fetches Colorado drought severity from the US Drought Monitor REST API and converts
/// it to a Palmer Drought Severity Index (PDSI) proxy.
///
/// PDSI range: -4 (exceptional drought) to +4 (exceptionally wet). 0 = normal.
/// The risk formula uses this to normalize drought contribution to [0,1]:
///   normalize = (pdsi + 4) / 8.0
///
/// USDM updates weekly (Thursdays). Cache TTL is 24 hours.
/// API is free, no key required.
///
/// API: https://usdmdataservices.unl.edu/api/StateStatistics/
///        GetDroughtSeverityStatisticsByAreaPercent?aoi=08&startdate=...&enddate=...&statisticsType=2
///
/// Colorado FIPS code: 08
/// statisticsType=2 → cumulative (D0 includes D1+D2+D3+D4)
/// </summary>
public class DroughtService
{
    private readonly HttpClient _http;
    private readonly ILogger<DroughtService> _logger;

    // Cached PDSI proxy — updated at most once per day
    // Default: -2.0 (severe drought, matching Colorado's April 2026 conditions per NIFC outlook)
    private double _cachedPdsi = -2.0;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    // PDSI proxy per drought level
    // Based on NOAA CPC historical correlation between USDM categories and PDSI
    private static readonly double[] DroughtLevelPdsi =
    [
        0.5,   // None (D-1, mildly wet-normal for Colorado)
        -1.0,  // D0  Abnormally Dry
        -2.0,  // D1  Moderate Drought
        -3.0,  // D2  Severe Drought
        -3.5,  // D3  Extreme Drought
        -4.0,  // D4  Exceptional Drought
    ];

    public DroughtService(IHttpClientFactory httpFactory, ILogger<DroughtService> logger)
    {
        _http   = httpFactory.CreateClient();
        _logger = logger;
    }

    /// <summary>
    /// Returns a state-level PDSI proxy for Colorado. Cached 24 hours.
    /// Falls back to the previously cached value (or default -2.0) if the API fails.
    /// </summary>
    public async Task<double> GetColoradoPdsiAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cacheExpiry > DateTimeOffset.UtcNow)
                return _cachedPdsi;
        }
        finally { _lock.Release(); }

        try
        {
            // Query the last 2 weeks so we always get at least one data point
            string end   = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            string start = DateTimeOffset.UtcNow.AddDays(-14).ToString("yyyy-MM-dd");

            // statisticsType=2 = cumulative percentages (D0 includes D1+D2+D3+D4)
            // We need non-cumulative to compute a weighted average, so use statisticsType=1
            string url = "https://usdmdataservices.unl.edu/api/StateStatistics/" +
                         $"GetDroughtSeverityStatisticsByAreaPercent" +
                         $"?aoi=08&startdate={start}&enddate={end}&statisticsType=1";

            var entries = await _http.GetFromJsonAsync<JsonElement[]>(url, ct);
            if (entries == null || entries.Length == 0)
                return _cachedPdsi;

            // Take the most recent entry
            var latest = entries[entries.Length - 1];

            double none = GetPct(latest, "None");
            double d0   = GetPct(latest, "D0");
            double d1   = GetPct(latest, "D1");
            double d2   = GetPct(latest, "D2");
            double d3   = GetPct(latest, "D3");
            double d4   = GetPct(latest, "D4");

            // Weighted average PDSI across drought categories
            double pdsi = (none * DroughtLevelPdsi[0] +
                           d0   * DroughtLevelPdsi[1] +
                           d1   * DroughtLevelPdsi[2] +
                           d2   * DroughtLevelPdsi[3] +
                           d3   * DroughtLevelPdsi[4] +
                           d4   * DroughtLevelPdsi[5]) / 100.0;

            pdsi = Math.Max(-4.0, Math.Min(4.0, pdsi));

            _logger.LogInformation(
                "USDM: CO drought — None={None:F1}% D0={D0:F1}% D1={D1:F1}% D2={D2:F1}% D3={D3:F1}% D4={D4:F1}% → PDSI proxy={Pdsi:F2}",
                none, d0, d1, d2, d3, d4, pdsi);

            await _lock.WaitAsync(ct);
            try
            {
                _cachedPdsi  = pdsi;
                _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheTtl);
            }
            finally { _lock.Release(); }

            return pdsi;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USDM drought fetch failed — using cached PDSI {Pdsi:F2}", _cachedPdsi);

            // Extend the cache window on failure to avoid hammering a down service
            await _lock.WaitAsync(ct);
            try { _cacheExpiry = DateTimeOffset.UtcNow.AddHours(6); }
            finally { _lock.Release(); }

            return _cachedPdsi;
        }
    }

    private static double GetPct(JsonElement entry, string key)
    {
        if (!entry.TryGetProperty(key, out var val)) return 0;
        if (val.ValueKind == JsonValueKind.Null) return 0;
        if (val.TryGetDouble(out double d)) return d;
        if (val.ValueKind == JsonValueKind.String &&
            double.TryParse(val.GetString(), out double ds)) return ds;
        return 0;
    }
}
