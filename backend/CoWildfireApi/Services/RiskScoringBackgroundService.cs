namespace CoWildfireApi.Services;

/// <summary>
/// Hourly background service that drives the risk scoring engine.
///
/// Uses PeriodicTimer (preferred over Timer in .NET 8 for hosted services — avoids
/// concurrent executions and respects cancellation cleanly).
///
/// Lifecycle:
///   1. 30-second startup delay (lets the app fully initialize + DB migrations complete)
///   2. First scoring run immediately after delay
///   3. Subsequent runs every hour
///
/// Injects IServiceScopeFactory because RiskScoringService is transient and depends
/// on IDbContextFactory — it must be resolved in a scope per invocation.
/// </summary>
public class RiskScoringBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RiskScoringBackgroundService> _logger;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval      = TimeSpan.FromHours(1);

    public RiskScoringBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<RiskScoringBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RiskScoringBackgroundService starting (first run in {Delay}s)",
            StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // App shutting down during startup delay — exit cleanly
        }

        // Run immediately, then every hour
        await RunScoringAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScoringAsync(stoppingToken);
        }
    }

    private async Task RunScoringAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RiskScoringService>();
        try
        {
            await service.ScoreAllCellsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown — not an error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Risk scoring run failed — will retry at next scheduled interval");
        }
    }
}
