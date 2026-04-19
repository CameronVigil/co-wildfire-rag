using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoWildfireApi.Models;

[Table("h3_risk_history")]
public class H3RiskHistory
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("h3_index")]
    [MaxLength(20)]
    public string H3Index { get; set; } = string.Empty;

    [Column("resolution")]
    public short Resolution { get; set; }

    [Column("risk_score")]
    public decimal RiskScore { get; set; }

    [Column("risk_category")]
    [MaxLength(20)]
    public string RiskCategory { get; set; } = string.Empty;

    [Column("wind_speed_mph")]
    public decimal? WindSpeedMph { get; set; }

    [Column("relative_humidity_pct")]
    public decimal? RelativeHumidityPct { get; set; }

    [Column("fuel_moisture_pct")]
    public decimal? FuelMoisturePct { get; set; }

    [Column("drought_index")]
    public decimal? DroughtIndex { get; set; }

    [Column("weather_source")]
    [MaxLength(10)]
    public string? WeatherSource { get; set; }

    [Column("scored_at")]
    public DateTimeOffset ScoredAt { get; set; } = DateTimeOffset.UtcNow;
}
