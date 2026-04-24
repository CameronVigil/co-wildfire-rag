using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoWildfireApi.Models;

/// <summary>
/// Hourly AirNow AQI + PM2.5 per H3-6 cell center. Added by AirNowService.
/// </summary>
[Table("aqi_observations")]
public class AqiObservation
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("h3_index")]
    [MaxLength(20)]
    public string H3Index { get; set; } = string.Empty;

    [Column("observed_at")]
    public DateTimeOffset ObservedAt { get; set; }

    [Column("aqi")]
    public short? Aqi { get; set; }

    [Column("pm25")]
    public decimal? Pm25 { get; set; }

    [Column("category")]
    [MaxLength(50)]
    public string? Category { get; set; }

    [Column("smoke_inferred")]
    public bool SmokeInferred { get; set; }
}
