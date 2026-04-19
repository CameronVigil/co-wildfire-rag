# Live Feed Panel

> **SDD Note:** This document specifies the SSE backend contract and frontend panel design
> for the live data feed. Backend implements `FeedService` and `FeedController` to match
> these specs. Frontend implements `src/feed.js` to consume and render them.

---

## Overview

The live feed panel sits to the **right of the map** as a fixed-width vertical panel
(~320px). It shows a real-time scrolling list of events as data is fetched, risk scores
change, reports are ingested, and alerts are issued.

The panel does not overlap the map. The RAG sidebar (cell click response) appears below
the map and feed panel in a bottom drawer.

---

## Layout

```
┌──────────────────────────────────────────────────────────┐
│  MAP (fills remaining width)        │  LIVE FEED  ~320px │
│                                     │ ┌────────────────┐ │
│                                     │ │ 🔴 CRITICAL    │ │
│                                     │ │ Red Flag Warn  │ │
│                                     │ │ Larimer Co.    │ │
│                                     │ │ 2 min ago      │ │
│                                     │ └────────────────┘ │
│                                     │ ┌────────────────┐ │
│                                     │ │ 🟣 OUT-OF-STATE│ │
│                                     │ │ NM smoke plume │ │
│                                     │ │ → Pueblo Co.   │ │
│                                     │ │ 4 min ago      │ │
│                                     │ └────────────────┘ │
│                                     │ ┌────────────────┐ │
│                                     │ │ 🔵 DATA FETCH  │ │
│                                     │ │ FIRMS: 14 det. │ │
│                                     │ │ 8 min ago      │ │
│                                     │ └────────────────┘ │
│                                     │                    │
│                                     │ [All][Alert][OOS]  │
│                                     │ [Data][RAG] ⏸ 🗑   │
├─────────────────────────────────────┴────────────────────┤
│  SIDEBAR — RAG response area (bottom drawer)             │
└──────────────────────────────────────────────────────────┘
```

---

## SSE Backend Contract

### Endpoint

`GET /api/feed`
`Content-Type: text/event-stream`
`Cache-Control: no-cache`

The client connects once. The server pushes events as they occur. The server sends a
`heartbeat` every 30 seconds to keep the connection alive.

### Event Envelope (all events)

Every event is a JSON object with these base fields:

| Field | Type | Always Present | Description |
|---|---|---|---|
| `type` | string | Yes | Event type identifier (see table below) |
| `severity` | string | Yes | `"info"`, `"warning"`, or `"critical"` |
| `timestamp` | string | Yes | ISO 8601 UTC |
| `detail` | string | Yes | Human-readable 1–2 sentence description |
| `source` | string | No | Data source (NOAA, FIRMS, InciWeb, etc.) |
| `h3Index` | string | No | H3 cell index if event is location-specific |
| `county` | string | No | Colorado county name if applicable |

### Event Types

| Type | Severity | Required Extra Fields | Description |
|---|---|---|---|
| `data_fetch` | `info` | `source` | NOAA, FIRMS, or WFAS data retrieved |
| `risk_score` | `info`/`warning`/`critical` | `h3Index`, `county` | Cell risk score changed by ≥ 1.0 or crossed category |
| `rag_query` | `info` | `h3Index` | RAG query completed for a cell |
| `report_ingested` | `info` | `source` | New InciWeb/NIFC document embedded into Qdrant |
| `alert` | `warning`/`critical` | `source` | Red Flag Warning or fire restriction issued |
| `out_of_state_fire` | `info`/`warning` | `originState`, `originStateName` | Out-of-state fire detected near CO border |
| `out_of_state_smoke` | `info`/`warning` | `originState`, `originStateName`, `impactedCounties` | Smoke transport from another state |
| `heartbeat` | — | — | Keep-alive; no display needed |

### Full Event Examples

