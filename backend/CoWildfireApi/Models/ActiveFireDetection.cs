using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Models;

/// <summary>
/// NASA FIRMS active fire detection with origin classification.
/// Refreshed every 15 minutes by FirmsService.
/// </summary>
[Table("active_fire_detections")]
public class ActiveFireDetection
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("latitude")]
    public decimal Latitude { get; set; }

    [Column("longitude")]
    public decimal Longitude { get; set; }

    // Generated column (STORED) — read-only in EF
    [Column("location")]
    public Point? Location { get; set; }

    [Column("brightness")]
    public decimal? Brightness { get; set; }

    [Column("frp")]
    public decimal? Frp { get; set; }

    [Column("confidence")]
    [MaxLength(10)]
    public string? Confidence { get; set; }

    [Column("satellite")]
    [MaxLength(20)]
    public string? Satellite { get; set; }

    [Column("acquired_at")]
    public DateTimeOffset AcquiredAt { get; set; }

    [Column("day_night")]
    [MaxLength(1)]
    public string? DayNight { get; set; }

    // Origin classification
    [Column("is_colorado")]
    public bool IsColorado { get; set; } = true;

    [Column("origin_state")]
    [MaxLength(2)]
    public string? OriginState { get; set; }

    [Column("impact_type")]
    [MaxLength(20)]
    public string? ImpactType { get; set; }  // 'fire', 'smoke_only', 'none'

    [Column("inserted_at")]
    public DateTimeOffset InsertedAt { get; set; } = DateTimeOffset.UtcNow;
}
