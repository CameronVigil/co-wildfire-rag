using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Models;

/// <summary>
/// US state boundary polygon. Seeded once from Census TIGER/Line state shapefile.
/// Used by OriginClassifierService for ST_Within lookups.
/// </summary>
[Table("state_boundaries")]
public class StateBoundary
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("state_fips")]
    [MaxLength(2)]
    public string StateFips { get; set; } = string.Empty;

    [Column("state_abbr")]
    [MaxLength(2)]
    public string StateAbbr { get; set; } = string.Empty;

    [Column("state_name")]
    [MaxLength(50)]
    public string StateName { get; set; } = string.Empty;

    [Column("boundary")]
    public MultiPolygon Boundary { get; set; } = null!;
}
