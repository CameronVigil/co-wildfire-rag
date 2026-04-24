namespace CoWildfireApi.Services;

public class FeedPollingBackgroundService : BackgroundService
{
    private readonly FirmsService _firms;
    private readonly AirNowService _airNow;
    private readonly HmsService _hms;
    private readonly IConfiguration _config;
    private readonly ILogger<FeedPollingBackgroundService> _logger;

    public FeedPollingBackgroundService(
        FirmsService firms,
        AirNowService airNow,
        HmsService hms,
        IConfiguration config,
        ILogger<FeedPollingBackgroundService> logger)
    {
        _firms  = firms;
        _airNow = airNow;
        _hms    = hms;
        _config = config;
        _logger = logger;
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
                    _hms.PollAsync(stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Feed polling cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