```
event: data_fetch
data: {"type":"data_fetch","source":"NASA FIRMS","detail":"Fetched 14 active fire detections for Colorado and border region","timestamp":"2026-04-03T14:20:00Z","severity":"info"}

event: risk_score
data: {"type":"risk_score","h3Index":"8629a0807ffffff","county":"Larimer","detail":"Risk score updated: 7.4 → 8.1 (High → Very High)","timestamp":"2026-04-03T14:00:12Z","severity":"warning"}

event: rag_query
data: {"type":"rag_query","h3Index":"8629a0807ffffff","detail":"RAG query completed — 5 chunks retrieved, 1842ms","timestamp":"2026-04-03T14:01:23Z","severity":"info"}

event: report_ingested
data: {"type":"report_ingested","source":"InciWeb","detail":"New report ingested: Cameron Peak Update #47 — 12,400 tokens, 25 chunks","timestamp":"2026-04-03T14:05:00Z","severity":"info"}

event: alert
data: {"type":"alert","source":"NOAA","detail":"Red Flag Warning issued for Larimer and Boulder counties until 8 PM MDT","timestamp":"2026-04-03T14:10:00Z","severity":"critical"}

event: out_of_state_fire
data: {"type":"out_of_state_fire","originState":"NM","originStateName":"New Mexico","detail":"High-confidence fire detection 45 miles south of CO border near Taos, NM","timestamp":"2026-04-03T14:12:00Z","severity":"warning"}

event: out_of_state_smoke
data: {"type":"out_of_state_smoke","originState":"NM","originStateName":"New Mexico","detail":"Heavy smoke plume from New Mexico detected over southern Colorado","timestamp":"2026-04-03T14:15:00Z","severity":"warning","impactedCounties":["Las Animas","Huerfano","Pueblo"]}

event: heartbeat
data: {"type":"heartbeat","timestamp":"2026-04-03T14:20:30Z"}
```

---

## Backend Implementation

### `FeedService.cs`

A singleton service that acts as a publish/subscribe channel. All other services that
produce observable events call `FeedService.PublishAsync()`.

```
FeedService (singleton)
    ├── Channel<LiveFeedEvent>  ← thread-safe, unbounded
    ├── PublishAsync(event)     ← called by all producing services
    └── ReadAllAsync()          ← consumed by FeedController SSE loop
```

**Services that publish to FeedService:**

| Service | Events Published |
|---|---|
| `FirmsService` | `data_fetch`, `out_of_state_fire` |
| `NoaaService` | `data_fetch`, `alert` (Red Flag Warnings) |
| `RiskScoringService` | `risk_score` (on significant change) |
| `RagService` | `rag_query` |
| `InciwebIngester` | `report_ingested` |
| `HmsService` | `out_of_state_smoke` |
| `AirNowService` | `data_fetch` |

### `FeedController.cs`

```
GET /api/feed
    │
    ├── Set headers: Content-Type: text/event-stream, Cache-Control: no-cache
    ├── Start heartbeat timer (every 30s)
    └── Loop: await FeedService.ReadAllAsync()
            │
            └── Write: "event: {type}\ndata: {json}\n\n"
                Flush after each event
```

**Server-side timeout:** Close the SSE connection after 5 minutes of client inactivity
(no reads). The frontend `EventSource` will automatically reconnect.

---

## Frontend Implementation

### `src/feed.js`

```
EventSource('/api/feed')
    │
    ├── addEventListener('data_fetch', ...)       → blue info card
    ├── addEventListener('risk_score', ...)        → yellow/red card by new score
    ├── addEventListener('alert', ...)             → red critical card, pinned to top
    ├── addEventListener('report_ingested', ...)   → blue info card
    ├── addEventListener('rag_query', ...)         → blue info card
    ├── addEventListener('out_of_state_fire', ...) → purple card, "OUT-OF-STATE" badge
    ├── addEventListener('out_of_state_smoke', ...)→ purple card, county list
    └── addEventListener('heartbeat', ...)         → update status bar timestamp only
```

### Feed Card Design

Each card contains:
- **Color bar** on left edge: red (critical) / orange (warning) / blue (info) / purple (out-of-state)
- **Icon**: 🔴 critical · 🟠 warning · 🔵 info · 🟣 out-of-state
- **Source badge**: `NOAA` / `FIRMS` / `InciWeb` / `RAG` / `AirNow` / `HMS`
- **Detail text**: 1–2 lines
- **County badge**: shown when `county` is present
- **OUT-OF-STATE badge**: purple pill shown for `out_of_state_*` events
- **Timestamp**: relative ("2 min ago"), title attribute shows absolute ISO timestamp
- **Clickable**: if `h3Index` is present → fly map to cell + open sidebar

### Feed Controls

```
Filter buttons: [All] [Alerts] [Data] [Out-of-State] [RAG]
Action buttons: [⏸ Pause] [🗑 Clear]
Status bar:     "● Live — Last update: 23s ago"
```

- **Filter:** Hides cards not matching the selected type. Does not disconnect SSE.
- **Pause:** Stops auto-scroll and stops prepending new cards (they queue internally). Resume flushes queue.
- **Clear:** Empties the visible card list. Does not stop the SSE connection.
- **Max cards:** 50 visible. Oldest pruned automatically from the DOM (not from the SSE stream).

### Reconnection

The browser `EventSource` API reconnects automatically on connection drop with exponential
backoff. No custom reconnection logic is needed. On reconnect, the feed resumes from the
current server state (no message replay — feeds are ephemeral by design).
