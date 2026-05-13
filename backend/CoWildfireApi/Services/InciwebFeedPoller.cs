using System.Xml.Linq;
using CoWildfireApi.Models;

namespace CoWildfireApi.Services;

/// <summary>
/// Polls the InciWeb Colorado RSS feed for new wildfire incidents.
/// Emits 'inciweb-incident' feed events only for incidents discovered after the first poll
/// (first poll seeds the seen-set so restarts don't flood the feed).
/// </summary>
public class InciwebFeedPoller
{
    private const string FeedUrl =
        "https://inciweb.wildfire.gov/feeds/rss/incidents/state/colorado/";

    private readonly HttpClient _http;
    private readonly FeedService _feed;
    private readonly ILogger<InciwebFeedPoller> _logger;

    private readonly HashSet<string> _seenGuids =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _firstRun = true;

    public InciwebFeedPoller(
        IHttpClientFactory httpFactory,
        FeedService feed,
        ILogger<InciwebFeedPoller> logger)
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
                if (string.IsNullOrEmpty(guid)) continue;

                if (_seenGuids.Contains(guid)) continue;
                _seenGuids.Add(guid);

                if (_firstRun) continue; // seed only — don't emit on startup

                var title = item.Element("title")?.Value ?? "(unnamed incident)";
                var link  = item.Element("link")?.Value;

                _logger.LogInformation("New InciWeb incident: {Title}", title);

                await _feed.PublishAsync(new LiveFeedEvent
                {
                    Type      = "inciweb-incident",
                    Severity  = "warning",
                    Source    = "InciWeb",
                    Detail    = title,
                    SourceUrl = link,
                }, ct);
            }

            _firstRun = false;

            // Guard against unbounded growth in long-running sessions
            if (_seenGuids.Count > 500) _seenGuids.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "InciWeb RSS poll failed");
        }
    }
}
