# Data Sources

> **SDD Note:** All data sources used in this project are listed here with their formats,
> access methods, update frequencies, and ingestion notes. Register for API keys before
> starting Phase 1.

---

## Source Registry

| Source | Data | Format | Update Freq | API Key? |
|---|---|---|---|---|
| [MTBS](https://www.mtbs.gov/direct-download) | Fire perimeters + burn severity (1984–present) | Shapefile (NAD83) | Annual | No — bulk download |
| [NIFC](https://www.nifc.gov/fire-information/statistics) | Historical fire statistics | CSV | Annual | No |
| [NASA FIRMS](https://firms.modaps.eosdis.nasa.gov/) | Active fire detections (VIIRS/MODIS) | CSV / GeoJSON | Near real-time | Yes (free) |
| [NOAA Weather.gov](https://api.weather.gov) | Forecasts, Red Flag Warnings | JSON REST | Hourly | No |
| [WFAS Fuel Moisture](https://www.wfas.net/) | Dead/live fuel moisture % | HTML / CSV | Daily | No |
| [LANDFIRE](https://landfire.gov/getdata.php) | Vegetation, fuel models, canopy | GeoTIFF raster | Periodic | No — bulk download |
| [InciWeb](https://inciweb.nwcg.gov/) | Incident reports (unstructured text) | HTML / RSS | As incidents occur | No |
| [USFS FACTS](https://data.fs.usda.gov/geodata/edw/datasets.php) | Fire occurrence points | Shapefile | Annual | No |
| [NOAA HMS](https://www.ospo.noaa.gov/Products/land/hms.html) | Satellite smoke plume polygons | GeoJSON / KML | Daily | No |
| [EPA AirNow](https://www.airnowapi.org/) | Real-time AQI + PM2.5 by location | JSON REST | Hourly | Yes (free) |
| [Census TIGER/Line](https://www.census.gov/geographies/mapping-files/time-series/geo/tiger-line-file.html) | US state boundary polygons | Shapefile | One-time seed | No |

**Colorado bounding box:** `-109.06, 36.99, -102.04, 41.00` (west, south, east, north)

---

## MTBS — Monitoring Trends in Burn Severity

**Download:** https://www.mtbs.gov/direct-download
**File:** "Burned Area Boundaries" Shapefile — `mtbs_perimeter_data.zip` (~150 MB, full CONUS)
**Coordinate System:** NAD83 (EPSG:4269) — **must reproject to WGS84 (EPSG:4326) via ProjNet**

**Key Shapefile fields:**

| Field | Type | Description |
|---|---|---|
| `Fire_ID` | string | Unique ID e.g. `CO3945010470920010614` |
| `Fire_Name` | string | Human-readable name |
| `Fire_Year` | int | Year of fire |
| `Ig_Date` | date | Ignition date |
| `BurnBndAc` | float | Burned area in acres |
| `Low`, `Mod`, `High`, `VHigh` | float | dNBR class acreages |
| geometry | MultiPolygon | Fire perimeter |

**Colorado subset:** Filter using Colorado bounding box during Shapefile read, then confirm
with `ST_Intersects` against the Colorado state boundary polygon in PostGIS.

**Ingestion class:** `MtbsIngester.cs`
**Target table:** `fire_events` with `source = 'MTBS'`

---

## NIFC — National Interagency Fire Center

**Download:** https://www.nifc.gov/fire-information/statistics
**Format:** CSV with annual summary stats; GeoJSON/Shapefile for fire locations
**Use:** Supplement MTBS with fires < 1,000 acres and pre-1984 context

**Ingestion class:** `NifcIngester.cs` using `CsvHelper`
**Target table:** `fire_events` with `source = 'NIFC'`

---

## NASA FIRMS — Active Fire Detections

**Registration:** https://firms.modaps.eosdis.nasa.gov/api/area/
**API Key:** Free; required in request URL

**Key endpoint:**
```
GET https://firms.modaps.eosdis.nasa.gov/api/area/csv/{MAP_KEY}/VIIRS_SNPP_NRT/{west},{south},{east},{north}/{days}
```

Use an expanded bounding box that includes neighboring states for out-of-state detection:
`-112.0,34.0,-99.0,43.0` (covers CO + border regions of NM, UT, WY, AZ, KS, OK)

**CSV fields returned:** `latitude`, `longitude`, `bright_ti4`, `scan`, `track`,
`acq_date`, `acq_time`, `satellite`, `instrument`, `confidence`, `frp`, `daynight`

**Caching:** Store in `active_fire_detections`. Refresh every 15 minutes via `PeriodicTimer`.
FIRMS NRT data updates every ~3 hours for VIIRS; daily for MODIS.

**Ingestion class:** `FirmsService.cs` with `CsvHelper` + `OriginClassifierService`
**Target table:** `active_fire_detections`

---

## NOAA Weather.gov

**No API key required.** Public REST API.

**Step 1 — Get grid point:**
```
GET https://api.weather.gov/points/{lat},{lon}
```
Returns `forecastGridData` URL and `county` zone.

**Step 2 — Get hourly forecast:**
```
GET https://api.weather.gov/gridpoints/{office}/{X},{Y}/forecast/hourly
```
Returns `windSpeed`, `relativeHumidity`, `probabilityOfPrecipitation` per hour.

**Step 3 — Active alerts:**
```
GET https://api.weather.gov/alerts/active?area=CO&event=Red%20Flag%20Warning
```

**Rate limits:** Soft limit ~10 req/s. Set `User-Agent: CoWildfireAnalyzer/1.0 (contact@email.com)` (required by NOAA TOS).

**Caching:** Cache per H3-6 cell for 1 hour (the grid points endpoint response should also
be cached — NOAA throttles this endpoint).

**Service class:** `NoaaService.cs` with Polly retry

---

## WFAS — Wildland Fire Assessment System (Fuel Moisture)

**URL:** https://www.wfas.net/
**Format:** HTML tables / downloadable CSV
**Data:** Dead (1-hr, 10-hr, 100-hr) and live fuel moisture percentages by station

**Ingestion approach:** `AngleSharp` HTML parsing or CSV download
**Update frequency:** Daily (stations report once daily)

---

## LANDFIRE

**URL:** https://landfire.gov/getdata.php
**Format:** GeoTIFF rasters (large files — ~GB for full Colorado)
**Data:** Vegetation type (`EVT`), fuel model (`FBFM40`), canopy cover, canopy height

**Phase:** Deferred to Phase 5. Requires GDAL.NET for raster processing.
**Target fields:** `h3_cells.vegetation_type`, `h3_cells.slope_degrees`

---

## InciWeb

**URL:** https://inciweb.nwcg.gov/
**RSS Feed:** https://inciweb.nwcg.gov/feeds/rss/incidents/
**Format:** HTML per incident; RSS for discovery

**Ingestion approach:**
1. Parse RSS to get current + recent Colorado incidents
2. Fetch each incident HTML page with `HttpClient`
3. Parse with `AngleSharp` to extract narrative text, location, cause, acreage
4. Chunk text into ~500-token segments with 50-token overlap
5. Embed each chunk with `nomic-embed-text` (768-dim)
6. Upsert into Qdrant collection `wildfire_docs`

**Ingestion class:** `InciwebIngester.cs`
**Target store:** Qdrant (not PostgreSQL)

---

## NOAA HMS — Hazard Mapping System

**URL:** https://www.ospo.noaa.gov/Products/land/hms.html
**Format:** Daily GeoJSON/KML smoke plume polygons (coarse / medium / heavy density)
**No API key required.**

**Ingestion approach:**
1. Fetch daily GeoJSON URL for current date
2. Intersect plume polygons with Colorado boundary (`ST_Intersects`)
3. For plumes with centroid outside Colorado, look up origin state via `state_boundaries`
4. Store in `smoke_events` table

**Service class:** `HmsService.cs`

---

## EPA AirNow

**Registration:** https://docs.airnowapi.org/
**API Key:** Free; required in request URL

**Key endpoint:**
```
GET https://www.airnowapi.org/aq/observation/latLong/current/?latitude={lat}&longitude={lon}&distance=25&format=application/json&API_KEY={key}
```

**Data returned:** AQI, PM2.5, O3 per observation station within `distance` miles.

**Rate limits:** ~500 requests/hour on free tier.
**Caching strategy:** Cache per H3-6 cell center for 1 hour. Query resolution 6 only
(~220 cells for Colorado) — never query per H3-8 cell.

**Service class:** `AirNowService.cs`
**Target table:** `aqi_observations`

---

## Census TIGER/Line — State Boundaries

**URL:** https://www.census.gov/geographies/mapping-files/time-series/geo/tiger-line-file.html
**File:** `tl_2023_us_state.zip` — state boundary Shapefiles (~50 MB)
**Coordinate System:** NAD83 (EPSG:4269) — reproject to WGS84

**Ingestion:** One-time seed into `state_boundaries` table.
Used by `OriginClassifierService` for `ST_Within` lookups.

---

## Idempotency

All ingestion runs check `ingestion_log` before processing:
```sql
INSERT INTO ingestion_log (source, dataset_key, status)
VALUES ('MTBS', 'mtbs_perimeter_data_2024.zip_sha256_abc123', 'pending')
ON CONFLICT (source, dataset_key) DO NOTHING
RETURNING id;
-- If no row returned, data already loaded — skip.
```

On success, update `status = 'success'` and `completed_at = NOW()`.
On failure, update `status = 'failed'` and `error_message`.
