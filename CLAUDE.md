# CLAUDE.md — Colorado Wildfire RAG Analyzer

## What This App Does

Full-stack wildfire risk analysis for Colorado. A user enters a home address; the map flies to that location and shows the risk score for the H3 hex cell covering it. Every cell is scored 0–10 using a weighted formula combining fire history, live weather, fuel moisture, terrain, drought index, and bark beetle kill severity — refreshed hourly. Users can click any cell to open a RAG sidebar and ask plain-English questions answered by retrieved incident documents + live conditions. A live SSE feed panel streams incoming data events, NWS alerts, out-of-state detections, and risk score changes in real time.

**Target users:** Colorado WUI homeowners, fire professionals / emergency managers, insurance analysts, researchers.

---

## Dev Stack Startup

```powershell
.\start-dev.ps1
```

Order: Docker → kill existing backend → `dotnet build` → `dotnet run --no-build` (new window) → `npm run dev` (new window).

Manual equivalents:
```powershell
docker compose up -d

# Backend
Get-Process -Name "CoWildfireApi" -ErrorAction SilentlyContinue | Stop-Process -Force
cd backend
dotnet build CoWildfireApi.sln
dotnet run --project CoWildfireApi\CoWildfireApi.csproj --no-build

# Frontend
cd frontend
npm run dev
```

## Ports

| Service | Port |
|---|---|
| PostgreSQL + PostGIS | 5432 |
| Qdrant REST | 6333 |
| Qdrant gRPC | 6334 |
| ASP.NET Core API | 5000 / 5001 |
| Vite dev server | 3000 (also allows 5173) |

CORS is configured for `http://localhost:5173` and `http://localhost:3000`.

---

## Architecture

```
Data Sources (MTBS, FIRMS, NOAA, HMS, AirNow, InciWeb, TIGER, CDOT)
    ↓
C# Ingestion (MtbsIngester, InciwebIngester, TigerSeeder)
    ├── Structured → PostgreSQL + PostGIS
    └── Unstructured → chunked + embedded (nomic-embed-text) → Qdrant

ASP.NET Core 8 Web API (controller-based)
    ├── /api/risk-grid         GET  — H3 fill GeoJSON, pre-serialized
    ├── /api/fire-history      GET  — burn perimeters GeoJSON
    ├── /api/active-fires      GET  — FIRMS points (in-state + out-of-state)
    ├── /api/smoke-plumes      GET  — HMS smoke polygons
    ├── /api/county-bounds     GET  — CO county borders GeoJSON
    ├── /api/query             POST — RAG: embed → Qdrant → llama3.2 → response
    ├── /api/feed              GET  — SSE live event stream
    ├── /api/cell-at-point     GET  — H3 cell lookup by lat/lng
    ├── /api/risk-history      GET  — 90-day hourly snapshots for a cell
    └── /api/health            GET  — liveness check

Vanilla JS + Vite Frontend (MapLibre GL 4.x)
    ├── H3 risk heatmap fill layer (zoom-responsive opacity)
    ├── Fire history burn perimeter layer (toggleable)
    ├── In-state active fire layer (red/orange, FIRMS)
    ├── Out-of-state fire layer (purple, separate, NEVER mixed)
    ├── Smoke plume layer (grey-brown HMS polygons)
    ├── County border layer
    ├── Terrain hillshade (AWS Terrain Tiles, dark-tuned)
    ├── Cell click → RAG sidebar (Chart.js time-series + RAG response)
    └── SSE live feed panel (right, 320px fixed, filter/pause/clear)
```

---

## Backend File Map

### Controllers (`backend/CoWildfireApi/Controllers/`)
| File | Route |
|---|---|
| `RiskController.cs` | GET /api/risk-grid |
| `FireHistoryController.cs` | GET /api/fire-history |
| `QueryController.cs` | POST /api/query |
| `ActiveFiresController.cs` | GET /api/active-fires |
| `SmokePlumesController.cs` | GET /api/smoke-plumes |
| `FeedController.cs` | GET /api/feed (SSE) |
| `CountyBoundsController.cs` | GET /api/county-bounds |
| `HealthController.cs` | GET /api/health |

