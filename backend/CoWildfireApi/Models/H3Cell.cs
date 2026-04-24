using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Models;

[Table("h3_cells")]
public class H3Cell
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("h3_index")]
    [MaxLength(20)]
    public string H3Index { get; set; } = string.Empty;

    [Column("resolution")]
    public short Resolution { get; set; }

    [Column("center_lat")]
    public decimal CenterLat { get; set; }

    [Column("center_lon")]
    public decimal CenterLon { get; set; }

    [Column("boundary")]
    public Polygon? Boundary { get; set; }

    // Fire history
    [Column("fires_last_20yr")]
    public short FiresLast20yr { get; set; }

    [Column("total_acres_burned")]
    public decimal TotalAcresBurned { get; set; }

    [Column("avg_burn_severity")]
    public decimal? AvgBurnSeverity { get; set; }

    [Column("years_since_last_fire")]
    public short? YearsSinceLastFire { get; set; }

    [Column("last_fire_year")]
    public short? LastFireYear { get; set; }

    // Terrain / vegetation (Phase 5)
    [Column("vegetation_type")]
    [MaxLength(100)]
    public string? VegetationType { get; set; }

    [Column("slope_degrees")]
    public decimal? SlopeDegrees { get; set; }

    [Column("aspect_degrees")]
    public decimal? AspectDegrees { get; set; }

    // Bark beetle (Phase 5)
    [Column("beetle_kill_severity")]
    public decimal? BeetleKillSeverity { get; set; }

    [Column("beetle_kill_phase")]
    [MaxLength(10)]
    public string? BeetleKillPhase { get; set; }

    // Smoke
    [Column("smoke_present")]
    public bool SmokePresent { get; set; }

    [Column("smoke_inferred")]
    public bool SmokeInferred { get; set; }

    // RAWS (Phase 2)
    [Column("raws_station_id")]
    [MaxLength(10)]
    public string? RawsStationId { get; set; }

    [Column("raws_distance_km")]
    public decimal? RawsDistanceKm { get; set; }

    [Column("raws_wind_speed_mph")]
    public decimal? RawsWindSpeedMph { get; set; }

    [Column("raws_relative_humidity_pct")]
    public decimal? RawsRelativeHumidityPct { get; set; }

    // Live risk score
    [Column("current_risk_score")]
    public decimal? CurrentRiskScore { get; set; }

    [Column("risk_score_updated_at")]
    public DateTimeOffset? RiskScoreUpdatedAt { get; set; }

    // Weather snapshot
    [Column("wind_speed_mph")]
    public decimal? WindSpeedMph { get; set; }

    [Column("relative_humidity_pct")]
    public decimal? RelativeHumidityPct { get; set; }

    [Column("fuel_moisture_pct")]
    public decimal? FuelMoisturePct { get; set; }

    [Column("drought_index")]
    public decimal? DroughtIndex { get; set; }

    [Column("days_since_rain")]
    public short? DaysSinceRain { get; set; }

    [Column("red_flag_warning")]
    public bool RedFlagWarning { get; set; }

    // NOAA gridpoint URL cache (permanent — determined by lat/lon, never changes)
    [Column("noaa_gridpoint_url")]
    [MaxLength(200)]
    public string? NoaaGridpointUrl { get; set; }

    [Column("weather_source")]
    [MaxLength(10)]
    public string WeatherSource { get; set; } = "NOAA";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<FireEventH3Intersection> FireIntersections { get; set; } = new List<FireEventH3Intersection>();
}
