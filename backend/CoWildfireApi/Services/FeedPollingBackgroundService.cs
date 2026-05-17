namespace CoWildfireApi.Services;

public class FeedPollingBackgroundService : BackgroundService
{
    private readonly FirmsService _firms;
    private readonly AirNowService _airNow;
    private readonly HmsService _hms;
    private readonly NoaaService _noaa;
    private readonly InciwebFeedPoller _inciweb;
    private readonly NifcIncidentPoller _nifc;
    private readonly CdotRssPoller _cdot;
    private readonly NewsRssPoller _news;
    private readonly ColoradoCountyOesPoller _oes;
    private readonly NwsSpotForecastPoller _spotForecast;
    private readonly RawsAlertPoller _rawsAlert;
    private readonly IConfiguration _config;
    private readonly ILogger<FeedPollingBackgroundService> _logger;

    public FeedPollingBackgroundService(
        FirmsService firms,
        AirNowService airNow,
        HmsService hms,
        NoaaService noaa,
        InciwebFeedPoller inciweb,
        NifcIncidentPoller nifc,
        CdotRssPoller cdot,
        NewsRssPoller news,
        ColoradoCountyOesPoller oes,
        NwsSpotForecastPoller spotForecast,
        RawsAlertPoller rawsAlert,
        IConfiguration config,
        ILogger<FeedPollingBackgroundService> logger)
    {
        _firms        = firms;
        _airNow       = airNow;
        _hms          = hms;
        _noaa         = noaa;
        _inciweb      = inciweb;
        _nifc         = nifc;
        _cdot         = cdot;
        _news         = news;
        _oes          = oes;
        _spotForecast = spotForecast;
        _rawsAlert    = rawsAlert;
        _config       = config;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var intervalMinutes = _config.GetValue<int>("FeedPolling:IntervalMinutes", 5);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        _logger.LogInformation("Feed polling started — interval {Minutes} min", intervalMinutes);

        do
        {
            try
            {
                await Task.WhenAll(
                    _firms.PollAsync(stoppingToken),
                    _airNow.PollAsync(stoppingToken),
                    _hms.PollAsync(stoppingToken),
                    _noaa.PollExpandedAlertsAsync(stoppingToken),
                    _inciweb.PollAsync(stoppingToken),
                    _nifc.PollAsync(stoppingToken),
                    _cdot.PollAsync(stoppingToken),
                    _news.PollAsync(stoppingToken),
                    _oes.PollAsync(stoppingToken),
                    _spotForecast.PollAsync(stoppingToken),
                    _rawsAlert.PollAsync(stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Feed polling cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