### Services (`backend/CoWildfireApi/Services/`)
| File | Purpose |
|---|---|
| `H3GridService.cs` | Generate H3 res-6/8 grid; pre-serialize boundaries |
| `RiskScoringService.cs` | Weighted risk formula (0–10) |
| `RiskScoringBackgroundService.cs` | Hourly refresh via `PeriodicTimer` |
| `NoaaService.cs` | NOAA weather + Red Flag Warnings; Polly retry; 1-hr cache per H3-6 |
| `RawsService.cs` | MesoWest RAWS station queries within 50km; fallback to NOAA gridded |
| `FirmsService.cs` | NASA FIRMS CSV parse + origin classification |
| `HmsService.cs` | NOAA HMS smoke plume polygons, CO intersection, county lookup |
| `AirNowService.cs` | EPA AirNow AQI per H3-6 cell; hourly cache |
| `OriginClassifierService.cs` | `ST_Within` point-in-polygon for state boundary classification |
| `RagService.cs` | Semantic Kernel + Ollama (llama3.2) + Qdrant retrieval + weather context |
| `EmbeddingService.cs` | nomic-embed-text (768-dim) via Ollama |
| `FeedService.cs` | **Singleton** — pub/sub channel for all SSE events |
| `FeedPollingBackgroundService.cs` | Polls ingestors on interval |
| `InciwebFeedPoller.cs` | InciWeb RSS polling + HTML parse (named incident tracking for feed) |
| `NifcIncidentPoller.cs` | IRWIN ArcGIS REST — acreage + containment tracking |
| `CdotRssPoller.cs` | CDOT statewide RSS — fire/smoke/evacuation road closures |
| `DroughtService.cs` | PDSI / drought index per cell |

### Ingestion (`backend/CoWildfireApi/Ingestion/`)
| File | Purpose |
|---|---|
| `MtbsIngester.cs` | MTBS Shapefile → PostGIS; NAD83 → WGS84 (ProjNet); fire-to-cell intersection |
| `InciwebIngester.cs` | RSS → HTML (AngleSharp) → chunk → embed → Qdrant (for RAG, not feed) |
| `TigerSeeder.cs` | Census TIGER/Line state + county boundary one-time seed |

### Models (`backend/CoWildfireApi/Models/`)
`H3Cell`, `FireEvent`, `FireEventH3Intersection`, `H3RiskHistory`, `ActiveFireDetection`, `SmokeEvent`, `CoCounty`, `StateBoundary`, `AqiObservation`, `IngestionLog`, `LiveFeedEvent`, `OriginClassification`, `QueryModels`, `FeedItem`

---

## Frontend File Map

### Core (`frontend/src/`)
| File | Purpose |
|---|---|
| `main.js` | Entry point — init map, sidebar, feed, info panel; legend filter wiring; RAG send button |
| `map.js` | MapLibre GL init centered on Colorado; region click/hover handlers; layer insertion hooks |
| `api.js` | Fetch wrappers for all backend endpoints |
| `config.js` | `API_BASE`, `COLORADO_CENTER/ZOOM`, `RISK_COLORS` dict, `riskColorExpression()` |
| `sidebar.js` | Cell stats, Chart.js time-series chart, RAG response (marked + DOMPurify), actionable guidance |
| `feed.js` | EventSource → SSE cards by type; filter buttons; pause/clear; max 50 cards (auto-prune) |
| `info.js` | Hover info panel (top-right) |

### Layers (`frontend/src/layers/`)
| File | Purpose |
|---|---|
| `riskGrid.js` | H3 fill (color scale 0→10); zoom-responsive opacity (0.80/0.65/0.45); pulse layer (Extreme only, 0.10→0.55 cycle); high-ring line layer (High/Very High/Extreme); risk count badges; legend click-to-filter |
| `activeFires.js` | FIRMS in-state points — red/orange weighted by FRP |
| `outOfStateFires.js` | FIRMS out-of-state points — purple/grey circles (smaller, distinct) |
| `smokePlumes.js` | HMS smoke polygons — grey-brown semi-transparent fill (coarse/medium/heavy) |
| `countyBorders.js` | Colorado county outlines — light grey stroke |

---

## Database Schema (Key Tables)

