namespace CoWildfireApi.Models;

// ── Request ──────────────────────────────────────────────────────────────────

/// <summary>POST /api/query request body.</summary>
public class QueryRequest
{
    /// <summary>Geographic point (lat/lon). Used to resolve h3Index if not provided.</summary>
    public LocationPoint? Location { get; set; }

    /// <summary>H3 cell index string. If omitted, derived from Location.</summary>
    public string? H3Index { get; set; }

    /// <summary>The natural language question.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>H3 resolution to look up (default 6).</summary>
    public int Resolution { get; set; } = 6;
}

public class LocationPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
}

// ── Response ─────────────────────────────────────────────────────────────────

/// <summary>POST /api/query response body.</summary>
public class QueryResponse
{
    public string Answer          { get; set; } = string.Empty;
    public List<SourceDocument> Sources { get; set; } = new();
    public CellStats?   CellStats          { get; set; }
    public CurrentConditions? CurrentConditions { get; set; }
    public long   ProcessingMs   { get; set; }
    public string ModelUsed      { get; set; } = string.Empty;
    public int    ChunksRetrieved { get; set; }
}

public class SourceDocument
{
    public string ChunkId        { get; set; } = string.Empty;
    public string DocumentTitle  { get; set; } = string.Empty;
    public string Excerpt        { get; set; } = string.Empty;
    public float  Similarity     { get; set; }
    public string SourceUrl      { get; set; } = string.Empty;
}

public class CellStats
{
    public string  H3Index          { get; set; } = string.Empty;
    public decimal? RiskScore       { get; set; }
    public string  RiskCategory     { get; set; } = string.Empty;
    public short   FiresLast20yr    { get; set; }
    public decimal TotalAcresBurned { get; set; }
    public decimal? AvgBurnSeverity { get; set; }
    public short?  YearsSinceLastFire { get; set; }
}

public class CurrentConditions
{
    public decimal? WindSpeedMph           { get; set; }
    public decimal? RelativeHumidityPct   { get; set; }
    public decimal? FuelMoisturePct       { get; set; }
    public decimal? DroughtIndex          { get; set; }
    public short?   DaysSinceRain         { get; set; }
    public bool     RedFlagWarning        { get; set; }
    public string   ForecastSummary       { get; set; } = string.Empty;
    public string   DataSource            { get; set; } = string.Empty;
    public DateTimeOffset RetrievedAt     { get; set; }
}
