using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls NWS Weather Forecast Offices for Fire Weather Statements (FWS).
/// An FWS being issued is near-definitive confirmation that an active incident management
/// team (IMT) is managing a fire in that WFO's area.
/// Emits 'spot-forecast' events with severity = "warning".
/// </summary>
public partial class NwsSpotForecastPoller
{
    // Colorado WFOs: Boulder, Pueblo, Grand Junction, Cheyenne (covers NE CO)
    private static readonly string[] Wfos = ["BOU", "PUB", "GJT", "CYS"];
    private const string ProductsBase = "https://api.weather.gov/products";

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly ILogger<NwsSpotForecastPoller> _logger;

    private readonly HashSet<string> _seenIds =
        new(StringComparer.OrdinalIgnoreCase);

    public NwsSpotForecastPoller(
        IHttpClientFactory httpFactory,
        FeedService feed,
        ILogger<NwsSpotForecastPoller> logger)
    {
        _http   = httpFactory.CreateClient("noaa");
        _feed   = feed;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        foreach (var wfo in Wfos)
        {
            try { await PollWfoAsync(wfo, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "FWS poll failed for WFO {Wfo}", wfo);
            }
        }

        if (_seenIds.Count > 500) _seenIds.Clear();
    }

    private async Task PollWfoAsync(string wfo, CancellationToken ct)
    {
        var listUrl = $"{ProductsBase}?type=FWS&location={wfo}&limit=5";
        var json = await _http.GetFromJsonAsync<JsonElement>(listUrl, ct);

        if (!json.TryGetProperty("@graph", out var graph)) return;

        foreach (var product in graph.EnumerateArray())
        {
            if (!product.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString() ?? "";
            if (string.IsNullOrEmpty(id) || _seenIds.Contains(id)) continue;
            _seenIds.Add(id);

            // Fetch full product text to extract fire name
            string? productText = null;
            try
            {
                var productJson = await _http.GetFromJsonAsync<JsonElement>(
                    $"{ProductsBase}/{id}", ct);
                if (productJson.TryGetProperty("productText", out var ptEl))
                    productText = ptEl.GetString();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch FWS product text for {Id}", id);
            }

            var detail = BuildDetail(wfo, productText);
            _logger.LogInformation("NWS FWS [{Wfo}]: {Detail}", wfo, detail);

            await _feed.PublishAsync(new LiveFeedEvent
            {
                Type      = "spot-forecast",
                Severity  = "warning",
                Source    = $"NWS {wfo}",
                Detail    = detail,
                SourceUrl = $"https://forecast.weather.gov/product.php?site={wfo}&issuedby={wfo}&product=FWS",
            }, ct);
        }
    }

    private static string BuildDetail(string wfo, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            // Try "FIRE NAME...SOME FIRE" or "INCIDENT NAME...SOME FIRE"
            var match = FireNameRegex().Match(text);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                    return $"Spot Forecast: {name} (WFO {wfo})";
            }
        }
        return $"Fire Weather Statement issued by WFO {wfo}";
    }

    [GeneratedRegex(@"(?:FIRE NAME|INCIDENT NAME|NAME)\.{3}\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex FireNameRegex();
}