| Table | Contents | Refresh |
|---|---|---|
| `h3_cells` | 220–3,200 rows; fire history metrics pre-computed; `current_risk_score` refreshed hourly; weather snapshot + RAWS link | Hourly |
| `fire_events` | MTBS/NIFC/USFS incidents; burn severity (dNBR); source attribution | Ingestion-time |
| `fire_event_h3_intersections` | Many-to-many: which fires overlap which cells | Ingestion-time |
| `h3_risk_history` | Hourly risk snapshots, 90-day retention; drives Chart.js sidebar chart | Hourly |
| `active_fire_detections` | FIRMS points; `is_colorado`, `origin_state`, `impact_type` flags | ~15 min |
| `smoke_events` | HMS plume polygons; county intersection; origin state | Daily |
| `aqi_observations` | AirNow AQI + PM2.5 per H3-6; smoke inference flag | Hourly |
| `co_counties` | Colorado county boundaries (Census TIGER) | One-time seed |
| `state_boundaries` | US state boundaries (Census TIGER) | One-time seed |
| `ingestion_log` | Idempotency tracking; unique on `(source, dataset_key)` | Per run |

All geometries: EPSG:4326 (WGS84). PostGIS GIST indexes on all geometry columns. `ST_Within` and `ST_Intersects` used for classification and county lookups.

---

## Risk Scoring Formula

Score 0–10, weighted sum of 8 components refreshed hourly. Primary inputs:
1. Fire history density (burns/decade per cell, normalized)
2. Live wind speed (RAWS primary, NOAA fallback)
3. Relative humidity (RAWS primary, NOAA fallback)
4. Fuel moisture (WFAS daily)
5. Drought index (PDSI)
6. Active fire proximity (FIRMS detections within N km)
7. Terrain slope (LANDFIRE — Phase 5)
8. Bark beetle kill severity modifier

Out-of-state fire detections (`is_colorado = false`) are excluded from the formula entirely.

Risk categories: Low (0–2, green) / Moderate (2–4) / High (4–6, orange) / Very High (6–8, red) / Extreme (8–10, dark red `#7b0000`).

---

## SSE Live Feed Events

Feed streams from `GET /api/feed`. FeedService is a singleton injected into every event-producing service. Event types:

| Type | Description |
|---|---|
| `data_fetch` | Completed data ingestion cycle |
| `risk_score` | Cell risk score updated |
| `rag_query` | User submitted a RAG query |
| `report_ingested` | New InciWeb document embedded into Qdrant |
| `alert` | NWS Red Flag Warning / Fire Weather Watch / Fire Warning |
| `out_of_state_fire` | FIRMS detection classified as non-Colorado |
| `out_of_state_smoke` | HMS plume with non-CO origin affecting CO air quality |
| `heartbeat` | Keep-alive (every 30s) |

Frontend filters default to: Alerts + Risk Score + Out-of-State only. Max 50 cards shown (oldest pruned).

---

## Out-of-State Classification

FIRMS points that fall outside Colorado's TIGER/Line boundary are tagged `is_colorado = false`, stored with `origin_state`, and rendered on the separate purple `outOfStateFires` layer. They never appear on the in-state fire layer and never contribute to `current_risk_score`. HMS plumes that intersect Colorado but originate from another state are similarly tagged on `smoke_events`. This distinction is surfaced in the feed (`out_of_state_fire`, `out_of_state_smoke` event types) and in sidebar tooltips.

---

## RAG Pipeline

1. User submits query via sidebar for a clicked H3 cell
2. `POST /api/query` — `QueryController` → `RagService`
3. `EmbeddingService` embeds query via Ollama `nomic-embed-text` (768-dim)
4. Qdrant similarity search on `wildfire_docs` collection with geographic pre-filter (state, county)
5. Retrieved chunks + live weather context injected into prompt
6. `llama3.2` (local Ollama) generates response with `actionableGuidance` field
7. Response rendered in sidebar via `marked` + `DOMPurify` (safe Markdown)

Qdrant collection: `wildfire_docs`, 768-dim, cosine similarity. Payload fields: `state`, `year`, `source_type`, `county`.

---

## Key Conventions — Read Before Writing Code

