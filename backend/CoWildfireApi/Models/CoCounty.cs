using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace CoWildfireApi.Models;

/// <summary>
/// Colorado county boundary polygon. Seeded once from Census TIGER/Line county shapefile
/// (STATEFP='08'). Used by HmsService to identify affected counties.
/// </summary>
[Table("co_counties")]
public class CoCounty
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("county_fips")]
    [MaxLength(5)]
    public string CountyFips { get; set; } = string.Empty;

    [Column("county_name")]
    [MaxLength(100)]
    public string CountyName { get; set; } = string.Empty;

    [Column("state_fips")]
    [MaxLength(2)]
    public string StateFips { get; set; } = "08";

    [Column("boundary")]
    public MultiPolygon Boundary { get; set; } = null!;
}
