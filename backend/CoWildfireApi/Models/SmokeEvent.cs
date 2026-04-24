using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Models;

/// <summary>
/// NOAA HMS smoke plume polygon intersecting Colorado. Added by HmsService daily.
/// </summary>
[Table("smoke_events")]
public class SmokeEvent
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plume_date")]
    public DateOnly PlumeDate { get; set; }

    [Column("density")]
    [MaxLength(10)]
    public string Density { get; set; } = "coarse";  // 'coarse', 'medium', 'heavy'

    [Column("plume")]
    public MultiPolygon Plume { get; set; } = null!;

    [Column("origin_state")]
    [MaxLength(2)]
    public string? OriginState { get; set; }

    [Column("origin_state_name")]
    [MaxLength(50)]
    public string? OriginStateName { get; set; }

    [Column("is_colorado_origin")]
    public bool IsColoradoOrigin { get; set; }

    [Column("colorado_counties_affected")]
    public string[] ColoradoCountiesAffected { get; set; } = Array.Empty<string>();

    [Column("smoke_description")]
    public string? SmokeDescription { get; set; }

    [Column("source")]
    [MaxLength(20)]
    public string Source { get; set; } = "NOAA_HMS";

    [Column("fetched_at")]
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}
