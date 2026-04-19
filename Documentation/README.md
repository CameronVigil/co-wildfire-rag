# Colorado Wildfire RAG Analyzer

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
| 1 | Not Started | Data ingestion + fire history grid |
| 2 | Not Started | Real-time risk scoring |
| 3 | Not Started | RAG query engine |
| 4 | Not Started | Heatmap frontend |
| 5 | Not Started | Live fire layer, alerts, origin classification |
| 6 | Not Started | Cloud migration (Azure + Claude) |

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
| Basemap | MapTiler Cloud (free tier) | 100k tile requests/month; `outdoor-v2` style |
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
│   │   │   └── HealthController.cs        ← GET /api/health
│   │   ├── Services/
│   │   │   ├── RiskScoringService.cs
│   │   │   ├── RagService.cs
│   │   │   ├── NoaaService.cs
│   │   │   ├── FirmsService.cs
│   │   │   ├── H3GridService.cs
│   │   │   ├── EmbeddingService.cs
│   │   │   ├── OriginClassifierService.cs
│   │   │   ├── HmsService.cs
│   │   │   ├── AirNowService.cs
│   │   │   └── FeedService.cs
│   │   ├── Ingestion/
│   │   │   ├── MtbsIngester.cs
│   │   │   ├── NifcIngester.cs
│   │   │   └── InciwebIngester.cs
│   │   ├── Data/
│   │   │   └── AppDbContext.cs
│   │   ├── Models/
│   │   │   ├── H3Cell.cs
│   │   │   ├── FireEvent.cs
│   │   │   ├── ActiveFireDetection.cs
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
        ├── sidebar.js
        ├── feed.js
        ├── layers/
        │   ├── heatmap.js
        │   ├── fireHistory.js
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

### Phase 6 — Cloud Migration
- [ ] Swap Ollama for Claude API via Semantic Kernel connector
- [ ] Migrate to Azure Database for PostgreSQL
- [ ] Migrate Qdrant to Azure AI Search vector index
- [ ] Deploy API to Azure App Service
- [ ] Deploy frontend to Azure Static Web Apps
- [ ] Add Azure AD B2C authentication

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
