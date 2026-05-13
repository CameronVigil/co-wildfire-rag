using System.Xml.Linq;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls the CDOT statewide news RSS feed for fire- or smoke-related road closures.
/// Emits 'road-closure' feed events for new matching items.
/// </summary>
public class CdotRssPoller
{
    private const string FeedUrl = "https://www.codot.gov/news/feeds/statewide";

    private static readonly string[] Keywords =
        ["fire", "wildfire", "smoke", "evacuation", "closure", "burn"];

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly ILogger<CdotRssPoller> _logger;

    private readonly HashSet<string> _seenGuids =
        new(StringComparer.OrdinalIgnoreCase);

    public CdotRssPoller(
        IHttpClientFactory httpFactory,
        FeedService feed,
        ILogger<CdotRssPoller> logger)
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
            var xml = await _http.GetStringAsync(FeedUrl, ct);
            var doc = XDocument.Parse(xml);

            var items = doc.Root?
                .Element("channel")?
                .Elements("item")
                ?? Enumerable.Empty<XElement>();

            foreach (var item in items)
            {
                var guid  = item.Element("guid")?.Value
                         ?? item.Element("link")?.Value
                         ?? "";
                if (string.IsNullOrEmpty(guid) || _seenGuids.Contains(guid))
                    continue;

                var title       = item.Element("title")?.Value ?? "";
                var description = item.Element("description")?.Value ?? "";
                var link        = item.Element("link")?.Value;

                var text = (title + " " + description).ToLowerInvariant();
                if (!Keywords.Any(kw => text.Contains(kw)))
                    continue;

                _seenGuids.Add(guid);
                _logger.LogInformation("CDOT fire-related road item: {Title}", title);

                await _feed.PublishAsync(new LiveFeedEvent
                {
                    Type      = "road-closure",
                    Severity  = "warning",
                    Source    = "CDOT",
                    Detail    = title,
                    SourceUrl = link,
                }, ct);
            }

            if (_seenGuids.Count > 500) _seenGuids.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "CDOT RSS poll failed");
        }
    }
}
