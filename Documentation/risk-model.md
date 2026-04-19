# Risk Model

> **SDD Note:** This document specifies the H3 grid strategy and risk scoring formula.
> Any changes to weights, normalization, or color scale must be updated here and reviewed
> before the `RiskScoringService.cs` is modified.

---

## H3 Grid Strategy

This system uses Uber's H3 hexagonal grid to index Colorado into discrete cells. Hexagons
tile without gaps, and neighbor lookups are O(1) — superior to squares or county polygons
for spatial risk analysis.

### Resolutions Used

| Resolution | Cell Area | Colorado Cell Count | Use |
|---|---|---|---|
| 6 | ~36 km² | ~190–220 | County-level risk overview; full GeoJSON safe (~100 KB) |
| 8 | ~0.74 km² | ~2,800–3,200 | Local fire behavior detail; **bbox filtering required** |

> For resolution 8, always supply `?bounds=` on `/api/risk-grid`. Full payload is ~17.5 MB.

### H3 Cell Fields

Each row in `h3_cells` stores:

| Field | Type | Description |
|---|---|---|
| `h3_index` | string | H3 index string e.g. `"8629a0807ffffff"` |
| `resolution` | int | 6 or 8 |
| `center_lat`, `center_lon` | float | Cell center (used for NOAA + AirNow lookups) |
| `boundary` | Polygon | Pre-computed WGS84 hex boundary |
| `fires_last_20yr` | int | Fire event count intersecting cell |
| `total_acres_burned` | float | Cumulative burned area |
| `avg_burn_severity` | float | Mean dNBR from MTBS |
| `years_since_last_fire` | int | Recency — low = recently burned, likely lower risk |
| `current_risk_score` | float | 0.00–10.00, refreshed hourly |
| `vegetation_type` | string | From LANDFIRE (Phase 5) |
| `slope_degrees` | float | From DEM (Phase 5) |

### H3 Library (Backend)

**NuGet package:** `H3` by Stephen Pell (canonical .NET port)

Key API:
```csharp
// Generate all cells covering Colorado polygon at resolution 6
var h3Indices = H3Index.FillPolygon(latLngBoundary, resolution: 6);

// Get pre-computed boundary polygon for a cell
H3Index cell = H3Index.Parse("8629a0807ffffff");
GeoCoordinate[] boundary = cell.GetCellBoundary();  // returns [lat, lng] pairs

// Get cell center
GeoCoordinate center = cell.ToGeoCoordinate();
```

> **Coordinate order warning:** `GetCellBoundary()` returns `[lat, lng]` pairs.
> GeoJSON requires `[lng, lat]`. Always convert before serializing.

### H3 Library (Frontend)

**npm package:** `h3-js` v4.x

> **v4 breaking change:** `cellToBoundary()` returns `[lat, lng]` — reverse to `[lng, lat]`
> for GeoJSON. The backend pre-serializes polygons so frontend h3-js is only needed for
> optional client-side operations, not for rendering.

---

## Risk Score Formula

The risk score is a weighted sum of normalized input variables. All inputs are normalized
to `[0, 1]` before weighting. The final score is scaled to `[0, 10]`.

```
risk_score = 10 × weighted_sum(
    normalize(wind_speed)              × 0.22,
    normalize(1 − relative_humidity)   × 0.18,
    normalize(1 − fuel_moisture)       × 0.18,
    fire_history_score                 × 0.12,
    normalize(slope)                   × 0.09,
    normalize(vegetation_flammability) × 0.09,
    normalize(drought_index)           × 0.08,
    normalize(days_since_rain)         × 0.04
)
```

Weights sum to **1.00**.

### Fire History Component (New — Phase 1)

Fire history is one of the strongest predictors of future fire risk. The component uses
data already stored in `h3_cells` from MTBS ingestion:

```
fire_history_score = normalize(fires_last_20yr)                        × 0.4
                   + normalize(avg_burn_severity / years_since_recovery) × 0.6
```

Where `years_since_recovery` = `MAX(1, years_since_last_fire)` (floor at 1 to avoid division by zero).

**Rationale:** A cell burned at high severity 3 years ago has a lower current score than one
burned at low severity 3 years ago, because severe burns leave less recoverable fuel. The
product `avg_burn_severity / years_since_recovery` captures both dimensions.

**Important:** The spec note "recently burned = lower risk" is an oversimplification — a
cell burned at high severity 3 years ago in dense shrubland may already have recovered
significant fuel loading. Always weight severity alongside recency.

### Input Variable Normalization Ranges

