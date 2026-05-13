using CoWildfireApi.Models;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Services;

public interface IOriginClassifierService
{
    Task EnsureLoadedAsync(CancellationToken ct = default);
    bool IsInColorado(double lat, double lon);
    string GetRegionLabel(double lat, double lon);
    string GetStateName(string? abbr);
    Task<OriginClassification> ClassifyPointAsync(
        double lat, double lon,
        double? frpMw = null, string? confidence = null,
        CancellationToken ct = default);
    Task<OriginClassification> ClassifyPlumeAsync(
        Geometry plumeGeometry, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAffectedColoradoCountiesAsync(
        Geometry plumeGeometry, CancellationToken ct = default);
}
