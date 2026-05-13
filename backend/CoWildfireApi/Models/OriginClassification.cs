namespace CoWildfireApi.Models;

public sealed record OriginClassification(
    bool IsColorado,
    string OriginState,
    string OriginStateName,
    bool SmokeTransportLikely,
    string ImpactType);
