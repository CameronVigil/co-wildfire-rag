# Scout Final Report — Phase 0 Research & Architecture Review
**Agent:** Scout (Lead Research Agent)
**Date:** 2026-04-03
**Project:** Colorado Wildfire RAG Analyzer
**Status:** Phase 0 Complete — Ready for Forge Phase 1 with recommended spec updates

---

## Executive Summary

The Colorado Wildfire RAG Analyzer is a well-conceived, technically sound project entering Phase 1 at an exceptionally opportune moment. The 2025–2026 wildfire season is shaping up as one of the most dangerous on record for Colorado, the wildfire analytics market is growing at ~16% CAGR toward $4B by 2033, and the competitive landscape has clear gaps this project can exploit. The spec is strong in architecture, data pipeline design, and out-of-state classification — but needs targeted improvements in user-facing features, risk model sophistication, and monetization path clarity before Forge begins coding.

---

## 1. Current Wildfire Threat Landscape (2025–2026)

### Colorado Conditions — Critical Context for This Project

**2025 was a record-breaking year.** Wildfires burned more than 200,000 acres in Colorado in 2025, driven by below-average snowpack, record-setting heat, and persistent drought across the western half of the state. The Mountain West experienced record drought, wildfires, and windstorms, establishing a dangerous precedent heading into 2026.

