# API Reference

> **SDD Note:** These contracts are the source of truth. Backend implements them exactly.
> Frontend consumes them exactly. Any deviation must be documented here first.

All responses use **camelCase JSON**. GeoJSON follows RFC 7946.
CORS must allow: `http://localhost:5173` and `http://localhost:3000`

---

## Endpoints Summary

| Method | Path | Description |
|---|---|---|
| GET | `/api/risk-grid` | H3 hex cells with risk scores as GeoJSON |
| GET | `/api/fire-history` | Historical fire perimeters as GeoJSON |
| POST | `/api/query` | RAG natural language query |
| GET | `/api/active-fires` | NASA FIRMS live fire detections |
| GET | `/api/smoke-plumes` | NOAA HMS smoke plume polygons |
| GET | `/api/feed` | SSE live event stream |
| GET | `/api/health` | Liveness + readiness check |
| GET | `/api/cell-at-point` | H3 cell properties at a given lat/lon |
| GET | `/api/risk-history/{h3Index}` | Hourly risk score history for a cell |

---

## `GET /api/risk-grid`

Returns the H3 risk grid as a GeoJSON FeatureCollection. Backend pre-serializes H3 cell
boundaries using `H3.net GetCellBoundary()` — frontend receives complete polygons.

**Query Parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `resolution` | int | `6` | H3 resolution (6 or 8) |
| `bounds` | string | Colorado bbox | `west,south,east,north` |
| `minRisk` | float | `0` | Exclude cells below this score |

> **Required for resolution 8:** Always supply `bounds`. Full res-8 payload is ~17.5 MB.

**Response (200 OK):**

```json
{
  "type": "FeatureCollection",
  "metadata": {
    "resolution": 6,
    "cellCount": 214,
    "generatedAt": "2026-04-03T14:22:00Z",
    "riskScoreUpdatedAt": "2026-04-03T14:00:00Z"
  },
  "features": [{
    "type": "Feature",
    "geometry": {
      "type": "Polygon",
      "coordinates": [[
        [-105.1234, 39.5678], [-105.0987, 39.5432], [-105.0901, 39.5190],
        [-105.1099, 39.5050], [-105.1346, 39.5296], [-105.1432, 39.5538],
        [-105.1234, 39.5678]
      ]]
    },
    "properties": {
      "h3Index": "8629a0807ffffff",
      "resolution": 6,
      "centerLat": 39.5364,
      "centerLon": -105.1167,
      "riskScore": 7.42,
      "riskCategory": "High",
      "redFlagWarning": false,
      "firesLast20yr": 3,
      "totalAcresBurned": 12450.5,
      "avgBurnSeverity": 312.4,
      "yearsSinceLastFire": 8,
      "lastFireYear": 2018,
      "windSpeedMph": 28.5,
      "relativeHumidityPct": 12.0,
      "fuelMoisturePct": 8.5,
      "vegetationType": "Shrub/Scrub",
      "slopeDegrees": 18.2,
      "riskScoreUpdatedAt": "2026-04-03T14:00:00Z"
    }
  }]
}
```

**`riskCategory` values:** `"Very Low"`, `"Low"`, `"Moderate"`, `"High"`, `"Very High"`, `"Extreme"`

---

## `GET /api/fire-history`

Returns historical fire perimeters as a GeoJSON FeatureCollection.

**Query Parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `bounds` | string | Colorado bbox | `west,south,east,north` |
| `startYear` | int | `1984` | Filter from year |
| `endYear` | int | current year | Filter to year |
| `minAcres` | float | `0` | Exclude fires smaller than this |
| `source` | string | all | `MTBS`, `NIFC`, or `USFS` |

**Response (200 OK):**

```json
{
  "type": "FeatureCollection",
  "metadata": {
    "totalFires": 89,
    "totalAcresBurned": 892345.0,
    "yearRange": [1984, 2024],
    "source": ["MTBS", "NIFC"]
  },
  "features": [{
    "type": "Feature",
    "geometry": { "type": "MultiPolygon", "coordinates": [[[[...]]]] },
    "properties": {
      "fireId": "CO3945010470920010614",
      "fireName": "Hayman Fire",
      "year": 2002,
      "startDate": "2002-06-08",
      "endDate": "2002-07-02",
      "acresBurned": 137760.0,
      "avgDnbr": 487.2,
      "maxDnbr": 1050.0,
      "source": "MTBS",
      "county": "Park",
      "state": "CO"
    }
  }]
}
```

---

## `POST /api/query`

Accepts a natural language question with location context, runs the RAG pipeline, and
returns a grounded response citing retrieved source documents.

**Request Body:**

```json
{
  "location": { "lat": 39.5364, "lon": -105.1167 },
  "h3Index": "8629a0807ffffff",
  "question": "What is the historical fire risk for this area and what conditions are making it dangerous right now?",
  "resolution": 6
}
```

