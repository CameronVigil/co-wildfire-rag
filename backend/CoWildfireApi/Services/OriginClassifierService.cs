namespace CoWildfireApi.Services;

/// <summary>
/// Classifies fire detections as in-Colorado or out-of-state.
/// Colorado's actual rectangular bounds (four corners + survey lines).
/// Out-of-state fires are published to the feed but do not affect cell risk scores.
/// </summary>
public class OriginClassifierService
{
    // Colorado state boundary (approximate rectangle — matches survey lines)
    private const double CoW = -109.0448;
    private const double CoE = -102.0417;
    private const double CoS =  36.9925;
    private const double CoN =  41.0006;

    public bool IsInColorado(double lat, double lon)
        => lat >= CoS && lat <= CoN && lon >= CoW && lon <= CoE;

    public string GetRegionLabel(double lat, double lon)
    {
        if (IsInColorado(lat, lon)) return "Colorado";
        if (lon < -111.0)           return "Utah/Nevada";
        if (lon > -102.0)           return "Kansas/Nebraska";
        if (lat < 37.0)             return "New Mexico/Arizona";
        if (lat > 41.5)             return "Wyoming/Idaho";
        return "Border Region";
    }
}