**2026 outlook is worse.** Colorado had one of its lowest snowpacks on record during the 2025–26 winter — snowpack sitting at 20–45% of normal across most of the state. Drought conditions are expected to worsen through spring 2026. The NIFC April 2026 outlook shows:
- **Above-normal significant fire potential** across the Front Range, southern Colorado, and West Slope through June–July
- **Southeast Colorado and the plains** facing elevated risk from wind events combined with dormant, high-loading fuels
- **Bark beetle-affected forests** (30–40% of Colorado's lodgepole pine stands affected) providing extreme fuel loading in the mountains

**Why this matters for the spec:**
- The H3 grid, RAWS integration, LANDFIRE beetle-kill data, and out-of-state smoke classification are all highly relevant NOW, not just as future features
- Users in 2026 are actively searching for exactly what this tool provides: real-time risk + historical context + smoke origin classification
- NOAA Red Flag Warnings are issuing with increasing frequency — the SSE alert feed will deliver immediate value

**Neighboring state threats are Colorado's smoke problem.** New Mexico, Utah, Arizona, and increasingly California continue to generate smoke transport events that blanket southern and western Colorado. The HMS smoke classification feature is not a nice-to-have — it directly addresses the most common wildfire-adjacent experience for Colorado residents.

### Key Risk Factors Not Fully Captured in Current Spec
1. **Atmospheric instability / dry thunderstorm ignitions** — Lightning starts are increasing in drought years; NOAA Sounding data is not in the current data sources
2. **Wind event timing** — Chinook winds (Front Range) and downslope events create rapid fire spread; the current wind normalization caps at 60 mph but doesn't capture event-scale gusts vs. sustained wind differently
3. **Urban-wildland interface density** — WUI expansion in Jefferson, Larimer, Boulder, El Paso counties is accelerating; no parcel-density or housing unit count variable in the risk model

---

## 2. User Information Needs Assessment

### Homeowner / Resident
**What they actually need (beyond the spec):**
- **Address-first, map-second** — The spec puts address search in Phase 4. For homeowners, the address input box IS the product. They will not understand an H3 grid before they can look up their own house. This is the single highest-priority UX fix.
- **Evacuation zone integration** — The homeowner persona lists "know when to evacuate" as a primary goal, but no evacuation zone data source is specified anywhere in the spec. Zonehaven/Know Your Zone owns this space; an API integration or CDPS data layer would differentiate.
- **Plain-language risk explanation** — "Your area is rated 7.4 / 10 (High)" is not actionable. Homeowners need: "What does this mean for me right now? What should I do?" The `actionableGuidance` field in the API exists but no content strategy is defined.
- **Push/SMS alerts for free tier** — Gating alert delivery behind the $49/month Pro tier will kill homeowner adoption. A free-tier email alert for "your watched address crossed into Very High risk" costs ~$0.001/email via SendGrid and is a powerful acquisition tool.
- **Mobile-first layout** — The spec's layout (320px feed panel to the right of map) is desktop-only. Homeowners in evacuation-adjacent situations will be on mobile.

### Fire Professional / Emergency Manager
**What they actually need:**
- **NFDRS / Energy Release Component (ERC)** — Fire behavior analysts rely on NFDRS indices, particularly ERC and Burning Index, not just raw wind/humidity. The current risk model doesn't include these composites.
- **Export and integration** — GIS export (GeoJSON, Shapefile, KMZ) for loading into ESRI products is essential. The spec mentions GeoJSON/CSV export only in the Enterprise tier — fire professionals at county OEM level need this without a $500/month subscription.
- **Incident commander view** — A high-density data view showing multiple H3 cells, RAWS station readings, and active incidents simultaneously. The current spec is single-cell focused via sidebar click.
- **Historical analog queries** — "Show me cells that burned under similar conditions" is the most powerful RAG use case for fire professionals; not yet reflected in the RAG prompt design.

### Analyst / Enterprise
**What they actually need:**
- **Parcel-level scoring** — ZestyAI, Verisk, and First Street all offer this. The spec mentions it as a future feature but it needs a roadmap and placeholder schema.
- **Time-series risk trends** — Insurance underwriters need to show regulators how risk has changed over time. The current schema overwrites `current_risk_score` hourly with no history table.
- **Bulk API with SLA** — The spec describes enterprise tier well; the key gap is no webhook/push delivery option (they want data pushed to them, not pulled).
- **Portfolio correlation** — Utilities and insurers need to understand correlated risk across many cells simultaneously, not just point queries.

---

## 3. Lucrative Opportunity Analysis

### Market Context
- **Wildfire Insurance Analytics market:** $1.34B in 2024, growing 16.2% CAGR → ~$4.12B by 2033
- **Wildfire Detection Systems market:** $2.1B in 2024 → $7.8B by 2033
- **Wildfire Protection/Consulting:** Combined $28B+ addressable market
- **Insurtech investment:** Rose 19.5% to $5.08B in 2025; wildfire risk analytics is a core investment theme

### Top Monetization Paths (Ranked by Near-Term Viability)

**1. Insurance / Reinsurance API — Highest Revenue, Fastest Enterprise Path**
- ZestyAI raised significant funding selling exactly this: per-parcel wildfire risk scores to insurance carriers
- Colorado is a target state: major insurers (State Farm, Allstate) are reducing or eliminating CO homeowner policies
- The regulatory pressure on insurers to justify underwriting decisions creates demand for third-party risk scores
- **Realistic revenue:** $25K–$250K/year per carrier for CO portfolio scoring API access
- **Spec gap:** No parcel table, no parcel-level scoring endpoint, no batch job queue

**2. Utility Corridor Risk Monitoring — Regulatory-Mandated**
- Colorado PUC has pushed utilities (Xcel Energy, Black Hills Energy) toward wildfire mitigation plans
- Transmission line corridors adjacent to H3 cells scoring above 7.0 need documented risk assessments
- **Realistic revenue:** $50K–$200K/year per utility for corridor monitoring + quarterly PDF reports
- **Spec strength:** H3 grid + GeoJSON export + risk scoring is a natural fit; needs a `transmission_corridors` or line-segment overlay capability

**3. County / Municipal Subscriptions**
- 10 highest-risk CO counties (Larimer, Boulder, Jefferson, El Paso, Garfield, Routt, Pitkin, La Plata, Montezuma, Archuleta) are natural customers
- County emergency managers need tools that their GIS staff can use and that integrate with existing CAD/EOC systems
- **Realistic revenue:** $15K–$40K/year per county, so $150K–$400K ARR from 10 counties
- **Spec strength:** Out-of-state classification, live feed, NOAA alert integration all align with OEM needs
- **Spec gap:** No GIS export for fire professionals without enterprise tier

**4. Federal Grants — Recalibrate Expectations**
- **BRIC program was canceled** in April 2025 by the Trump administration; $3B in pending grants returned to Treasury
- **HMGP** is in funding limbo; new allocations paused
- **USDA Forest Service** wildfire resilience programs remain active and are a better fit given open-data strategy
- **Colorado Wildfire Resilience Fund** (state-level) and **CDPHE environmental justice grants** are viable alternatives
- **NSF CIVIC** and **NIH environmental health** grants for the research user segment
- **Revised grant strategy:** Lead with USDA Forest Service partnership and Colorado-specific state programs; deprioritize FEMA BRIC/HMGP in current political environment

**5. Consumer Subscription (Pro Tier) — Longer-Term, Volume Dependent**
- $49–99/month Pro tier is well-priced for serious homeowners and small-business owners in WUI zones
- 300,000+ households in Colorado WUI zones is the addressable market
- **Realistic revenue at 0.1% conversion:** ~300 subscribers × $74/month = $22K MRR (~$267K ARR)
- The free tier with email alerts (recommended above) is the acquisition funnel

---

## 4. Prioritized Top-10 Spec Changes

*Reconciled across Atlas (Architecture) and Beacon (UX/Product). Ranked by impact.*

---

### #1 — Add `h3_risk_history` Table (Atlas — Critical)
**Problem:** `h3_cells.current_risk_score` is overwritten every hour. The spec's enterprise use case explicitly states "How has risk changed across Jefferson County parcels over 5 years?" This is impossible without a history table.

**Recommended change (database-schema.md):**
Add a new table:
```sql
CREATE TABLE h3_risk_history (
    id              BIGSERIAL PRIMARY KEY,
    h3_index        VARCHAR(20) NOT NULL,
    resolution      SMALLINT NOT NULL,
    risk_score      NUMERIC(4,2) NOT NULL,
    risk_category   VARCHAR(20) NOT NULL,
    wind_speed_mph  NUMERIC(5,1),
    relative_humidity_pct NUMERIC(5,1),
    fuel_moisture_pct NUMERIC(5,1),
    drought_index   NUMERIC(5,2),
    weather_source  VARCHAR(10),
    scored_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_risk_history_h3_scored ON h3_risk_history(h3_index, scored_at DESC);
CREATE INDEX idx_risk_history_scored_at ON h3_risk_history(scored_at DESC);
```
Retain hourly snapshots for 90 days; daily aggregates indefinitely. Add `GET /api/risk-history/{h3Index}` endpoint to api-reference.md. This unlocks the Chart.js time-series chart in the sidebar AND the enterprise trend analysis use case.

**Justification:** Referenced in README.md enterprise persona, Phase 4 sidebar Chart.js chart, and monetization section. Zero cost to implement alongside Phase 2.

---

### #2 — Add Address-First Entry Point as Phase 1 Frontend Stub (Beacon — Critical)
**Problem:** Address search is deferred to Phase 4. For homeowners (the largest user segment), the map is intimidating without first knowing "where am I on this map." Every competing consumer tool (AirNow, Zillow, Redfin) opens with address search.

**Recommended change (README.md Phase 1):**
Add a centered address search bar as a Phase 1 frontend stub — even with a static "we're building this" placeholder response. Wire it to MapTiler Geocoding API (already in the stack) to fly the map to the entered address. The actual `GET /api/cell-at-point` lookup can remain Phase 4, but the visual entry point must be present from the first demo.

**Also add to api-reference.md:** Document `GET /api/cell-at-point?lat={lat}&lon={lon}` — it is referenced in the Phase 4 checklist but absent from the API spec.

---

### #3 — Add NFDRS Energy Release Component (ERC) to Risk Model (Atlas — High)
**Problem:** The current risk formula uses raw wind speed, humidity, and fuel moisture as independent variables. Professional fire behavior uses composite indices — primarily ERC (Energy Release Component) from NFDRS, which integrates fuel dryness, wind, and atmospheric conditions into a single value that correlates strongly with large fire occurrence.

**Recommended change (risk-model.md):**
- Add ERC as an optional input variable sourced from WFAS NFDRS station data (already in the data sources registry)
- When ERC is available from a nearby RAWS station, use it as a modifier on the drought+fuel moisture component
- Add `nfdrs_erc` column to `h3_cells` schema
- Reference: NIFC uses ERC as the primary trigger for Preparedness Level changes

**Justification:** This is the single most credibility-boosting change for fire professional users and grant applications to USDA Forest Service.

---

### #4 — Add `parcels` Table Stub and Parcel-Score Endpoint Placeholder (Atlas + Beacon — High)
**Problem:** The insurance monetization path (the highest-revenue opportunity) requires parcel-level scoring. The spec mentions it only as a vague "future" feature with no schema, no data source, and no API placeholder.

**Recommended change (database-schema.md + api-reference.md):**
- Add stub table: `parcels (id, parcel_id, county_fips, h3_index_6, h3_index_8, address, lat, lon, boundary GEOMETRY(Polygon,4326), created_at)` — seeded from CO county assessor open data (most CO counties publish this)
- Add placeholder endpoint `POST /api/parcels/score` in api-reference.md with a `501 Not Implemented` response and documented request/response schema
- Data source: Colorado county assessor GIS portals (Jefferson, Larimer, Boulder, El Paso all publish parcel shapefiles)

**Justification:** Creates a clear Phase 6+ roadmap item and allows enterprise sales conversations before implementation.

---

### #5 — Add Hybrid BM25 + Vector Re-ranking to RAG Pipeline Spec (Atlas — High)
**Problem:** The RAG spec (Phase 3) specifies embedding → geographic pre-filter → Qdrant similarity search → prompt. This is vanilla vector RAG. For wildfire incident documents, keyword matching on fire names, dates, and locations is critical. Pure semantic search misses exact fire-name queries ("What happened in the Cameron Peak Fire?").

**Recommended change (data-sources.md InciWeb section + README.md Phase 3):**
- Add BM25 keyword index alongside Qdrant vector index (Qdrant supports sparse vectors for BM25 hybrid search natively since v1.7)
- Specify re-ranking step: retrieve top-20 by vector similarity, top-20 by BM25, merge with RRF (Reciprocal Rank Fusion), pass top-5 to LLM
- Add `query_type` detection: geographic queries → geographic pre-filter first; named-incident queries → BM25 first
- Update Qdrant collection config to enable sparse vectors

**Justification:** Improves RAG answer quality significantly for the most common professional queries (named incident lookups). Qdrant v1.9.x (already pinned) supports this natively.

---

### #6 — Specify Mobile-Responsive Layout (Beacon — High)
**Problem:** The live-feed.md layout spec is desktop-only: map + 320px fixed-width feed panel side-by-side. On mobile (which homeowners in emergency situations will use), this renders as an unusable 200px-wide map.

**Recommended change (live-feed.md + README.md):**
Add a mobile layout spec:
- Below 768px: feed panel collapses to a bottom drawer (toggle button: "🔔 3 new alerts")
- Map fills full screen on mobile
- RAG sidebar becomes a full-screen overlay (not a bottom drawer)
- Address search bar pins to the top on mobile
- Feed card tap → full-screen event detail (not map fly-to, which disorients mobile users)

This is a CSS/layout spec change, not a backend change. MapLibre GL JS is fully mobile-compatible.

---

### #7 — Add Evacuation Zone Data Layer (Beacon — High)
**Problem:** The homeowner persona's primary stated need is "know when to evacuate." The spec has zero reference to evacuation zones, pre-evacuation zones, or evacuation routes. Zonehaven (now "Know Your Zone") has built an entire company on this single feature.

**Recommended change (data-sources.md + README.md):**
- Add data source: Colorado DFPC (Division of Fire Prevention & Control) evacuation zone polygons — available via Colorado Open Data Portal
- Add layer `evacuation-zones` to the frontend layer stack
- Add `GET /api/evacuation-zones?bounds=` endpoint stub
- Style: color-coded by zone level (Go = red, Set = orange, Ready = yellow) with zone ID label
- Note: This data is county-managed and inconsistent; document limitations clearly

**Justification:** Highest-value homeowner feature. Direct gap vs. all existing tools. No backend complexity — purely a GeoJSON layer.

---

### #8 — Replace WFAS HTML Scraping with SynopticData (MesoWest) API for Fuel Moisture (Atlas — Medium)
**Problem:** WFAS fuel moisture is ingested via AngleSharp HTML parsing — a fragile approach dependent on WFAS not changing their page layout. WFAS itself sources its data from the same RAWS/MesoWest network already planned for Phase 2. MesoWest/Synoptic Data API (already in Phase 2 checklist) returns fuel moisture directly via REST API.

**Recommended change (data-sources.md):**
- Remove WFAS HTML scraping as primary fuel moisture source
- Add MesoWest Synoptic API as primary fuel moisture source: `GET https://api.synopticdata.com/v2/stations/timeseries?vars=fuel_moisture&state=co`
- Retain WFAS as a secondary/validation source
- Update `RawsService.cs` spec to pull fuel moisture alongside wind/humidity in one API call

**Justification:** Reduces maintenance risk, improves data freshness (RAWS stations report hourly vs. WFAS daily), and simplifies the ingestion pipeline.

---

### #9 — Add `GET /api/risk-grid` Response Caching + ETag Support (Atlas — Medium)
**Problem:** The H3-6 risk grid (~220 cells, ~100KB) is refreshed hourly but every browser tab hitting the map will poll this endpoint. With no ETag or cache headers, this creates unnecessary load. At even modest traffic (100 concurrent users), this becomes a bottleneck.

**Recommended change (api-reference.md + README.md Known Risks):**
- Add `ETag` response header to `/api/risk-grid` based on `MAX(risk_score_updated_at)` across cells
- Return `304 Not Modified` when client sends matching `If-None-Match`
- Add `Cache-Control: public, max-age=300` (5-min client cache for res-6)
- Frontend polls every 10 minutes (not on every map move) and uses conditional GET
- Add this to the Known Risks table as a scalability note

---

### #10 — Revise Grant Strategy: Remove BRIC, Add USDA + Colorado State Programs (Beacon — Medium)
**Problem:** The README.md monetization section lists "FEMA BRIC" as a grant opportunity. BRIC was canceled in April 2025 by the Trump administration; all FY2020–2023 applications were rejected and funds returned to Treasury. HMGP is also in funding limbo.

**Recommended change (README.md Monetization section):**
Replace the FEMA BRIC reference with:
- **USDA Forest Service — Community Wildfire Defense Grant (CWDG):** $200M program, active, targets WUI communities; this project's open-data + community risk assessment angle is a strong fit
- **Colorado Wildfire Resilience Fund** (CO DFPC administered): Active state-level funding for risk assessment tools
- **NSF Convergence Accelerator:** Track F (Open Knowledge Network) — directly aligns with RAG + open data approach
- **CDPHE Environmental Justice grants:** Southern Colorado smoke burden is a documented EJ issue; out-of-state smoke classification directly supports this

---

## 5. Items That Should NOT Change

*These are spec strengths — protect them.*

### H3 Hexagonal Grid Strategy
The choice of H3 at resolutions 6 and 8 is correct. H3 provides neighbor lookup efficiency, consistent cell areas, and clean spatial joins. The viewport-bounded approach for res-8 is the right performance tradeoff. Do not switch to county polygons or custom grid systems. The `?bounds=` pattern is well-designed.

### Out-of-State Classification Architecture
The `OriginClassifierService` design — with `ST_Within` PostGIS checks, state boundary caching in memory, and strict "zero impact on risk score" isolation for out-of-state events — is exactly right. This is a genuine competitive differentiator. The smoke transport inference logic using wind direction + distance + FRP threshold is sophisticated and correct. Do not simplify this.

### Controller-Based ASP.NET Core (Not Minimal API)
The README correctly notes that controller-based routing is preferred for auth middleware in Phase 6. This is good architectural foresight. Minimal API patterns make per-route auth middleware significantly more complex. Keep controllers.

### Vanilla JS + MapLibre GL (No React)
For a map-centric application, this is the right call. React overhead (virtual DOM, bundle size, hydration) provides no value when the primary interaction surface is a WebGL canvas. MapLibre GL JS is a first-class mapping library that performs better without a framework wrapper. This decision will look even more correct at Phase 6 when bundle size matters for Azure Static Web Apps CDN delivery.

### Semantic Kernel + Ollama for Local RAG
The choice to use Semantic Kernel with Ollama locally, migrating to Claude API in Phase 6, is a solid architecture. It keeps local development free and fast, and the SK connector abstraction means the migration is a config swap, not a code rewrite. The `llama3.2` (8b) model choice is appropriate for local RAG grounding quality.

### Strict SDD (Spec-Driven Development) Process
The requirement to update specs before changing code is the most important process decision in the project. With multiple agents (Scout, Forge, Ember) working across phases, spec-first prevents divergent implementations. Do not allow any agent to "just fix it in code and update the spec later."

### RAWS-First, NOAA-Fallback Weather Strategy
Using observed RAWS station data as primary input with NOAA gridded forecast as fallback is meteorologically correct and practical. RAWS stations are the gold standard for fire weather because they are sited specifically for fire behavior assessment (exposed ridges, representative terrain). The 50km radius threshold is standard in fire weather analysis.

### `ingestion_log` Idempotency Pattern
The pattern of checking `ingestion_log` before processing and using `ON CONFLICT DO NOTHING` is correct and will prevent duplicate data issues during development restarts. Protect this pattern — do not allow ingesters to skip the log check for "speed."

---

## 6. Recommended Next Steps Before Forge Starts Phase 1

*Ordered by dependency and urgency.*

### Immediate (Before Any Code)

**Step 1: Add `h3_risk_history` table to database-schema.md**
This is a zero-cost schema addition that must be in place before Phase 2 risk scoring begins. If added after, it requires a backfill migration. Takes 30 minutes to spec.

**Step 2: Add `GET /api/cell-at-point` to api-reference.md**
It is referenced in the Phase 4 checklist but missing from the spec. Forge will implement Phase 1 endpoints; the contract should be defined now so there are no surprises in Phase 4.

**Step 3: Add `GET /api/risk-history/{h3Index}` to api-reference.md**
Required for Phase 4 Chart.js time-series chart. Define the contract now.

**Step 4: Confirm MesoWest/Synoptic API token is obtainable**
The Phase 2 checklist calls for a MesoWest token (free tier). Register at synopticdata.com before Phase 2 to avoid a blocking dependency. Free tier provides ~500 req/hour.

### Before Phase 2 (Risk Scoring)

**Step 5: Add `nfdrs_erc` column to `h3_cells` schema**
ERC integration (Spec Change #3) requires this column. Add it now so Phase 2 can optionally populate it without a migration.

**Step 6: Add `h3_risk_history` insertion logic to `RiskScoringService.cs` spec**
The service spec should state: "After persisting `current_risk_score` to `h3_cells`, insert a row into `h3_risk_history`."

### Before Phase 3 (RAG)

**Step 7: Update Qdrant collection spec to enable sparse vectors (BM25 hybrid)**
Qdrant v1.9.x supports sparse vectors. The collection creation config needs `sparse_vectors` enabled. This is a one-line change to the Phase 3 checklist but cannot be changed after the collection is created without dropping and recreating it.

**Step 8: Define RAG prompt template in the spec**
The current spec says "embed → geographic pre-filter → search → weather → prompt → llama3.2" but does not specify the actual system prompt or user prompt template. Add a `rag-prompt-template.md` or embed it in data-sources.md. This is critical for RAG quality — vague prompts produce vague answers.

### Before Phase 4 (Frontend)

**Step 9: Add mobile layout spec to live-feed.md**
Ember needs the mobile layout breakpoint behavior defined before building the feed panel.

**Step 10: Register all external API keys**
- NASA FIRMS (free, ~24h): firms.modaps.eosdis.nasa.gov
- MapTiler (free, whitelist localhost): maptiler.com/cloud
- EPA AirNow (free): docs.airnowapi.org
- MesoWest/Synoptic (free tier): synopticdata.com

Keys cannot be obtained during a coding sprint. Register them now.

---

## Appendix A: Spec Coverage Assessment

| Spec Document | Coverage Quality | Gaps Found |
|---|---|---|
| README.md | Excellent | BRIC grant outdated; mobile layout missing |
| database-schema.md | Very Good | Missing `h3_risk_history`; missing `parcels` stub |
| risk-model.md | Good | Missing ERC/NFDRS; RAWS-zero-station fallback unspecified |
| api-reference.md | Good | Missing `cell-at-point`; missing `risk-history` endpoint |
| data-sources.md | Good | WFAS fragile; fuel moisture from MesoWest preferred |
| live-feed.md | Very Good | Missing mobile layout spec |
| out-of-state-classification.md | Excellent | No gaps found; strongest spec document |

---

## Appendix B: Competitive Landscape Summary

| Tool | Strengths | Weaknesses vs. This Project |
|---|---|---|
| InciWeb | Authoritative incident reports | No risk scoring, no spatial analysis, no RAG |
| AirNow | Real-time AQI, trusted brand | No fire risk, no history, no spatial grid |
| NIFC Active Fire Map | Official, comprehensive | No risk model, no RAG, no local context |
| First Street Fire Factor | Parcel-level, nationwide, ML | No real-time RAWS, no RAG, no live feed, commercial |
| ZestyAI Z-FIRE | Insurance-grade parcel model | No public interface, no RAG, no live feed, expensive |
| Zonehaven/Know Your Zone | Evacuation zones, trusted by OEM | No risk scoring, no RAG, no history |
| WFAS | NFDRS/ERC, fire weather | No spatial grid, no RAG, professional-only UX |

**This project's durable differentiators:** Historical + real-time risk in one view, natural language RAG grounded in incident documents, out-of-state smoke classification, open-data and reproducible, H3 spatial consistency, live SSE event feed.

**The gap to close:** Parcel-level scoring (for insurance) and evacuation zone integration (for homeowners).

---

*Report compiled by Scout (Lead Research Agent) — Phase 0*
*Sources consulted: NIFC, Colorado Newsline, Colorado Sun, KSJD, Drought.gov, CB Insights, DataIntelo, GlobeNewsWire, Risk Strategies, DeepSky Climate, EPRI Wildfire Tool Inventory, Wildfire Risk to Communities (USDA FS), First Street Foundation, Zonehaven/FireTech Connect, Insurance Business Magazine*