**Response (200 OK):**

```json
{
  "answer": "This cell near the Foothills southwest of Denver has experienced 3 significant fires in the past 20 years...",
  "sources": [{
    "chunkId": "inciweb-buffalo-creek-2018-001",
    "documentTitle": "Buffalo Creek Fire — InciWeb Incident Report",
    "excerpt": "The Buffalo Creek Fire burned under extreme red flag conditions with sustained winds of 40 mph...",
    "similarity": 0.91,
    "sourceUrl": "https://inciweb.nwcg.gov/incident/..."
  }],
  "cellStats": {
    "h3Index": "8629a0807ffffff",
    "riskScore": 7.42,
    "riskCategory": "High",
    "firesLast20yr": 3,
    "totalAcresBurned": 12450.5,
    "avgBurnSeverity": 312.4,
    "yearsSinceLastFire": 8
  },
  "currentConditions": {
    "windSpeedMph": 28.5,
    "relativeHumidityPct": 12.0,
    "fuelMoisturePct": 8.5,
    "droughtIndex": 3.8,
    "daysSinceRain": 21,
    "redFlagWarning": false,
    "forecastSummary": "Sunny, windy, gusts up to 45 mph expected this afternoon.",
    "dataSource": "NOAA Weather.gov",
    "retrievedAt": "2026-04-03T13:58:00Z"
  },
  "processingMs": 1842,
  "modelUsed": "llama3.2",
  "chunksRetrieved": 5
}
```

**Error Response:**

```json
{ "error": "LLM inference timeout", "code": "LLM_TIMEOUT" }
```

---

## `GET /api/active-fires`

Returns NASA FIRMS active fire detections for the expanded Colorado region (including
border areas). Each point is tagged with origin classification.

**Query Parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `bounds` | string | Colorado + buffer | `west,south,east,north` |
| `hoursBack` | int | `24` | Hours of detections (max 72) |
| `minConfidence` | string | `nominal` | `low`, `nominal`, `high` |

**Response (200 OK):**

```json
{
  "type": "FeatureCollection",
  "metadata": {
    "detectionCount": 47,
    "coloradoCount": 14,
    "outOfStateCount": 33,
    "hoursBack": 24,
    "retrievedAt": "2026-04-03T14:20:00Z",
    "dataSource": "NASA FIRMS VIIRS/MODIS",
    "cacheAgeSeconds": 420
  },
  "features": [
    {
      "type": "Feature",
      "geometry": { "type": "Point", "coordinates": [-107.8234, 37.9456] },
      "properties": {
        "brightness": 312.5,
        "frp": 8.4,
        "confidence": "high",
        "satellite": "VIIRS_SNPP",
        "acquiredAt": "2026-04-03T11:36:00Z",
        "dayNight": "D",
        "isColorado": true,
        "originState": "CO",
        "originStateName": "Colorado",
        "smokeTransportLikely": false,
        "impactType": "fire"
      }
    },
    {
      "type": "Feature",
      "geometry": { "type": "Point", "coordinates": [-106.2, 36.8] },
      "properties": {
        "brightness": 380.1,
        "frp": 42.3,
        "confidence": "high",
        "satellite": "VIIRS_SNPP",
        "acquiredAt": "2026-04-03T11:36:00Z",
        "dayNight": "D",
        "isColorado": false,
        "originState": "NM",
        "originStateName": "New Mexico",
        "smokeTransportLikely": true,
        "impactType": "smoke_only"
      }
    }
  ]
}
```

---

## `GET /api/smoke-plumes`

Returns NOAA HMS smoke plume polygons intersecting Colorado for the current day.

**Query Parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `date` | string | today | ISO date `YYYY-MM-DD` |
| `minDensity` | string | `coarse` | `coarse`, `medium`, `heavy` |

**Response (200 OK):**

```json
{
  "type": "FeatureCollection",
  "metadata": {
    "date": "2026-04-03",
    "source": "NOAA HMS",
    "retrievedAt": "2026-04-03T14:00:00Z",
    "plumeCount": 3
  },
  "features": [{
    "type": "Feature",
    "geometry": { "type": "MultiPolygon", "coordinates": [[[[...]]]] },
    "properties": {
      "density": "heavy",
      "plumeDate": "2026-04-03",
      "originState": "NM",
      "originStateName": "New Mexico",
      "isColorado": false,
      "coloradoCountiesAffected": ["Las Animas", "Huerfano", "Pueblo"],
      "smokeDescription": "Heavy smoke from Cerro Pelado Fire complex, NM"
    }
  }]
}
```

---

## `GET /api/feed` — SSE Live Event Stream

Server-Sent Events endpoint. The client connects once; the server pushes events as they
occur. See [live-feed.md](live-feed.md) for the full frontend design.

**Response:** `Content-Type: text/event-stream`

**Event format (each line):**