| Variable | Source | Min (→0) | Max (→1) | Notes |
|---|---|---|---|---|
| `wind_speed` | RAWS (primary) / NOAA (fallback) | 0 mph | 60 mph | Clamp at 60; use RAWS observed if station within 50km |
| `relative_humidity` | RAWS (primary) / NOAA (fallback) | 0% | 100% | Inverted: `1 - rh/100` |
| `fuel_moisture` | WFAS | 0% | 35% | Inverted: `1 - fm/35`. Dead 1-hr preferred |
| `fire_history_score` | MTBS (computed) | 0 | 1 | See formula above |
| `slope` | DEM/LANDFIRE | 0° | 45° | Clamp at 45° (Phase 5) |
| `vegetation_flammability` | LANDFIRE | 0 | 1 | Lookup table by fuel model (Phase 5) |
| `drought_index` | NOAA/PDSI | −4 | 4 | Rescale: `(pdsi + 4) / 8` |
| `days_since_rain` | RAWS (primary) / NOAA (fallback) | 0 | 30 | Clamp at 30 |

### Vegetation Flammability Lookup (Phase 5)

| LANDFIRE Fuel Model | Flammability Score | Notes |
|---|---|---|
| NB1–NB9 (non-burnable) | 0.0 | Urban, water, barren |
| GR1–GR9 (grass) | 0.7–0.9 | Higher on eastern plains |
| GS1–GS2 (grass-shrub) | 0.6–0.8 | |
| SH1–SH9 (shrub) | 0.5–0.9 | Oak, gambel oak very high |
| TU1–TU5 (timber-understory) | 0.4–0.7 | |
| TL1–TL9 (timber litter) | 0.3–0.6 | |
| SB1–SB4 (slash-blowdown) | 0.6–0.85 | |

### Bark Beetle Kill Modifier (Phase 5)

Colorado's bark beetle epidemic (mountain pine beetle, spruce beetle) dramatically increases
fuel loading in affected stands. The USFS ADS `beetle_kill_severity` field (0–1) on `h3_cells`
modifies the vegetation flammability score:

```
adjusted_flammability = base_flammability + (beetle_kill_severity × 0.25)
adjusted_flammability = MIN(1.0, adjusted_flammability)   // cap at 1.0
```

**Kill phase context:**
- **Red phase (years 1–3):** Dry needles and fine twigs present — extreme fire intensity potential
- **Gray phase (years 4–10):** Needles dropped, standing dead stems — high intensity, elevated spotting
- **Down phase (years 10+):** Logs on ground — lower intensity but continuous fuel bed

The ADS data provides kill year; use elapsed years to estimate phase and apply an additional
intensity modifier when in red phase: `adjusted_flammability = MIN(1.0, adjusted_flammability + 0.15)`

### Placeholder Until Phase 5

Until LANDFIRE data is integrated, use these defaults:
- `slope_degrees = 15.0` (average Colorado foothills slope)
- `vegetation_flammability = 0.6` (moderate shrubland)

These defaults will overstate risk in flat plains and understate in steep terrain — acceptable
for Phase 1-4 development, must be replaced before any public deployment.

---

## Risk Categories

| Score | Label | Color | Hex |
|---|---|---|---|
| 0.0–2.0 | Very Low | Dark green | `#1a7a1a` |
| 2.0–4.0 | Low | Light green | `#7dc67d` |
| 4.0–6.0 | Moderate | Yellow | `#f5e642` |
| 6.0–8.0 | High | Orange | `#f5a623` |
| 8.0–9.0 | Very High | Red | `#d0021b` |
| 9.0–10.0 | Extreme | Dark red | `#7b0000` |

### MapLibre GL Color Expression

```js
'fill-color': [
  'interpolate', ['linear'], ['get', 'riskScore'],
  0.0,  '#1a7a1a',
  2.0,  '#7dc67d',
  4.0,  '#f5e642',
  6.0,  '#f5a623',
  8.0,  '#d0021b',
  10.0, '#7b0000'
]
```

---

## Risk Score Impact Rules for Out-of-State Events

| Detection Type | Impact on Risk Score |
|---|---|
| In-state active fire pixel | Full weight — all variables contribute |
| In-state smoke (elevated AQI, no fire pixel) | Small bump to `days_since_rain` proxy; `smoke_present` flag set |
| Out-of-state fire pixel | **Zero impact** — classified separately, excluded from formula |
| Out-of-state smoke plume over CO | **Zero impact** — displayed as overlay; AQI surfaced in sidebar only |

See [out-of-state-classification.md](out-of-state-classification.md) for full classification logic.

---

## Refresh Schedule

- Risk scores recomputed **hourly** via `BackgroundService` + `PeriodicTimer`
- NOAA weather cached per H3-6 cell for 1 hour (aligned with refresh)
- Fuel moisture cached daily (WFAS updates once daily)
- When a score changes by ≥ 1.0 or crosses a category boundary → publish `risk_score` event to `FeedService`
