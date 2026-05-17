using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls Colorado fire-focused RSS feeds for wildfire news.
/// Emits 'news-article' events; severity = "warning" if title contains evacuation/structure keywords.
/// First poll seeds the seen-set to avoid flooding the feed on startup.
/// </summary>
public class NewsRssPoller
{
    private static readonly (string Url, string Name)[] Feeds =
    [
        ("https://coloradosun.com/category/wildfire/feed/",   "Colorado Sun"),
        ("https://www.denverpost.com/wildfire/feed/",          "Denver Post"),
        ("https://csfs.colostate.edu/feed/",                   "CSFS"),
    ];

    private static readonly string[] Keywords =
        ["fire", "wildfire", "evacuat", "closure", "burn", "blaze"];

    private static readonly string[] WarnKeywords =
        ["evacuat", "structure threat", "structure-threat"];

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly ILogger<NewsRssPoller> _logger;

    private readonly HashSet<string> _seenGuids =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _firstRun = true;

    public NewsRssPoller(
        IHttpClientFactory httpFactory,
        FeedService feed,
        ILogger<NewsRssPoller> logger)
    {
        _http = httpFactory.CreateClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "CoWildfireAnalyzer/1.0 (contact@cowildfire.dev)");
        _feed   = feed;
        _logger = logger;
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        foreach (var (url, name) in Feeds)
        {
            try { await PollFeedAsync(url, name, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "News RSS poll failed for {Name}", name);
            }
        }

        _firstRun = false;
        if (_seenGuids.Count > 1000) _seenGuids.Clear();
    }

    private static string SanitizeXml(string xml) =>
        Regex.Replace(xml, @"&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)", "&amp;");

    private async Task PollFeedAsync(string url, string sourceName, CancellationToken ct)
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
            if (_firstRun) continue;

            var title = item.Element("title")?.Value ?? "";
            var link  = item.Element("link")?.Value;

            var text = title.ToLowerInvariant();
            if (!Keywords.Any(kw => text.Contains(kw))) continue;

            var severity = WarnKeywords.Any(kw => text.Contains(kw)) ? "warning" : "info";

            _logger.LogInformation("News article [{Source}]: {Title}", sourceName, title);
            await _feed.PublishAsync(new LiveFeedEvent
            {
                Type      = "news-article",
                Severity  = severity,
                Source    = sourceName,
                Detail    = title,
                SourceUrl = link,
            }, ct);
        }
    }
}
