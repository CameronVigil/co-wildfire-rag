namespace CoWildfireApi.Services;

/// <summary>
/// Phase 5 stub — NASA FIRMS active fire detection ingestion.
/// Queries expanded bounding box (-112,34,-99,44) to capture border-state fires.
/// Every detection is classified by OriginClassifierService (ST_Within check).
/// Out-of-state fires: zero impact on risk score; published to FeedService.
/// TODO: Register NASA FIRMS API key at firms.modaps.eosdis.nasa.gov (free, ~24h).
/// </summary>
public class FirmsService
{
    private readonly ILogger<FirmsService> _logger;
    public FirmsService(ILogger<FirmsService> logger) => _logger = logger;
}
