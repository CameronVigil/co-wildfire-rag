using System.Net.Http.Json;
using System.Text.Json;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls the NIFC/WFIGS ArcGIS REST API for active wildland fire incidents in Colorado.
/// Emits 'active-incident' events for new fires and significant updates (≥25% acreage
/// growth or ≥10pp containment change). First poll seeds known state without emitting.
/// </summary>
public class NifcIncidentPoller
{
    // NIFC current wildland fire locations, Colorado only
    private const string QueryUrl =
        "https://services3.arcgis.com/T4QMspbfLg3qTGWY/arcgis/rest/services/" +
        "Current_WildlandFire_Locations/FeatureServer/0/query" +
        "?where=POOState%20%3D%20%27US-CO%27" +
        "&outFields=UniqueFireIdentifier%2CIncidentName%2CDiscoveryAcres%2CDailyAcres%2CPercentContained" +
        "&f=json&resultRecordCount=50";

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly ILogger<NifcIncidentPoller> _logger;

    // FireId → (acres, containment%)
    private readonly Dictionary<string, (double Acres, double? Pct)> _known = new();
    private bool _firstRun = true;

    public NifcIncidentPoller(
        IHttpClientFactory httpFactory,
        FeedService feed,
        ILogger<NifcIncidentPoller> logger)
    {
        _http = httpFactory.CreateClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "CoWildfireAnalyzer/1.0 (contact@cowildfire.dev)");
        _feed   = feed;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonElement>(QueryUrl, ct);
            if (!json.TryGetProperty("features", out var features))
            {
                _logger.LogDebug("NIFC response had no 'features' property — possible API error");
                return;
            }

            for (int i = 0; i < features.GetArrayLength(); i++)
            {
                var attrs = features[i].GetProperty("attributes");

                var id = GetStr(attrs, "UniqueFireIdentifier");
                if (string.IsNullOrEmpty(id)) continue;

                var name = GetStr(attrs, "IncidentName") is { Length: > 0 } n ? n : "Unknown Fire";

                double acres = GetDouble(attrs, "DailyAcres")
                            ?? GetDouble(attrs, "DiscoveryAcres")
                            ?? 0;
                double? pct  = GetDouble(attrs, "PercentContained");

                if (!_known.TryGetValue(id, out var prev))
                {
                    _known[id] = (acres, pct);
                    if (!_firstRun)
                    {
                        var detail = BuildDetail(name, acres, pct);
                        _logger.LogInformation("New NIFC incident: {Name}", name);
                        await _feed.PublishAsync(new LiveFeedEvent
                        {
                            Type      = "active-incident",
                            Severity  = "warning",
                            Source    = "NIFC",
                            Detail    = detail,
                            SourceUrl = "https://www.nifc.gov/fire-information/nfn",
                        }, ct);
                    }
                }
                else
                {
                    bool acreasGrew = prev.Acres > 0 && acres > prev.Acres * 1.25;
                    bool containmentShifted = pct.HasValue && prev.Pct.HasValue
                        && Math.Abs(pct.Value - prev.Pct.Value) >= 10;

                    if (acreasGrew || containmentShifted)
                    {
                        _known[id] = (acres, pct);
                        var detail = BuildDetail(name, acres, pct) + " (updated)";
                        await _feed.PublishAsync(new LiveFeedEvent
                        {
                            Type      = "active-incident",
                            Severity  = "info",
                            Source    = "NIFC",
                            Detail    = detail,
                            SourceUrl = "https://www.nifc.gov/fire-information/nfn",
                        }, ct);
                    }
                }
            }

            _firstRun = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NIFC incident poll failed");
        }
    }

    private static string BuildDetail(string name, double acres, double? pct) =>
        $"{name} — {acres:N0} acres" + (pct.HasValue ? $", {pct:N0}% contained" : "");

    private static string? GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? GetDouble(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
