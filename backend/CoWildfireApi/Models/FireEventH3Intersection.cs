using System.ComponentModel.DataAnnotations.Schema;

namespace CoWildfireApi.Models;

[Table("fire_event_h3_intersections")]
public class FireEventH3Intersection
{
    [Column("fire_event_id")]
    public Guid FireEventId { get; set; }

    [Column("h3_cell_id")]
    public long H3CellId { get; set; }

    [Column("overlap_pct")]
    public decimal? OverlapPct { get; set; }

    public FireEvent FireEvent { get; set; } = null!;
    public H3Cell H3Cell { get; set; } = null!;
}
