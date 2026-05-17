# Colorado Wildfire RAG Analyzer
    Type a home address → map flies to that location and shows the risk score for that specific hex cell.

    Target Users
    - Homeowners in Colorado wildland-urban interface (WUI) zones wanting to know their current fire risk
    - Fire professionals / emergency managers tracking conditions across counties
    - Insurance analysts assessing portfolio risk across the state
    - Researchers querying historical incident data in natural language

    ---
    The Core Differentiator

    Most wildfire tools show you where fires are right now. This app combines historical burn patterns + live weather + terrain data + AI document retrieval into a single risk score per cell, updated hourly — and lets you ask questions about it in plain English.
> **This is a Spec-Driven Development (SDD) project.**
> All features, architecture decisions, API contracts, and data models are fully specified
> in this Documentation folder before any code is written. No implementation begins until
> the relevant spec document is complete and reviewed. Agents and contributors must read
> the applicable spec before writing a single line of code.

---

## Documentation Index

| Document | Description |
|---|---|
| **README.md** (this file) | Project overview, status, goals, architecture, phases, tech stack |
| [agents.md](agents.md) | Agent registry — who is involved, what they own, current status |
| [api-reference.md](api-reference.md) | All API endpoint contracts with full JSON examples |
| [database-schema.md](database-schema.md) | PostgreSQL/PostGIS table definitions |
| [data-sources.md](data-sources.md) | Data source registry and ingestion details |
| [risk-model.md](risk-model.md) | H3 grid strategy and risk scoring formula |
| [live-feed.md](live-feed.md) | SSE live feed spec and frontend panel design |
| [out-of-state-classification.md](out-of-state-classification.md) | Out-of-state fire/smoke classification logic |

---

## Project Status

| Phase | Status | Description |
|---|---|---|
| 0 | **Complete** | Research & architecture |
| 1 | **Complete** | Data ingestion + fire history grid |
| 2 | **Complete** | Real-time risk scoring |
| 3 | **Complete** | RAG query engine |
| 4 | **Complete** | Heatmap frontend |
| 5 | **Complete** | Live fire layer, alerts, origin classification |
| 6 | **Complete** | HMS smoke, county borders, origin classifier refactor |
| 7 | **Complete** | Map detail, terrain, risk highlighting, UX polish |
| 8 | Not Started | Cloud migration (Azure + Claude) |
| 9 | **Complete** | Live feed enrichment (expanded NWS alerts, InciWeb incident tracking, NIFC/WFIGS, CDOT closures) |
| 10a | **Complete** | High-risk region intelligence — quick wins (news RSS, county OES alerts, NWS spot forecasts, RAWS alerts, critical UX) |
| 10b | Not Started | Satellite upgrade (GOES-East fire detection + lightning) |
| 10c | Not Started | Social layer (Reddit fire reports) |
| 10d | Not Started | Paid sources (X API, Broadcastify scanner) |

---

## User Personas

All feature decisions, API design, and UI prioritization must reference these personas.

### Homeowner / Resident
- **Who:** Lives in or near a fire-prone Colorado community (WUI, foothills, mountain towns)
- **Primary goals:** Understand risk to their specific address; know when to evacuate; protect family and property
- **Key questions:** *"Is my neighborhood at risk right now?"* · *"Should I be worried about that smoke?"* · *"What do I do if a fire starts nearby?"*
- **Key features:** Address search, plain-language risk summary, evacuation zone layer, air quality indicators, proactive alerts

### Fire Professional
- **Who:** Incident commanders, emergency managers, land managers, fire behavior analysts
- **Primary goals:** Situational awareness across a region; historical fire behavior context; grounded natural language queries against incident data
- **Key questions:** *"What conditions preceded the last major fire in this terrain?"* · *"Which H3 cells crossed into Very High risk in the last 6 hours?"* · *"Where is the smoke coming from?"*
- **Key features:** RAWS-quality weather data, fire history context, RAG query on incident reports, out-of-state classification, data export

### Analyst / Enterprise
- **Who:** Insurance underwriters, utility risk managers, county planners, researchers
- **Primary goals:** Portfolio or corridor-level risk analysis; API access for downstream systems; historical trend data
- **Key questions:** *"How has risk changed across Jefferson County parcels over 5 years?"* · *"Which transmission line segments are in H3 cells above score 7 today?"*
- **Key features:** API key access, batch endpoints, GeoJSON/CSV export, rate-limited tiers, parcel-level scoring (future)

---

## Competitive Differentiation

| Feature | This App | InciWeb | AirNow | NIFC Active Fire Map | Colorado CFIRS |
|---|---|---|---|---|---|
| Historical + real-time risk in one view | ✅ | ❌ | ❌ | ❌ | ❌ |
| Natural language query (RAG) | ✅ | ❌ | ❌ | ❌ | ❌ |
| Out-of-state smoke/fire classification | ✅ | ❌ | ❌ | ❌ | ❌ |
| H3 hex grid spatial consistency | ✅ | ❌ | ❌ | ❌ | ❌ |
| Address search entry point | ✅ (Phase 4) | ❌ | ✅ | ❌ | ❌ |
| Live SSE event feed | ✅ | ❌ | ❌ | ❌ | ❌ |
| Open data sources / reproducible | ✅ | ✅ | ✅ | ✅ | ❌ |
| API access for enterprise | ✅ (Phase 6) | ❌ | limited | ❌ | ❌ |

---

## Monetization Architecture

### Tiers

**Free (public):** Map access, H3-6 risk grid, fire history, smoke plumes, basic RAG (10 queries/day), SSE live feed, address search.

**Professional ($49–99/month):** H3-8 detail, RAWS station overlays, API key access (1,000 calls/day), email/SMS alert delivery for watched locations, PDF risk reports, 90-day data retention.

**Enterprise ($500–5,000/month or custom):** Parcel-level risk scoring API, batch portfolio endpoints, SLA-backed uptime, custom alert configurations, GeoJSON/CSV export, white-label option, dedicated support.