- **No React.** Vanilla JS only. No routing, state tree, or component framework.
- **Controller-based API** (not Minimal API) — required for Phase 6 auth middleware.
- **H3 coordinate order:** Backend pocketken.H3 v4 `GetCellBoundary()` → NTS Polygon in `[lng, lat]` GeoJSON order. Frontend h3-js v4 `cellToBoundary()` → `[lat, lng]` — reverse before use.
- **Out-of-state events NEVER affect `current_risk_score`.** Purple layer only. Never mixed into in-state data.
- **FeedService is a singleton.** Inject via DI into all event-producing services. Never register as transient or scoped.
- **H3 polygons are pre-serialized.** The API returns complete GeoJSON. No client-side H3 boundary computation for rendering.
- **NOAA Weather.gov requires `User-Agent` header** — `CoWildfireAnalyzer/1.0 (contact@email.com)`.
- **MTBS is in NAD83 (EPSG:4269).** Always reproject to WGS84 via ProjNet before PostGIS insert (~50m error if skipped).
- **pocketken.H3 NuGet ID is `pocketken.H3`**, not `H3`. The API is NTS-native: `Polyfill.Fill(Geometry, res)`, `cell.GetCellBoundary(geoFactory)`.
- **Qdrant payload is untyped** — deserialize dictionary carefully with `JsonSerializer`.
- **Risk score = ignition/spread risk, NOT air quality.** AQI is a separate indicator. Document this in tooltips.
- **SSE holds one HTTP connection per browser tab.** Server-side idle timeout: 5 min. Frontend `EventSource` reconnects automatically.
- **`ingestion_log` ensures idempotency.** Always write to it; unique on `(source, dataset_key)`.
- **Insert all data layers `beforeId` the first vector symbol layer** so city/road labels always render on top.

---

## External API Keys Required

| Service | Where to Get | Notes |
|---|---|---|
| NASA FIRMS | firms.modaps.eosdis.nasa.gov | Free, ~24h delivery |
| EPA AirNow | docs.airnowapi.org | Free |
| MesoWest / Synoptic | synopticdata.com | Free tier; RAWS primary weather source |

No API key required: NOAA Weather.gov, NOAA HMS, InciWeb RSS, NIFC/WFIGS ArcGIS, CDOT RSS, OpenFreeMap basemap, AWS Terrain Tiles.

---

## appsettings.Development.json Shape

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=co_wildfire;Username=wildfire;Password=wildfire_dev"
  },
  "Qdrant": { "Host": "localhost", "Port": 6334, "CollectionName": "wildfire_docs" },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "ChatModel": "llama3.2",
    "EmbeddingModel": "nomic-embed-text"
  },
  "Cors": { "AllowedOrigins": ["http://localhost:5173", "http://localhost:3000"] }
}
```

---

## Phase Status

| Phase | Status | Summary |
|---|---|---|
| 0 | Complete | Research & architecture |
| 1 | Complete | MTBS ingestion, H3 grid, fire-history + risk-grid endpoints |
| 2 | Complete | NOAA/RAWS weather, risk formula, hourly background refresh |
| 3 | Complete | Qdrant, InciWeb ingestion, Semantic Kernel + Ollama, /api/query |
| 4 | Complete | MapLibre frontend, H3 heatmap, SSE feed panel, cell click sidebar |
| 5 | Complete | FIRMS tagging, origin classification, HMS plumes, AirNow AQI |
| 6 | Complete | HMS smoke, county borders, OriginClassifier refactor to interface |
| 7 | Complete | OpenFreeMap basemap, terrain hillshade, zoom opacity, pulse/ring, badges, legend filter |
| 8 | Not started | Cloud migration: Azure App Service, Azure PostgreSQL, Azure AI Search, Claude API |
| 9 | Planned | Live feed enrichment: expanded NWS alerts, InciWeb incident tracking, NIFC/WFIGS, CDOT closures |

---

## Documentation Index

All specs live in `Documentation/`. Read the applicable doc before implementing a feature.

| File | Contents |
|---|---|
| `README.md` | Project overview, tech stack, phase task lists, known risks, architecture |
| `api-reference.md` | All endpoint contracts with full JSON request/response examples |
| `database-schema.md` | Full SQL table definitions, indexes, EF Core notes |
| `risk-model.md` | H3 grid strategy, scoring formula weights, normalization ranges, color scheme |
| `live-feed.md` | SSE backend contract, event type catalog, FeedService spec, frontend EventSource impl |
| `out-of-state-classification.md` | FIRMS/HMS classification logic, risk score impact rules, map representation |
| `data-sources.md` | All 11 data sources with ingestion approach, caching strategy, idempotency |
| `agents.md` | Agent roster, ownership, handoff order |
