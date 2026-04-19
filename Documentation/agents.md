# Development Agents

> **SDD Note:** This document is the authoritative registry of all agents involved in the
> development of the Colorado Wildfire RAG Analyzer. Every agent must read this file and
> the main [README.md](README.md) before beginning any task. Agents must update their
> status here when work begins and when it completes.

---

## Agent Roster

| Name | Role | Phase Ownership | Current Status |
|---|---|---|---|
| **Ember** | Frontend development | Phase 4 | Research complete — awaiting spec finalization |
| **Forge** | Backend development | Phases 1–3, 5 | Research complete — awaiting spec finalization |
| **Scout** | Wildfire research + spec review coordinator | Phase 0 | Complete |
| **Atlas** | Architecture & Data spec review | Phase 0 | Complete |
| **Beacon** | UX, Product & Market spec review | Phase 0 | Complete |

---

## Agent Profiles

---

### Ember — Frontend Agent

**Role:** Owns all frontend implementation for Phase 4.

**Primary Spec Documents:**
- [README.md](README.md) — project overview, tech stack, project structure
- [api-reference.md](api-reference.md) — JSON contracts the frontend consumes
- [live-feed.md](live-feed.md) — SSE feed panel design and `EventSource` implementation
- [out-of-state-classification.md](out-of-state-classification.md) — layer separation rules and UI copy

**Tasked With:**
- Scaffold Vite (vanilla JS) project in `frontend/`
- Initialize MapLibre GL JS centered on Colorado with MapTiler `outdoor-v2` basemap
- Implement H3 risk fill layer (`heatmap.js`) with color interpolation expression
- Implement fire history perimeter layer (`fireHistory.js`)
- Implement in-state active fire point heatmap (`activeFires.js`)
- Implement out-of-state fire point layer (`outOfStateFires.js`) — purple, distinct from in-state
- Implement HMS smoke plume fill layer (`smokePlumes.js`) — grey-brown, semi-transparent
- Implement cell click → sidebar with cell stats, Chart.js fire history chart, RAG response
- Implement live feed panel (`feed.js`) — `EventSource('/api/feed')`, card rendering, filter controls
- Feed card click → fly map to location + open sidebar
- Wire all layers to live backend endpoints once Phases 1–3 are complete

**Key Constraints:**
- Risk fill layer uses `fill` type — NOT `heatmap` type (heatmap is for point features only)
- CORS origin is `http://localhost:5173` (Vite default) — backend must allow this
- Backend pre-serializes H3 polygons — frontend does NOT convert H3 index strings to polygons for rendering
- Out-of-state fires render on a SEPARATE layer, never mixed with in-state fire layer
- h3-js v4 breaking change: `cellToBoundary()` returns `[lat, lng]` — must reverse to `[lng, lat]` for GeoJSON
- All tooltips for out-of-state events must include the standard disclaimer (see [out-of-state-classification.md](out-of-state-classification.md))

**Status:** Research complete. Awaiting spec finalization from Scout before Phase 4 coding begins.

---

### Forge — Backend Agent

**Role:** Owns all backend implementation for Phases 1–3 and 5.

**Primary Spec Documents:**
- [README.md](README.md) — project overview, tech stack, NuGet packages, project structure
- [api-reference.md](api-reference.md) — endpoint contracts to implement
- [database-schema.md](database-schema.md) — all table definitions and EF Core notes
- [data-sources.md](data-sources.md) — ingestion details per data source
- [risk-model.md](risk-model.md) — H3 grid strategy and risk scoring formula
- [out-of-state-classification.md](out-of-state-classification.md) — `OriginClassifierService` spec
- [live-feed.md](live-feed.md) — `FeedService` and `FeedController` implementation spec

**Tasked With:**

*Phase 1 — Data Foundation:*
- Write `docker-compose.yml` (PostgreSQL/PostGIS + Qdrant)
- Write SQL init scripts (`001_extensions.sql`, `002_schema.sql`)
- Scaffold ASP.NET Core 8 controller-based project
- Add all NuGet packages from the README package list
- Build `MtbsIngester.cs` — Shapefile → PostGIS with NAD83→WGS84 reprojection via ProjNet
- Build `H3GridService.cs` — generate H3 res-6 and res-8 grid for Colorado, pre-serialize polygons
- Compute fire-to-cell intersections and aggregate metrics on `h3_cells`
- Implement `GET /api/fire-history`, `GET /api/risk-grid`, `GET /api/health`

*Phase 2 — Risk Scoring:*
- Build `NoaaService.cs` with Polly retry; cache per H3-6 cell for 1 hour
- Implement risk scoring formula from [risk-model.md](risk-model.md)
- Schedule hourly refresh via `BackgroundService` + `PeriodicTimer`

*Phase 3 — RAG Engine:*
- Configure Semantic Kernel with Ollama connectors
- Create Qdrant collection `wildfire_docs` (768-dim, cosine)
- Build `InciwebIngester.cs` — RSS → HTML → AngleSharp → chunk → embed → Qdrant
- Build `RagService.cs` — embed → search → weather → prompt → llama3.2
- Implement `POST /api/query`

