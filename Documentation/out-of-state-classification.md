# Out-of-State Fire & Smoke Classification

> **SDD Note:** Colorado regularly receives smoke from fires in New Mexico, Utah, Wyoming,
> Arizona, and California. This spec defines how the system classifies fire and smoke events
> by origin, prevents out-of-state events from inflating Colorado risk scores, and surfaces
> them clearly in the UI.

---

## Problem Statement

NASA FIRMS active fire detections and NOAA HMS smoke plumes do not carry state-of-origin
labels. Without classification:
1. Out-of-state fire pixels near the Colorado border inflate risk scores for Colorado H3 cells
2. Users see smoke plumes labeled as "Colorado fire activity" when they originate in New Mexico
3. RAG queries return misleading risk assessments during heavy smoke transport events

This spec defines the classification pipeline that prevents all three problems.

---

## Active Fire Detection Classification (FIRMS)

### Step 1 — Expanded Bounding Box

FIRMS is queried with an expanded bounding box that extends ~3° beyond Colorado borders:

```
Colorado bbox:  -109.06, 36.99, -102.04, 41.00
Expanded bbox:  -112.06, 33.99, -99.04,  44.00
```

This captures fires in border regions of New Mexico, Utah, Wyoming, Arizona, Kansas, and
Oklahoma that may transport smoke into Colorado.

### Step 2 — In-State Check

For every FIRMS point:
```sql
SELECT EXISTS (
  SELECT 1 FROM state_boundaries
  WHERE state_abbr = 'CO'
    AND ST_Within(
      ST_SetSRID(ST_MakePoint(:lon, :lat), 4326),
      boundary
    )
)
```

- Result `true` → `is_colorado = true`, `origin_state = 'CO'`
- Result `false` → proceed to Step 3

### Step 3 — Origin State Lookup

```sql
SELECT state_abbr, state_name FROM state_boundaries
WHERE ST_Within(
  ST_SetSRID(ST_MakePoint(:lon, :lat), 4326),
  boundary
)
LIMIT 1;
```

If no state matches (offshore, ocean, or border edge): `origin_state = 'UNKNOWN'`

### Step 4 — Smoke Transport Inference

For out-of-state detections, set `smoke_transport_likely = true` when:
- Detection is within 200 miles of Colorado border, AND
- NOAA forecast wind direction points toward Colorado (within 45° of bearing to CO), AND
- Detection has `confidence = 'high'` and `frp > 10 MW`

### Step 5 — Impact Type

| Condition | `impact_type` |
|---|---|
| `is_colorado = true` | `"fire"` |
| `is_colorado = false` AND `smoke_transport_likely = true` | `"smoke_only"` |
| `is_colorado = false` AND `smoke_transport_likely = false` | `"none"` |

Only detections with `impact_type = "fire"` are included in risk score computation.

---

## Smoke Transport Classification (NOAA HMS)

Smoke transport is independent of active fire pixels. A fire in New Mexico can blanket
southern Colorado in heavy smoke with zero FIRMS pixels inside Colorado.

### HMS Plume Processing

1. Download daily NOAA HMS smoke polygon GeoJSON:
   ```
   https://satepsanone.nesdis.noaa.gov/pub/FIRE/web/HMS/Smoke_Polygons/GeoJSON/{YYYY}/{YYYY_MM_DD}.json
   ```

2. Intersect all plume polygons with Colorado boundary:
   ```sql
   SELECT p.* FROM hms_plumes p, state_boundaries s
   WHERE s.state_abbr = 'CO'
     AND ST_Intersects(p.plume_geom, s.boundary)
   ```

3. For each intersecting plume, compute centroid and look up origin state:
   ```sql
   SELECT state_abbr, state_name FROM state_boundaries
   WHERE ST_Within(ST_Centroid(:plume_geom), boundary)
   ```

4. If centroid is outside Colorado → `is_colorado_origin = false`, tag origin state.

5. Compute which Colorado counties are affected:
   ```sql
   -- Use county boundary table (seeded from Census TIGER county data)
   SELECT county_name FROM co_counties
   WHERE ST_Intersects(boundary, :plume_geom)
   ```

6. Upsert into `smoke_events` table.

### AirNow Smoke Inference

When no HMS plume polygon covers a cell but AQI exceeds threshold:

```
IF aqi_observations.aqi > 100         -- Unhealthy for Sensitive Groups
AND aqi_observations.pm25 > 35.4      -- EPA 24-hr standard threshold
AND no active_fire_detections within 50 miles of cell center with is_colorado = true
→ smoke_inferred = true in aqi_observations
→ Publish 'out_of_state_smoke' event to FeedService (if trending upward)
```

