# CLAUDE.md — Colorado Wildfire RAG Analyzer

## Project Overview

Full-stack wildfire risk analysis app for Colorado. Combines historical burn data, live weather, terrain, and AI document retrieval into a per-H3-cell risk score, with a natural language query interface and SSE live feed.

- **Backend:** ASP.NET Core 8, controller-based, PostgreSQL/PostGIS, Qdrant, Semantic Kernel + Ollama
- **Frontend:** Vanilla JS + Vite, MapLibre GL JS 4.x, h3-js 4.x
- **Infrastructure:** Docker (Postgres + Qdrant), Ollama on host for GPU access

Full architecture, API contracts, and data models are in `Documentation/`. Read the applicable spec before writing code.

## Starting the Dev Stack

```powershell
.\start-dev.ps1
```

This kills any running backend, rebuilds, then starts Docker + backend + frontend in separate windows. See `Documentation/README.md` → "Starting Dev Services" for details.

## Key Conventions

- **No React.** Vanilla JS only. No routing libraries, no state trees.
- **Controller-based API** (not Minimal API) — required for auth middleware in Phase 6.
- **H3 coordinate order:** pocketken.H3 v4 `GetCellBoundary()` returns NTS Polygon in `[lng,lat]` (GeoJSON) order. h3-js v4 `cellToBoundary()` returns `[lat,lng]` — reverse before use.
- **Out-of-state fires must never affect `current_risk_score`.** They render on a separate purple layer.
- **NOAA Weather.gov requires a `User-Agent` header** — `CoWildfireAnalyzer/1.0 (contact@email.com)`.
- **FeedService is a singleton** — inject via DI into all event-producing services.
- **Pre-serialize H3 polygons** — do not compute boundaries at request time.

## Ports

| Service | Port |
|---|---|
| PostgreSQL | 5432 |
| Qdrant REST | 6333 |
| Qdrant gRPC | 6334 |
| ASP.NET Core API | 5000 / 5001 |
| Vite frontend | 5173 |

## Current Phase

Phase 7 complete. Phase 8 (cloud migration to Azure + Claude API) is next. Phase 9 (live feed enrichment) is planned in parallel.

See `Documentation/README.md` for full phase breakdown and task lists.