*Phase 5 — Live Data & Classification:*
- Seed `state_boundaries` from Census TIGER/Line
- Build `OriginClassifierService.cs`, `HmsService.cs`, `AirNowService.cs`
- Build `FeedService.cs` (singleton publish channel) and `FeedController.cs` (SSE)
- Wire `FeedService.PublishAsync()` into all event-producing services
- Implement `GET /api/active-fires` (with origin tags), `GET /api/smoke-plumes`, `GET /api/feed`

**Key Constraints:**
- `FeedService` is a singleton — inject into all services that produce observable events
- Out-of-state fire/smoke events must NEVER affect `current_risk_score`
- Always write to `ingestion_log` before and after every ingestion run (idempotency)
- Pre-serialize H3 polygons server-side using `H3.net GetCellBoundary()` — frontend expects complete polygons
- MTBS Shapefiles use NAD83 (EPSG:4269) — always reproject to WGS84 before PostGIS insert
- CORS must allow `http://localhost:5173` and `http://localhost:3000`

**Status:** Research complete. Awaiting spec finalization from Scout before Phase 1 coding begins.

---

### Scout — Lead Research Agent

**Role:** Phase 0 research coordinator. Researches the current wildfire landscape,
identifies the most valuable information needs, evaluates monetization potential, reads
the full project spec, and coordinates the Architecture and UX agents to produce a
unified pre-development spec review.

**Primary Spec Documents:** All 7 documents in `Documentation/`

**Tasked With:**
1. Research current 2025–2026 wildfire conditions, threats, and agency priorities
2. Research what information homeowners, fire professionals, and researchers most need
3. Research monetization opportunities (insurers, utilities, municipalities, grants)
4. Read the complete project spec (all 7 Documentation files)
5. Spawn and coordinate the Architecture & Data Agent and UX, Product & Market Agent
6. Compile a unified final report with prioritized spec change recommendations

**Deliverable:** A final report containing:
- Current wildfire threat landscape findings
- User information needs assessment
- Lucrative opportunity analysis
- Prioritized top-10 spec changes (reconciled across both specialist agents)
- Items that should NOT change and why

**Status:** Complete. Final report delivered to project owner.

---

### Atlas — Architecture & Data Agent

**Role:** Phase 0 specialist. Reviews the project spec from a backend architecture,
data pipeline, and AI/RAG best practices perspective.

**Primary Spec Documents:** All 7 documents in `Documentation/` (provided inline by Lead Research Agent)

**Tasked With:**
- Identify missing data sources given current wildfire conditions
- Evaluate weaknesses in the risk model formula and weights
- Identify database schema gaps or normalization issues
- Recommend RAG pipeline improvements (chunking, re-ranking, prompt design)
- Flag API design issues or scalability concerns
- Review the H3 grid strategy for completeness
- Follow industry best practices throughout

**Deliverable:** Prioritized list of recommended spec changes with justification, referencing specific spec sections.

**Status:** Complete. Findings incorporated into Scout's final report.

---

### Beacon — UX, Product & Market Agent

**Role:** Phase 0 specialist. Reviews the project spec from a user experience, product
strategy, and market opportunity perspective.

**Primary Spec Documents:** All 7 documents in `Documentation/` (provided inline by Lead Research Agent)

**Tasked With:**
- Assess whether planned features match what users actually need
- Identify missing features that would increase value or enable monetization
- Evaluate the UI design (heatmap, live feed, sidebar) for usability
- Identify monetization models not captured in the current spec
- Assess competitive positioning against existing tools (AirNow, InciWeb, NIFC, Zonehaven)
- Follow UX and product best practices throughout

**Deliverable:** Prioritized list of recommended spec changes with justification, referencing specific spec sections.

**Status:** Complete. Findings incorporated into Scout's final report.

---

## Agent Communication Protocol

- Agents do not communicate directly with each other except when explicitly orchestrated
  by a coordinator agent (e.g., Lead Research Agent spawning specialist agents)
- All shared state lives in the `Documentation/` folder — agents read and write spec files,
  not memory or in-process state
- When an agent completes a task that changes a spec file, it must note the change in the
  relevant document's header or a `## Changelog` section
- Frontend and Backend agents coordinate through the API contracts in [api-reference.md](api-reference.md) —
  this is the only interface between them

---

## Handoff Order

```
Phase 0:  Scout (Lead Research)
              ├── Atlas (Architecture & Data)  ──┐
              └── Beacon (UX, Product & Market) ──┴── Final Report → Owner Review
                                                            │
Phase 1–3:                                        Forge (Backend — data + API)
                                                            │
Phase 4:                                          Ember (Frontend — map + feed UI)
                                                            │
Phase 5:                                          Forge (Backend — live data + classification)
                                                            │
Phase 6:                                          Ember + Forge (cloud migration)
```

---

## Updating This Document

When an agent's status changes, the orchestrating agent or the owner should update the
**Agent Roster** table and the agent's **Status** field in their profile. This document
is the single source of truth for who is doing what at any point in the project.