---

## Risk Score Impact Rules

| Detection Type | Risk Score Impact |
|---|---|
| `is_colorado = true`, `impact_type = 'fire'` | **Full weight** — all variables apply |
| `is_colorado = true`, elevated AQI, `smoke_inferred = true` | **Small bump** — `days_since_rain` proxy reduced by 20%; `smoke_present` flag set on H3 cell |
| `is_colorado = false`, any `impact_type` | **Zero impact** — excluded from formula entirely |
| HMS plume over Colorado, out-of-state origin | **Zero impact** — displayed as overlay; AQI surfaced in sidebar |

The risk score (`current_risk_score`) measures **ignition and fire spread risk**, not air
quality. This distinction must be documented in all UI tooltips.

---

## Map Representation

| Event | Layer | Style | Tooltip |
|---|---|---|---|
| Colorado active fire | `active-fires-co` | Red/orange point heatmap (frp weight) | "Active fire — Colorado" |
| Out-of-state fire | `active-fires-oos` | Purple/grey point (smaller radius) | "Fire origin: [State] — excluded from CO risk score" |
| HMS smoke plume (CO intersect) | `smoke-plumes` | Semi-transparent grey-brown fill (opacity by density) | "Smoke plume from [State] — [density] density. May affect air quality in [counties]." |
| Elevated AQI (no fire pixel) | `aqi-alerts` | Yellow-orange county boundary outline | "AQI [value] — smoke likely from [origin] based on wind patterns" |

### MapLibre Layer Specs

```js
// Out-of-state fire points — distinct from in-state
map.addLayer({
  id: 'active-fires-oos',
  type: 'circle',
  source: 'active-fires',
  filter: ['==', ['get', 'isColorado'], false],
  paint: {
    'circle-radius': ['interpolate', ['linear'], ['get', 'frp'], 0, 4, 100, 12],
    'circle-color': '#7b2d8b',    // purple
    'circle-opacity': 0.75,
    'circle-stroke-width': 1,
    'circle-stroke-color': '#4a0072'
  }
});

// Smoke plumes — grey-brown semi-transparent fill
map.addLayer({
  id: 'smoke-plumes',
  type: 'fill',
  source: 'smoke-plumes',
  paint: {
    'fill-color': [
      'match', ['get', 'density'],
      'heavy',  '#7a6a5a',
      'medium', '#9a8a7a',
                '#b8a898'   // coarse default
    ],
    'fill-opacity': [
      'match', ['get', 'density'],
      'heavy',  0.55,
      'medium', 0.40,
                0.25
    ]
  }
});
```

---

## `OriginClassifierService.cs`

This service is the single point of truth for all origin classification. It is called by
`FirmsService` during every data refresh.

**Interface:**
```csharp
public interface IOriginClassifierService
{
    Task<OriginClassification> ClassifyPointAsync(double lat, double lon);
    Task<OriginClassification> ClassifyPlumeAsync(Geometry plumeGeometry);
}

public record OriginClassification(
    bool IsColorado,
    string OriginState,     // 2-letter abbr
    string OriginStateName,
    bool SmokeTransportLikely,
    string ImpactType       // "fire", "smoke_only", "none"
);
```

**Performance:** Cache state boundary geometries in memory at startup. PostGIS
`ST_Within` is fast for point-in-polygon when the boundary index is loaded. Expect
<5ms per classification.

---

## Feed Events for Out-of-State Events

Out-of-state detections publish to `FeedService` as:

```json
// out_of_state_fire
{
  "type": "out_of_state_fire",
  "severity": "warning",
  "originState": "NM",
  "originStateName": "New Mexico",
  "detail": "High-confidence fire detection 45 miles south of CO border near Taos, NM (FRP: 42 MW)",
  "timestamp": "2026-04-03T14:12:00Z"
}

// out_of_state_smoke
{
  "type": "out_of_state_smoke",
  "severity": "warning",
  "originState": "NM",
  "originStateName": "New Mexico",
  "detail": "Heavy HMS smoke plume from New Mexico detected over southern Colorado",
  "timestamp": "2026-04-03T14:15:00Z",
  "impactedCounties": ["Las Animas", "Huerfano", "Pueblo"]
}
```

---

## UI Copy Guidelines

All tooltips and sidebar text for out-of-state events must include:

> "This [fire / smoke] originates outside Colorado and is **not included in the local
> wildfire risk score**. The risk score reflects ignition and fire spread conditions
> within Colorado only. Smoke from out-of-state fires may affect air quality — check
> the AQI indicator for current conditions."