```
event: {event_type}
data: {JSON object}

```

**Event examples:**

```
event: data_fetch
data: {"type":"data_fetch","source":"NASA FIRMS","detail":"Fetched 14 active detections for Colorado","timestamp":"2026-04-03T14:20:00Z","severity":"info"}

event: risk_score
data: {"type":"risk_score","h3Index":"8629a0807ffffff","detail":"Risk score updated: 7.4 → 8.1 (High → Very High)","county":"Larimer","timestamp":"2026-04-03T14:00:12Z","severity":"warning"}

event: alert
data: {"type":"alert","source":"NOAA","detail":"Red Flag Warning issued for Larimer and Boulder counties","timestamp":"2026-04-03T14:10:00Z","severity":"critical"}

event: out_of_state_smoke
data: {"type":"out_of_state_smoke","originState":"NM","detail":"Smoke plume from New Mexico detected over southern Colorado","timestamp":"2026-04-03T14:15:00Z","severity":"warning","impactedCounties":["Las Animas","Huerfano"]}

event: heartbeat
data: {"type":"heartbeat","timestamp":"2026-04-03T14:20:30Z"}
```

**Event types:**

| Type | Severity | Description |
|---|---|---|
| `data_fetch` | `info` | NOAA, FIRMS, WFAS data retrieved |
| `risk_score` | `info` / `warning` / `critical` | Cell risk score changed |
| `rag_query` | `info` | RAG query completed |
| `report_ingested` | `info` | New InciWeb document embedded |
| `alert` | `warning` / `critical` | Red Flag Warning or restriction issued |
| `out_of_state_fire` | `info` / `warning` | Fire origin confirmed outside CO |
| `out_of_state_smoke` | `info` / `warning` | Smoke transport from another state |
| `heartbeat` | — | Keep-alive every 30 seconds |

---

## `GET /api/cell-at-point`

Returns the H3 cell properties for a given geographic point. Used by the frontend address search to identify which H3 cell contains a searched address.

**Query Parameters:**

| Param | Type | Required | Description |
|---|---|---|---|
| `lat` | float | Yes | Latitude (WGS84) |
| `lon` | float | Yes | Longitude (WGS84) |
| `resolution` | int | No | H3 resolution; default `6` |

**Response (200 OK):**

```json
{
  "h3Index": "8629a0807ffffff",
  "resolution": 6,
  "centerLat": 39.5364,
  "centerLon": -105.1167,
  "riskScore": 7.42,
  "riskCategory": "High",
  "redFlagWarning": false,
  "firesLast20yr": 3,
  "totalAcresBurned": 12450.5,
  "avgBurnSeverity": 312.4,
  "yearsSinceLastFire": 8,
  "lastFireYear": 2018,
  "windSpeedMph": 28.5,
  "relativeHumidityPct": 12.0,
  "fuelMoisturePct": 8.5,
  "riskScoreUpdatedAt": "2026-04-03T14:00:00Z"
}
```

**Error (404 Not Found):** Point is outside Colorado or no cell exists at that resolution.

```json
{ "error": "No cell found at this location", "code": "CELL_NOT_FOUND" }
```

---

## `GET /api/risk-history/{h3Index}`

Returns hourly risk score history for a specific H3 cell. Powers the Phase 4 Chart.js time-series chart in the RAG sidebar.

**Path Parameters:**

| Param | Type | Description |
|---|---|---|
| `h3Index` | string | H3 cell index string |

**Query Parameters:**

| Param | Type | Default | Description |
|---|---|---|---|
| `hours` | int | `168` | Hours of history to return (max 2160 = 90 days) |

**Response (200 OK):**

```json
{
  "h3Index": "8629a0807ffffff",
  "resolution": 6,
  "count": 168,
  "dataPoints": [
    {
      "scoredAt": "2026-04-03T14:00:00Z",
      "riskScore": 7.42,
      "riskCategory": "High",
      "windSpeedMph": 28.5,
      "relativeHumidityPct": 12.0,
      "fuelMoisturePct": 8.5,
      "weatherSource": "RAWS"
    },
    {
      "scoredAt": "2026-04-03T13:00:00Z",
      "riskScore": 6.91,
      "riskCategory": "High",
      "windSpeedMph": 22.0,
      "relativeHumidityPct": 18.0,
      "fuelMoisturePct": 8.5,
      "weatherSource": "NOAA"
    }
  ]
}
```

**Error (404 Not Found):** No history exists for the cell.

```json
{ "error": "No history found for cell", "code": "NO_HISTORY" }
```

---

## `GET /api/health`

**Response (200 OK):**

```json
{
  "status": "healthy",
  "timestamp": "2026-04-03T14:22:00Z",
  "dependencies": {
    "postgres": "healthy",
    "qdrant": "healthy",
    "ollama": "healthy"
  },
  "version": "1.0.0-phase1"
}
```
