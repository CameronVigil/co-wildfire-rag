using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Models;

[Table("fire_events")]
public class FireEvent
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("fire_id")]
    [MaxLength(50)]
    public string FireId { get; set; } = string.Empty;

    [Column("fire_name")]
    [MaxLength(255)]
    public string FireName { get; set; } = string.Empty;

    [Column("year")]
    public short Year { get; set; }

    [Column("start_date")]
    public DateOnly? StartDate { get; set; }

    [Column("end_date")]
    public DateOnly? EndDate { get; set; }

    [Column("acres_burned")]
    public decimal? AcresBurned { get; set; }

    [Column("avg_dnbr")]
    public decimal? AvgDnbr { get; set; }

    [Column("max_dnbr")]
    public decimal? MaxDnbr { get; set; }

    [Column("source")]
    [MaxLength(50)]
    public string Source { get; set; } = string.Empty;

    [Column("state")]
    [MaxLength(2)]
    public string State { get; set; } = "CO";

    [Column("county")]
    [MaxLength(100)]
    public string? County { get; set; }

    [Column("perimeter")]
    public MultiPolygon? Perimeter { get; set; }

    // centroid is a generated/computed column — read-only
    [Column("centroid")]
    public Point? Centroid { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<FireEventH3Intersection> H3Intersections { get; set; } = new List<FireEventH3Intersection>();
}