### Revenue Opportunities
- **Insurance:** Per-parcel risk score API — insurers/reinsurers pay $10K–$500K/year for CO portfolio scoring
- **Utilities:** Transmission corridor risk reports — Xcel Energy and others are mandated by Colorado PUC to assess line-adjacent fire risk
- **Municipalities:** County subscriptions — 10 CO counties at $20K/year = $200K ARR
- **Grants:** FEMA BRIC, USDA Forest Service resilience programs, Colorado Wildfire Resilience Fund — strong candidate given open-data strategy and out-of-state classification capability

### Phase 6 Billing Infrastructure
- API key issuance and validation middleware
- Usage tracking per key (PostgreSQL `api_usage_log` table)
- Stripe integration for subscription billing
- Rate limiting tied to tier (free: 10 RAG/day; pro: 1,000 API calls/day; enterprise: custom)

---

## Project Goals

- Ingest historical Colorado wildfire data (MTBS, NIFC, USFS)
- Compute per-cell fire history metrics using an H3 hexagonal grid
- Score real-time wildfire risk per cell using weather, fuel moisture, and terrain data
- Embed and index unstructured incident reports and research for RAG queries
- Serve an interactive heatmap (risk + fire history) via a MapLibre GL frontend
- Enable natural language queries grounded in retrieved documents and live data
- **Stream a live feed panel** showing incoming reports, data events, and RAG activity in real time
- **Distinguish out-of-state fire and smoke events** that affect Colorado air quality without inflating in-state risk scores
- Designed to migrate to Azure + Claude API for production

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                        DATA SOURCES                          │
│  MTBS · NIFC · NASA FIRMS · NOAA · LANDFIRE · InciWeb · USFS│
│  NOAA HMS · EPA AirNow · Census TIGER/Line                   │
└──────────────────────┬──────────────────────────────────────┘
                       │
               [C# Ingestion Service]
               ├── Structured  → PostgreSQL + PostGIS
               ├── Raster      → GDAL → H3 Grid cells (Phase 5)
               └── Unstructured→ Chunked + Embedded → Qdrant

               [ASP.NET Core 8 Web API — Controller-based]
               ├── Risk scoring engine (per H3 cell, hourly refresh)
               ├── GeoJSON endpoint — pre-serialized H3 polygons
               ├── RAG query endpoint (Semantic Kernel + Ollama)
               ├── Historical fire history endpoint
               ├── SSE /api/feed → live event stream to frontend
               └── Out-of-state fire/smoke classification + tagging

               [MapLibre GL JS Frontend — Vanilla JS + Vite]
               ├── Fill layer         (H3 polygon risk heatmap)
               ├── Fire history layer  (burn perimeters, toggleable)
               ├── In-state fire layer (NASA FIRMS red/orange points)
               ├── Out-of-state layer  (purple points, distinct tooltip)
               ├── Smoke plume layer   (HMS polygons, grey-brown fill)
               ├── Click → RAG sidebar (natural language assessment)
               └── Live Feed panel    (SSE stream, right of map)
```

---

## Tech Stack

### Backend (C#)

| Component | Technology | Notes |
|---|---|---|
| Framework | ASP.NET Core 8 — **Controller-based** | Preferred for auth middleware in Phase 6 |
| ORM / Spatial DB | PostgreSQL 16 + PostGIS 3.4 | Geospatial queries, fire history |
| Vector Store | Qdrant v1.9.x (Docker) | Embedding storage and similarity search |
| RAG Engine | Microsoft Semantic Kernel 1.21.x | Orchestration, prompt templating, RAG |
| Local LLM | Ollama — **`llama3.2` (8b)** | Best RAG grounding quality at local scale |
| Embeddings | Ollama — **`nomic-embed-text`** | 768-dimensional, optimized for retrieval |
| Geospatial | NetTopologySuite 2.5.x + H3 4.1.x | Grid indexing, polygon serialization |
| Shapefile I/O | NetTopologySuite.IO.ShapeFile 2.1.x | Read MTBS/NIFC Shapefiles |
| Coord Reproject | ProjNet 2.0.x | NAD83 (EPSG:4269) → WGS84 (EPSG:4326) |
| HTTP Resilience | Polly 8.4.x | Retry/circuit-breaker for NOAA + FIRMS |
| Scheduling | PeriodicTimer (IHostedService) | Hourly risk refresh; Quartz for Phase 6 |
| CSV Parsing | CsvHelper 33.0.x | NIFC CSV, NASA FIRMS CSV |
| HTML Scraping | AngleSharp 1.1.x | InciWeb incident report parsing (Phase 3) |
| Logging | Serilog.AspNetCore 8.0.x | Structured logging |

> **GDAL deferred to Phase 5.** Use NetTopologySuite.IO.ShapeFile exclusively for Phases 1–3.

### Frontend (JavaScript)

| Component | Technology | Notes |
|---|---|---|
| Build Tool | **Vite** (vanilla template) | HMR, ES modules, zero config |
| Map Engine | MapLibre GL JS 4.x | Open source, no proprietary key required |
| Basemap | OpenFreeMap dark vector style | Free, no API key; roads, labels, landmarks, national parks |
| H3 Client | h3-js 4.x | **v4 breaking change:** `cellToBoundary` returns `[lat,lng]` — reverse to `[lng,lat]` for GeoJSON |
| Charts | Chart.js 4.4.x + chartjs-adapter-date-fns | Time-series charts; adapter is required |
| Markdown | marked + dompurify | Safe rendering of LLM Markdown responses |

> **No React or component framework.** The map-centric app has no routing or state tree that justifies the overhead.

### Infrastructure (Local / Free Tier)

| Component | Technology | Port |
|---|---|---|
| PostgreSQL + PostGIS | Docker (`postgis/postgis:16-3.4`) | 5432 |
| Qdrant | Docker (`qdrant/qdrant:v1.9.2`) | 6333 (REST), 6334 (gRPC) |
| Ollama | Host install (for GPU access) | 11434 |
| ASP.NET Core API | Host (`dotnet run`) | 5000/5001 |
| Frontend Dev Server | Vite | **5173** (CORS must allow this) |

### Future — Phase 6 Cloud Migration

| Component | Technology |
|---|---|
| LLM | Claude API (Anthropic) via Semantic Kernel connector |
| Hosting | Azure App Service |
| Database | Azure Database for PostgreSQL |
| Vector Store | Azure AI Search (vector index) |
| Storage | Azure Blob Storage (raster files) |
| Auth | Azure Active Directory B2C |

---

## NuGet Packages (Phase 1–3)

```xml
<!-- Core -->
<PackageReference Include="Swashbuckle.AspNetCore" Version="6.6.*" />

<!-- Database / Spatial -->
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.*" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite" Version="8.0.*" />
<PackageReference Include="NetTopologySuite" Version="2.5.*" />
<PackageReference Include="NetTopologySuite.IO.GeoJSON4STJ" Version="4.0.*" />
<PackageReference Include="NetTopologySuite.IO.ShapeFile" Version="2.1.*" />
<PackageReference Include="ProjNet" Version="2.0.*" />

<!-- H3 Grid -->
<PackageReference Include="pocketken.H3" Version="4.0.*" />  <!-- NuGet ID is pocketken.H3, not H3 -->

<!-- Vector DB -->
<PackageReference Include="Qdrant.Client" Version="1.9.*" />

<!-- RAG / AI -->
<PackageReference Include="Microsoft.SemanticKernel" Version="1.21.*" />
<PackageReference Include="Microsoft.SemanticKernel.Connectors.Ollama" Version="1.21.*" />
<PackageReference Include="Microsoft.SemanticKernel.Plugins.Memory" Version="1.21.*" />

<!-- HTTP / Resilience -->
<PackageReference Include="Polly" Version="8.4.*" />
<PackageReference Include="Microsoft.Extensions.Http.Polly" Version="8.0.*" />

<!-- Data Parsing -->
<PackageReference Include="CsvHelper" Version="33.0.*" />
<PackageReference Include="AngleSharp" Version="1.1.*" />

<!-- Logging -->
<PackageReference Include="Serilog.AspNetCore" Version="8.0.*" />
<PackageReference Include="Serilog.Sinks.Console" Version="5.0.*" />
```

### Frontend npm Packages

```bash
npm create vite@latest frontend -- --template vanilla
cd frontend
npm install maplibre-gl h3-js chart.js chartjs-adapter-date-fns date-fns marked dompurify
```

---

## Project Structure

```
co-wildfire-rag/
├── Documentation/                         ← All specs (read first)
│   ├── README.md                          ← This file
│   ├── api-reference.md
│   ├── database-schema.md
│   ├── data-sources.md
│   ├── risk-model.md
│   ├── live-feed.md
│   └── out-of-state-classification.md
├── docker-compose.yml
├── backend/
│   ├── CoWildfireApi.sln
│   ├── CoWildfireApi/
│   │   ├── Controllers/
│   │   │   ├── RiskController.cs          ← GET /api/risk-grid
│   │   │   ├── FireHistoryController.cs   ← GET /api/fire-history
│   │   │   ├── QueryController.cs         ← POST /api/query
│   │   │   ├── ActiveFiresController.cs   ← GET /api/active-fires
│   │   │   ├── SmokePlumesController.cs   ← GET /api/smoke-plumes
│   │   │   ├── FeedController.cs          ← GET /api/feed (SSE)
│   │   │   ├── CountyBoundsController.cs  ← GET /api/county-bounds (GeoJSON)
│   │   │   └── HealthController.cs        ← GET /api/health
│   │   ├── Services/
│   │   │   ├── RiskScoringService.cs
│   │   │   ├── RagService.cs
│   │   │   ├── NoaaService.cs
│   │   │   ├── FirmsService.cs
│   │   │   ├── H3GridService.cs
│   │   │   ├── EmbeddingService.cs
│   │   │   ├── IOriginClassifierService.cs
│   │   │   ├── OriginClassifierService.cs
│   │   │   ├── HmsService.cs
│   │   │   ├── AirNowService.cs
│   │   │   └── FeedService.cs
│   │   ├── Ingestion/
│   │   │   ├── MtbsIngester.cs
│   │   │   ├── NifcIngester.cs
│   │   │   └── InciwebIngester.cs
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs
│   │   │   └── tiger/                     ← Census TIGER/Line shapefiles (gitignored)
│   │   ├── Models/
│   │   │   ├── H3Cell.cs
│   │   │   ├── FireEvent.cs
│   │   │   ├── ActiveFireDetection.cs
│   │   │   ├── LiveFeedEvent.cs
│   │   │   ├── OriginClassification.cs
│   │   │   └── RagResponse.cs
│   │   └── appsettings.Development.json
│   └── sql/
│       └── init/
│           ├── 001_extensions.sql
│           └── 002_schema.sql
└── frontend/
    ├── index.html
    ├── package.json
    ├── vite.config.js
    └── src/
        ├── map.js
        ├── api.js
        ├── config.js
        ├── sidebar.js
        ├── feed.js
        ├── info.js
        ├── layers/
        │   ├── riskGrid.js
        │   ├── countyBorders.js
        │   ├── activeFires.js
        │   ├── outOfStateFires.js
        │   └── smokePlumes.js
        └── styles/
            └── main.css
```

---

## Development Phases

### Phase 1 — Data Foundation
- [ ] Set up Docker Compose (PostgreSQL/PostGIS + Qdrant)
- [ ] Write SQL init scripts (extensions + full schema)
- [ ] Scaffold ASP.NET Core 8 project (controller-based)
- [ ] Add all NuGet packages
- [ ] Download MTBS Shapefile from https://www.mtbs.gov/direct-download
- [ ] Build `MtbsIngester.cs` (Shapefile → PostGIS, reproject NAD83→WGS84 via ProjNet)
- [ ] Generate H3 Resolution 6 + 8 grid for Colorado (~220 + ~3,000 cells)
- [ ] Compute fire-to-cell intersections → aggregate metrics on `h3_cells`
- [ ] Implement `GET /api/fire-history` + `GET /api/risk-grid` (risk score null for now)
- [ ] Implement `GET /api/health`

### Phase 2 — Risk Scoring
- [ ] Build `NoaaService.cs` with Polly retry; cache per H3-6 cell for 1 hour
- [ ] Obtain NASA FIRMS API key (free; firms.modaps.eosdis.nasa.gov)
- [ ] Obtain MesoWest/Synoptic API token (free tier; synopticdata.com)
- [ ] Build `RawsService.cs` — query MesoWest stations within 50km of each H3-6 cell center; cache 1 hour
- [ ] Add `raws_station_id`, `raws_distance_km`, `raws_wind_speed_mph`, `raws_relative_humidity_pct` columns to `h3_cells`
- [ ] Implement risk scoring formula (including fire history component); persist score to `h3_cells`
- [ ] Use RAWS observed weather as primary input; fall back to NOAA gridded for cells with no nearby station
- [ ] Schedule hourly refresh via `BackgroundService` + `PeriodicTimer`
- [ ] `/api/risk-grid` returns live risk scores

### Phase 3 — RAG Engine
- [ ] Pull Ollama models: `ollama pull llama3.2 && ollama pull nomic-embed-text`
- [ ] Configure Semantic Kernel with Ollama connectors
- [ ] Create Qdrant collection `wildfire_docs` (768-dim, cosine, indexed payload fields: `state`, `year`, `source_type`, `county`)
- [ ] Build `InciwebIngester.cs` — RSS → HTML → chunk → embed → Qdrant (with full payload schema)
- [ ] Build additional ingesters: `MtbsReportIngester.cs`, `CsfsReportIngester.cs`, `NwcgLessonsIngester.cs`, `DfpcSummaryIngester.cs`
- [ ] Build `RagService.cs` — embed → geographic pre-filter → search → weather → prompt → llama3.2
- [ ] Implement `POST /api/query` with `actionableGuidance` in response

### Phase 4 — Frontend Heatmap + Live Feed
- [ ] Verify Node.js 18+ LTS; obtain MapTiler API key
- [ ] Scaffold Vite project, install npm packages
- [ ] MapLibre GL map centered on Colorado with MapTiler `outdoor-v2` basemap
- [ ] Add address search bar using MapTiler Geocoding API (free tier); on select → fly to location + auto-open RAG sidebar
- [ ] Implement `GET /api/cell-at-point` lookup on address select (see api-reference.md)
- [ ] Create static mock GeoJSON for offline development
- [ ] Implement risk fill layer, fire history layer, in-state/out-of-state fire layers
- [ ] Implement smoke plume layer (`smokePlumes.js`)
- [ ] Implement cell click → sidebar with cell stats + Chart.js chart + RAG response + actionable guidance section
- [ ] Implement live feed panel (`feed.js`) — SSE `EventSource`, card rendering; default filter = Alerts + Risk Score + Out-of-State only
- [ ] Feed card click → fly map to location + open sidebar
- [ ] Wire all layers to live backend endpoints

### Phase 5 — Live Data, Alerts & Origin Classification
- [ ] Seed `state_boundaries` from Census TIGER/Line polygons
- [ ] Seed `co_counties` from Census TIGER/Line county polygons (see database-schema.md)
- [ ] Build `OriginClassifierService.cs` — `ST_Within` check per FIRMS point
- [ ] Build `HmsService.cs` — NOAA HMS smoke plumes + CO intersection + county lookup via `co_counties`
- [ ] Build `AirNowService.cs` — EPA AirNow AQI per H3-6 cell hourly
- [ ] Extend schema: `is_colorado`, `origin_state`, `impact_type` on `active_fire_detections`
- [ ] Add `smoke_events`, `aqi_observations`, and `co_counties` tables
- [ ] Implement `GET /api/smoke-plumes` and `GET /api/feed` (SSE)
- [ ] Wire `FeedService.PublishAsync()` into all services
- [ ] Ensure out-of-state events are excluded from `current_risk_score`
- [ ] NOAA Red Flag Warning → push to feed as `alert` events
- [ ] LANDFIRE vegetation/slope raster integration (GDAL.NET)
- [ ] USFS ADS bark beetle data integration — populate `beetle_kill_severity` on `h3_cells`

### Phase 7 — Map Detail, Terrain & Risk UX

Goal: make the map immediately informative and visually compelling. Users should be able to tell at a glance which regions are high or extreme risk, see topographic context, and read city/road names as they zoom in — without losing any existing functionality.

#### 7a — Terminology: "Cell" → "Region" ✅ Complete
- Renamed all user-facing text, HTML element IDs, CSS selectors, and internal JS function names
- Backend API field names unchanged (`h3Index`, `/api/risk-grid`) to preserve API compatibility
- Files affected: `index.html`, `sidebar.js`, `map.js`, `riskGrid.js`, `main.js`, `info.js`, `api.js`, `main.css`

#### 7b — Terrain Hillshade ✅ Complete
- AWS Terrain Tiles as a `raster-dem` source (free, no API key, terrarium encoding)
- `hillshade` layer below the H3 hex fill; uses dark-tuned shadow/highlight colors to complement the dark basemap
- Hillshade inserted `beforeId` of the first vector symbol layer so city/road labels always render above it

#### 7c — Vector Basemap (OpenFreeMap) ✅ Complete
- Replaced CartoDB Dark Matter raster tiles with OpenFreeMap dark vector style (free, no API key)
- Provides: richer road network, city/town labels, named landmarks, national parks, rivers — all zoom-responsive
- All data layers (hex fills, county borders) inserted before the first symbol layer so city/road labels always appear on top

#### 7d — Zoom-Responsive Hex Opacity ✅ Complete
- Zoom 5–7 (state overview): 0.80 opacity; zoom 8–10 (county): 0.65; zoom 11+ (city/street): 0.45
- MapLibre `interpolate` expression on `fill-opacity` — smooth transition at all zoom levels

#### 7e — High/Extreme Risk Visual Highlighting ✅ Complete
- **Animated pulse layer**: separate `fill` filtered to Extreme only; opacity 0.10→0.55 cycle via `setInterval` + `setPaintProperty`
- **Bold border ring**: `line` layer filtered to High/Very High/Extreme with colored strokes (red, crimson, amber)
- **Opacity boost**: selected region bumps to 0.95; non-selected use zoom-interpolated BASE_OPACITY

#### 7f — Risk Count Badge ✅ Complete
- Overlay chips (top-left): Extreme count + High+ count from loaded GeoJSON
- Clicking either chip flies the map to the highest-scoring region

#### 7g — City Label Styling ✅ Complete
- After map load, `styleCityLabels()` applies county-label-matching paint to OpenFreeMap place layers
  (`place_city_large`, `place_city`, `place_town`, `place_village`, `place_suburb`, `place_other`):
  `text-color: #c0c4d0`, `text-halo-color: #000000`, `text-halo-width: 1.2` — font size unchanged

#### 7h — Legend Click-to-Filter ✅ Complete
- Clicking a risk level row in the legend highlights only matching regions (opacity 0.95) and dims all others (0.08)
- Pulse and high-ring layers are adjusted to match the active filter
- Click the same row again to deselect and restore normal view
- "Not scored" row is non-interactive

### Phase 9 — Live Feed Enrichment (Planned)

Goal: expand the live feed beyond satellite/weather data to include named incident tracking, fire-related road closures, and expanded NWS fire weather alerts — all using free, no-API-key government endpoints.

#### 9a — Expanded NWS Fire Weather Alerts
- Current feed catches Red Flag Warnings via `event=Red Flag Warning` filter
- Expand to include: **Fire Weather Watch**, **Fire Warning**, **Extreme Fire Danger**
- Same endpoint: `https://api.weather.gov/alerts/active?area=CO`; just widen the event filter list
- Publish to SSE feed as `fire-weather-watch` and `fire-warning` event types
- Frontend: new icons/colors for each type in `feed.js`

#### 9b — InciWeb Colorado Incident RSS Feed
- Poll `https://inciweb.wildfire.gov/feeds/rss/incidents/state/colorado/` every 10 minutes
- Emit feed events when new incidents appear or existing ones update (acreage, containment change)
- Different from the existing `InciwebIngester` (which ingests documents for RAG) — this is for named fire tracking in the feed
- Backend: new `InciwebFeedPoller` background service; frontend: `📋 Incident Update` card type

#### 9c — NIFC/WFIGS Active Incidents
- Poll IRWIN-sourced ArcGIS REST API for current CO wildland fire incidents:
  `https://services3.arcgis.com/T4QMspbfLg3qTGWY/arcgis/rest/services/Current_WildlandFire_Locations/FeatureServer/0/query?where=POOState='US-CO'&outFields=*&f=json`
- Track new incidents and acreage/containment changes; emit to feed
- Provides: fire name, discovered date, acres, containment %, coordinates (can fly-to on click)
- Backend: new `NifcIncidentPoller`; frontend: `🔥 Active Incident` card with fly-to support

#### 9d — CDOT Fire-Related Road Closures
- Poll CDOT statewide RSS: `https://www.codot.gov/news/feeds/statewide`
- Filter items whose title or description mentions fire/smoke/evacuation
- Emit as `road-closure` feed events; frontend: `🚧 Road Closure` card type
- Useful for residents needing evacuation route awareness

### Phase 10 — High-Risk Region Intelligence (Planned)

Goal: when any H3 cell crosses score >= 6.0 (High), it enters an active monitoring state. Every data source — plus several new ones — begins filtering specifically for that cell's geographic footprint. The frontend surfaces these not as generic feed cards but as spatially grounded, urgent alerts tied directly to the map. Free sources are prioritized first.

#### Trigger Condition
A cell is "actively monitored" when `current_risk_score >= 6.0`. All new event types below tag `h3Index` and `county` so the frontend can link alerts directly to map cells.

---

#### 10a — Quick Wins (Free, ~2–3 days)

**Backend — new pollers (all follow the `InciwebFeedPoller` / `CdotRssPoller` pattern):**

**`NewsRssPoller`** — configurable list of fire-focused RSS feeds; emits `news-article` events
- Colorado Sun wildfire feed: `https://coloradosun.com/category/wildfire/feed/`
- Denver Post wildfire feed: `https://www.denverpost.com/wildfire/feed/`
- Colorado State Forest Service: `https://csfs.colostate.edu/feed/`
- USFS forest unit feeds (Arapaho-Roosevelt, Pike-San Isabel, White River, GMUG) — check per-forest RSS URLs
- Keyword filter: fire/wildfire/evacuation/closure; dedup by GUID; emit `severity = "info"` unless headline contains evacuation/structure-threatened → `"warning"`

**`ColoradoCountyOesPoller`** — county Office of Emergency Services RSS feeds; emits `evacuation-alert` (severity: critical)
- Jefferson County: `https://www.jeffco.us/rss.aspx?feed=news`
- Larimer County: `https://www.larimer.gov/rss/news`
- El Paso County: `https://www.elpasoco.com/news/feed/`
- Arapahoe County: `https://www.arapahoegov.com/rss.aspx?RSID=26`
- Filter for evacuation/fire keywords; escalate to `severity = "critical"`

**`NwsSpotForecastPoller`** — polls each CO Weather Forecast Office for Fire Weather Statements (FWS); emits `spot-forecast`
- Endpoint: `GET https://api.weather.gov/products?type=FWS&location={wfo}` for BOU, PUB, GJT, CYS
- A spot forecast being issued is near-definitive confirmation that an active IMT is managing a fire
- Parse product text for coordinates ("LOCATION...XX.XX N XXX.XX W"), reverse-geocode to H3 cell using `H3GridService`
- Dedup by product ID; emit with fire name and WFO as `source`

**`Colorado511Poller`** — CoTrip structured road incident API; emits `road-closure`
- API: `https://cotrip.org/api/v2/incidents?format=json` (free, registration required)
- Returns GeoJSON with incident type and lat/lon — far better than CDOT RSS because it includes coordinates
- H3-index each incident point to associate it with a specific cell
- Filter by incident types: fire, smoke, evacuation

**`RawsAlertPoller`** — extend existing `RawsService`; emits `raws-alert`
- Scope to only the RAWS stations nearest to cells scoring >= 6.0
- Compare each reading against the previous observation (stored in `_known` dict, same pattern as other pollers)
- If wind speed jumped > 15 mph OR relative humidity dropped > 10 percentage points: emit `severity = "warning"`
- If both thresholds crossed simultaneously: emit `severity = "critical"`
- Latency: 2–5 minutes from physical measurement to feed event

**Backend — extend existing services:**
- `RiskScoringService` — on score update, publish `risk_score` event whenever a cell crosses a category boundary (6.0, 8.0, 9.0) with `h3Index` tagged; currently this event is not emitted
- `FeedPollingBackgroundService` — inject and register all new pollers above

**New endpoint:**
- `GET /api/feed/recent?h3Index={idx}&limit=10` — returns the last N feed events tagged to a specific H3 cell; powers the new sidebar section

**New event types to register in `feed.js`:**
`news-article`, `evacuation-alert`, `spot-forecast`, `road-closure` (upgraded from CDOT RSS), `raws-alert`

**Frontend — 6 UX changes (all in this sprint):**

1. **Pinned alert banner** — `<div id="alert-banner">` above `#feed-cards`. When a `critical` event arrives with `h3Index` set, render it into the banner (not the card list) with a "Fly to region" button calling `map.flyTo()` to the cell center. Auto-dismisses after 60 seconds. Multiple alerts stack vertically.

2. **Map pulse on critical H3 cell** — when a critical event with `h3Index` arrives, add a pulsing circle layer at that cell's center in MapLibre GL. Implemented as a `circle` paint layer with `setInterval` opacity cycling (same pattern as the existing Extreme pulse in `riskGrid.js`). Auto-removes after 5 minutes.

3. **H3-linked sidebar section** — when the user clicks a cell scoring >= 6, add a "Recent alerts for this region" section above the RAG input. Fetches from `GET /api/feed/recent?h3Index={idx}&limit=5`. Shows the last 5 events for that specific cell with timestamp and severity.

4. **Browser notifications** (opt-in) — `new Notification("Active Fire Alert", { body: event.detail })` on the first `critical` event per session. Request permission on page load; store grant in `localStorage`. Only fires once per event (dedup by event ID).

5. **Sound alert** (opt-in) — a single short audio tone on `critical` events. Gated behind a speaker-icon toggle in the feed header; default off; stored in `localStorage`.

6. **Severity filter pills** — replace current filter buttons with "All | Warning | Critical" pills above `#feed-cards`. "Critical" view hides `data-severity="info"` cards. Lets users silence routine data-fetch noise during an active event.

7. **Colloquial region naming** — replace raw H3 cell indices with human-readable location names everywhere a cell is referenced in the UI.
   - H3 indices (e.g., `8648db2cfffffff`) are opaque to users; replace with names like "Jefferson County – Evergreen Area" or "Larimer County – Fort Collins North"
   - **Backend:** Add a nullable `display_name` column to `h3_cells`; populate once at grid-generation time using a two-step lookup: (1) county name from `co_counties` boundary join, (2) nearest named place (city/town/neighborhood) from a simple Nominatim reverse-geocode of the cell center, or from a pre-seeded place table derived from OpenFreeMap data
   - **API:** Surface `displayName` in all responses that currently include `h3Index` — risk-grid GeoJSON feature properties, `/api/feed/recent` payloads, SSE event payloads for `risk_score`, `raws-alert`, `evacuation-alert`, etc.
   - **Frontend:** Substitute `displayName` wherever `h3Index` is shown to users: sidebar header ("Region: Jefferson County – Evergreen Area"), live feed cards, alert banner, RAG query pre-fill context
   - **Fallback:** If no named place is found within 25 km of cell center, use `"[County Name] – Unincorporated Area"`; if county lookup also fails, fall back to the raw H3 index
   - **Data source:** `co_counties` table already populated from TIGER/Line seed; place names can be seeded from OpenFreeMap Nominatim (`https://nominatim.openstreetmap.org/reverse?lat=...&lon=...&format=json`) at grid-generation time — one call per cell, run once, cached forever in DB

---

#### 10b — Satellite Upgrade (Free, GOES-East S3, ~3–5 days)

**`GoesFireDetectionService`** — GOES-East ABI FDC (Fire/Hot Spot Characterization); emits `goes-fire-detection`
- Source: public AWS S3 bucket `s3://noaa-goes16/ABI-L2-FDCC/` — no API key, no cost
- Cadence: every 5 minutes (vs. FIRMS VIIRS at ~15-minute orbital repeat for Colorado)
- List the latest file in the S3 prefix via HTTP GET (`https://noaa-goes16.s3.amazonaws.com/?list-type=2&prefix=ABI-L2-FDCC/{year}/{day}/`)
- Download the latest `.nc` file (~2–5 MB); parse with `NetCDF.NET` NuGet package
- Variables: `Mask` (fire classification: 10 = processed fire, 30 = saturated), `Power` (FRP in MW), `Area` (km²), plus geostationary projection variables
- Convert pixel indices to lat/lon using GOES-R geostationary projection formula (GOES-R PUG Vol 5)
- Filter to Colorado bounding box; H3-index each fire pixel; emit with FRP-based severity thresholds
- Tag `h3Index` so frontend can pulse the cell on the map

**`GoesLightningService`** — GOES-East GLM (Geostationary Lightning Mapper); emits `lightning-strike`
- Source: same S3 infrastructure — `s3://noaa-goes16/GLM-L2-LCFA/` — free
- Cadence: 60-second file cadence; poll every 60 seconds
- Parse flash group lat/lon from NetCDF; filter to Colorado bbox; H3-index each flash
- Trigger logic: if >= 3 flashes hit the same cell within a 10-minute window AND `current_risk_score >= 6.0` → emit `severity = "warning"` lightning alert
- Escalation: if a FIRMS or GOES FDC fire detection appears in the same cell within 30 minutes of a lightning cluster → escalate to `severity = "critical"` with cross-reference to the lightning event

> **Note:** Both GOES services share the same NetCDF parsing and geostationary projection infrastructure. Build them together. The projection math is fixed for GOES-East perspective and can be hardcoded as a utility class.

---

#### 10c — Social Layer (Free, ~1–2 days)

**`RedditFirePoller`** — Reddit JSON API (no auth required); emits `social-report`
- Endpoints:
  - `https://www.reddit.com/r/ColoradoWildfire/new.json?limit=25` — dedicated fire subreddit
  - `https://www.reddit.com/search.json?q=wildfire+colorado&sort=new&t=hour` — cross-subreddit
- Headers: `User-Agent: CoWildfireApp/1.0` (required by Reddit)
- Rate limit: 1 req/2s for unauthenticated; poll every 3 minutes; well within limits
- Filter: posts created within last 30 minutes; title/selftext matches fire keywords; score >= 2 (reduces noise)
- Gate: only fetch when at least one H3 cell scores >= 6.0 (avoids wasted calls during quiet periods)
- Attempt location extraction from post text (city/highway/landmark mentions) to populate `county`
- Dedup by post ID; max 500 entries; emit `severity = "info"` (crowdsourced, unverified)

---

#### 10d — Paid Sources (Deferred — evaluate after 10a/10b/10c are stable)

| Source | What it provides | Cost | Notes |
|---|---|---|---|
| **X API filtered stream** | Real-time tweets from @COEmergency, @CSFS, county sheriffs; eyewitness reports | $100/mo | Persistent HTTP chunked stream; official accounts elevated to `severity = "critical"` |
| **Broadcastify + Whisper transcription** | Public-safety scanner audio → transcribed dispatch calls naming exact intersections | $30/yr (Broadcastify) + Whisper API | Highest information value; buffer MP3 stream in 30s chunks, POST to Whisper, keyword-filter transcript, emit `scanner-report` events |

---

#### 10 — Summary: New Event Types

| Type | Source | Severity | Notes |
|---|---|---|---|
| `news-article` | NewsRssPoller | info / warning | Breaking news; warning if evacuation keyword in headline |
| `evacuation-alert` | ColoradoCountyOesPoller | critical | County OES RSS; highest-priority event type |
| `spot-forecast` | NwsSpotForecastPoller | warning | Issued only when IMT is managing an active fire |
| `raws-alert` | RawsAlertPoller | warning / critical | Sudden wind spike or RH drop on a high-risk cell |
| `goes-fire-detection` | GoesFireDetectionService | warning / critical | 5-min satellite refresh; FRP-based severity |
| `lightning-strike` | GoesLightningService | warning / critical | Flash cluster on high-risk cell; escalates on co-detection |
| `social-report` | RedditFirePoller | info | Crowdsourced; unverified; county-tagged when location parseable |
| `scanner-report` | Broadcastify (Phase 10d) | warning / critical | Transcribed dispatch audio; highest latency value |

---

### Phase 8 — Cloud Migration
- [ ] Swap Ollama for Claude API via Semantic Kernel connector
- [ ] Migrate to Azure Database for PostgreSQL
- [ ] Migrate Qdrant to Azure AI Search vector index
- [ ] Deploy API to Azure App Service
- [ ] Deploy frontend to Azure Static Web Apps
- [ ] Add Azure AD B2C authentication

---

## Starting Dev Services

All three services must be running for the app to function locally. Use the provided script from the project root:

```powershell
.\start-dev.ps1
```

This script does the following in order:
1. `docker compose up -d` — starts PostgreSQL/PostGIS and Qdrant containers
2. Kills any existing `CoWildfireApi` process (so the exe is not locked)
3. `dotnet build` — rebuilds the backend; aborts if build fails
4. `dotnet run --no-build` — starts the backend API in a new window (port 5000/5001)
5. `npm run dev` — starts the Vite frontend dev server in a new window (port 5173)

To start services individually:

```powershell
# Docker only
docker compose up -d

# Backend only (kill first if already running)
Get-Process -Name "CoWildfireApi" -ErrorAction SilentlyContinue | Stop-Process -Force
cd backend
dotnet build CoWildfireApi.sln
dotnet run --project CoWildfireApi\CoWildfireApi.csproj --no-build

# Frontend only
cd frontend
npm run dev
```

---

## Pre-Development Checklist

```bash
dotnet --version          # Must be 8.0.x
docker compose version    # Must be v2.20+
docker --version          # Must be 24.x+
ollama list               # Need llama3.2 + nomic-embed-text
node --version            # Must be 18 LTS or 20 LTS
npm --version             # Must be 9+
```

External registrations needed:
- NASA FIRMS API key — firms.modaps.eosdis.nasa.gov (free, ~24h delivery)
- MapTiler API key — maptiler.com/cloud (free, whitelist localhost)
- EPA AirNow API key — docs.airnowapi.org (free)

Data downloads needed:
- MTBS Shapefile (~150 MB) — mtbs.gov/direct-download
- Census TIGER/Line state boundaries — census.gov
- Census TIGER/Line county boundaries (Colorado) — census.gov
- MesoWest API token — synopticdata.com (free tier)

---

## appsettings.Development.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=co_wildfire;Username=wildfire;Password=wildfire_dev"
  },
  "Qdrant": {
    "Host": "localhost",
    "Port": 6334,
    "CollectionName": "wildfire_docs"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "ChatModel": "llama3.2",
    "EmbeddingModel": "nomic-embed-text"
  },
  "Cors": {
    "AllowedOrigins": ["http://localhost:5173", "http://localhost:3000"]
  }
}
```

---

## Cost Summary

| Tier | Monthly Cost |
|---|---|
| Local development (current) | $0 |
| Phase 10a/10b/10c (all free sources) | $0 — GOES S3, Reddit, RSS, NWS, CoTrip all free |
| Phase 10d — X API filtered stream | +$100/mo |
| Phase 10d — Broadcastify + Whisper | +~$5–15/mo (Whisper API usage-based) |
| VPS hosted + local LLM | ~$12–20 |
| Azure hosted + Claude API | ~$30–50 |

---

## Known Risks & Gotchas

| Risk | Mitigation |
|---|---|
| MTBS uses NAD83 (EPSG:4269) — ~50m offset if not reprojected | Use `ProjNet` to reproject to WGS84 before PostGIS insert |
| H3 Resolution 8 full GeoJSON = ~17.5 MB | Always use `?bounds=` viewport filter for res-8 |
| H3 NuGet package name incorrect in spec | Use `pocketken.H3` v4.x (not `H3` v4.1 — that version doesn't exist). API is NTS-native: `Polyfill.Fill(Geometry, res)`, `cell.GetCellBoundary(geoFactory)` returns NTS Polygon in GeoJSON order |
| H3.net vs h3-js coordinate order: `[lat,lng]` not `[lng,lat]` | pocketken.H3 v4 `GetCellBoundary(geoFactory)` returns NTS Polygon already in `[lng,lat]` GeoJSON order; frontend h3-js v4: reverse `cellToBoundary()` output |
| Semantic Kernel Ollama connector was in preview as of Aug 2025 | Pin version; verify `AddOllamaChatCompletion` signature against pinned version |
| NOAA Weather.gov requires `User-Agent` header | Set `User-Agent: CoWildfireAnalyzer/1.0 (contact@email.com)` |
| MapTiler free tier: 100k requests/month | Use `outdoor-v2` style; avoid excessive pan/zoom in dev |
| Qdrant payload is untyped | Deserialize payload dictionary carefully with `JsonSerializer` |
| FIRMS points near CO border may be misclassified with low-res boundary | Use Census TIGER/Line 1:500k or higher |
| HMS smoke plume polygons are coarse (10–50 km) | Display + AQI inference only; not sole basis for conclusions |
| AirNow free tier: ~500 req/hour | Cache per H3-6 cell for 1 hour; do not query per H3-8 cell |
| SSE holds one HTTP connection per browser tab | Server-side idle timeout (5 min); frontend `EventSource` reconnects automatically |
| Out-of-state smoke can still lower visibility near fires | Document in tooltips: risk score = ignition risk, not air quality |
| GOES NetCDF files use geostationary projection (not lat/lon) | Must apply GOES-R PUG Vol 5 projection formula to convert pixel indices to WGS84; hardcode GOES-East satellite longitude (-75.0°) |
| GOES S3 file listing returns all files for the day | Always sort by filename (timestamp is embedded) and take only the most recent; do not download historical files on each poll |
| Reddit unauthenticated rate limit: 1 req/2s | Never poll faster than every 3 minutes; add `User-Agent: CoWildfireApp/1.0` or Reddit will block requests |
| County OES RSS feeds are inconsistently maintained | Wrap each feed in try/catch; a failed feed should not block the others; log the failure to `ingestion_log` |
| NWS FWS product text format is unstructured | Parse coordinates with a regex; if parsing fails, emit the event without `h3Index` rather than dropping it entirely |
| CoTrip 511 API requires free registration | Register at `https://data.cotrip.org/` for an API key; store in `appsettings.json` under `Colorado511:ApiKey` |
| Broadcastify MP3 streams require Premium account for stream key access | Do not attempt to scrape stream keys without a paid account; use RadioReference API as alternative source |
| RAWS alert thresholds will fire frequently during active weather events | Add a per-cell cooldown (e.g., 30 minutes between alerts for the same cell) to prevent alert fatigue |
| Browser Notification API requires HTTPS in production | Works on localhost; ensure HTTPS is configured before Phase 8 cloud deployment |

---

## Agent Notes

> **Ember (Frontend Agent):** Own Phase 4. See [api-reference.md](api-reference.md) for
> JSON shapes and [live-feed.md](live-feed.md) for feed panel spec. Risk fill layer uses
> `fill` type (not `heatmap`) for polygon H3 cells. CORS origin is `http://localhost:5173`.
> Out-of-state fires render on a separate purple layer, never mixed with in-state.
> Always check Known Risks before implementing a new feature.

> **Forge (Backend Agent):** Own Phases 1–3 and 5. See [api-reference.md](api-reference.md),
> [database-schema.md](database-schema.md), [data-sources.md](data-sources.md), and
> [out-of-state-classification.md](out-of-state-classification.md). Pre-serialize H3
> polygons using `H3.net GetCellBoundary()`. Always write to `ingestion_log`.
> `FeedService` is a singleton — inject into all event-producing services. Out-of-state
> events must never affect `current_risk_score`. Always check Known Risks.

---

## Contributing

All agents and contributors must:
1. **Read the Documentation folder first** before any architectural decision
2. Reference the applicable spec document before writing code
3. Update Phase status as tasks complete
4. Document deviations from the planned stack in this folder
5. Reference Known Risks when implementing spatial or AI features
