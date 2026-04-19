namespace CoWildfireApi.Services;

/// <summary>
/// Phase 2 stub — hourly risk scoring engine.
/// Will implement the weighted risk formula from risk-model.md:
///   risk_score = 10 × weighted_sum(wind, humidity, fuel_moisture, fire_history,
///                                  slope, vegetation, drought, days_since_rain)
/// After each score update, inserts a row into h3_risk_history.
/// Uses RAWS observed weather as primary; NOAA gridded as fallback.
/// </summary>
public class RiskScoringService
{
    private readonly ILogger<RiskScoringService> _logger;
    public RiskScoringService(ILogger<RiskScoringService> logger) => _logger = logger;

    public Task ScoreAllCellsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("RiskScoringService: Phase 2 not yet implemented");
        return Task.CompletedTask;
    }
}
