using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls county Office of Emergency Services RSS feeds for evacuation and fire alerts.
/// Emits 'evacuation-alert' events with severity = "critical".
/// Each feed is polled independently — a failed feed does not block the others.
/// </summary>
public class ColoradoCountyOesPoller
{
    private static readonly (string Url, string County)[] Feeds =
    [
        ("https://www.jeffco.us/rss.aspx?feed=news",        "Jefferson County"),
        ("https://www.larimer.gov/rss/news",                 "Larimer County"),
        ("https://www.elpasoco.com/news/feed/",              "El Paso County"),
        ("https://www.arapahoegov.com/rss.aspx?RSID=26",    "Arapahoe County"),
    ];

    private static readonly string[] Keywords =
        ["fire", "wildfire", "evacuat", "closure", "emergency", "burn"];

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly ILogger<ColoradoCountyOesPoller> _logger;

    private readonly HashSet<string> _seenGuids =
        new(StringComparer.OrdinalIgnoreCase);

    public ColoradoCountyOesPoller(
        IHttpClientFactory httpFactory,
        FeedService feed,
        ILogger<ColoradoCountyOesPoller> logger)
    {
        _http = httpFactory.CreateClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "CoWildfireAnalyzer/1.0 (contact@cowildfire.dev)");
        _feed   = feed;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        foreach (var (url, county) in Feeds)
        {
            try { await PollFeedAsync(url, county, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "County OES poll failed for {County}", county);
            }
        }

        if (_seenGuids.Count > 1000) _seenGuids.Clear();
    }

    private static string SanitizeXml(string xml) =>
        Regex.Replace(xml, @"&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)", "&amp;");

    private async Task PollFeedAsync(string url, string county, CancellationToken ct)
    {
        var xml = await _http.GetStringAsync(url, ct);
        var doc = XDocument.Parse(SanitizeXml(xml));

        var items = doc.Root?.Element("channel")?.Elements("item")
                    ?? Enumerable.Empty<XElement>();

        foreach (var item in items)
        {
            var guid = item.Element("guid")?.Value
                    ?? item.Element("link")?.Value ?? "";
            if (string.IsNullOrEmpty(guid) || _seenGuids.Contains(guid)) continue;
            _seenGuids.Add(guid);

            var title       = item.Element("title")?.Value ?? "";
            var description = item.Element("description")?.Value ?? "";
            var link        = item.Element("link")?.Value;

            var text = (title + " " + description).ToLowerInvariant();
            if (!Keywords.Any(kw => text.Contains(kw))) continue;

            _logger.LogInformation("County OES [{County}]: {Title}", county, title);
            await _feed.PublishAsync(new LiveFeedEvent
            {
                Type      = "evacuation-alert",
                Severity  = "critical",
                Source    = county,
                Detail    = title,
                County    = county,
                SourceUrl = link,
            }, ct);
        }
    }
}
