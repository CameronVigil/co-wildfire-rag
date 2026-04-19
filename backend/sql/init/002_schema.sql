-- backend/sql/init/002_schema.sql

-- ============================================================
-- fire_events
-- One row per MTBS/NIFC/USFS fire incident.
-- ============================================================
CREATE TABLE IF NOT EXISTS fire_events (
    id              UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    fire_id         VARCHAR(50)     NOT NULL UNIQUE,
    fire_name       VARCHAR(255)    NOT NULL,
    year            SMALLINT        NOT NULL,
    start_date      DATE,
    end_date        DATE,
    acres_burned    NUMERIC(12, 2),
    avg_dnbr        NUMERIC(8, 4),
    max_dnbr        NUMERIC(8, 4),
    source          VARCHAR(50)     NOT NULL,           -- 'MTBS', 'NIFC', 'USFS'
    state           CHAR(2)         NOT NULL DEFAULT 'CO',
    county          VARCHAR(100),
    perimeter       GEOMETRY(MultiPolygon, 4326),
    centroid        GEOMETRY(Point, 4326)
        GENERATED ALWAYS AS (ST_Centroid(perimeter)) STORED,
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_fire_events_year        ON fire_events(year);
CREATE INDEX IF NOT EXISTS idx_fire_events_perimeter   ON fire_events USING GIST(perimeter);
CREATE INDEX IF NOT EXISTS idx_fire_events_centroid    ON fire_events USING GIST(centroid);
CREATE INDEX IF NOT EXISTS idx_fire_events_source      ON fire_events(source);

-- ============================================================
-- h3_cells
-- One row per H3 cell at resolution 6 or 8 covering Colorado.
-- ============================================================
CREATE TABLE IF NOT EXISTS h3_cells (
    id                          BIGSERIAL       PRIMARY KEY,
    h3_index                    VARCHAR(20)     NOT NULL UNIQUE,
    resolution                  SMALLINT        NOT NULL,          -- 6 or 8
    center_lat                  NUMERIC(10, 7)  NOT NULL,
    center_lon                  NUMERIC(10, 7)  NOT NULL,
    boundary                    GEOMETRY(Polygon, 4326),

    -- Fire history (computed during ingestion)
    fires_last_20yr             SMALLINT        NOT NULL DEFAULT 0,
    total_acres_burned          NUMERIC(14, 2)  NOT NULL DEFAULT 0,
    avg_burn_severity           NUMERIC(6, 4),
    years_since_last_fire       SMALLINT,
    last_fire_year              SMALLINT,

    -- Terrain / vegetation (Phase 5, from LANDFIRE)
    vegetation_type             VARCHAR(100),
    slope_degrees               NUMERIC(5, 2),
    aspect_degrees              NUMERIC(6, 2),

    -- Bark beetle kill (Phase 5, from USFS ADS)
    beetle_kill_severity        NUMERIC(4, 3),                         -- 0.000–1.000 normalized
    beetle_kill_phase           VARCHAR(10),                           -- 'red', 'gray', 'down', null

    -- Smoke status
    smoke_present               BOOLEAN         NOT NULL DEFAULT FALSE,
    smoke_inferred              BOOLEAN         NOT NULL DEFAULT FALSE,

    -- RAWS nearest station (Phase 2)
    raws_station_id             VARCHAR(10),
    raws_distance_km            NUMERIC(5, 1),
    raws_wind_speed_mph         NUMERIC(5, 1),
    raws_relative_humidity_pct  NUMERIC(5, 1),

    -- Live risk score (refreshed hourly)
    current_risk_score          NUMERIC(4, 2),
    risk_score_updated_at       TIMESTAMPTZ,

    -- Weather snapshot (RAWS-first, NOAA fallback)
    wind_speed_mph              NUMERIC(5, 1),
    relative_humidity_pct       NUMERIC(5, 1),
    fuel_moisture_pct           NUMERIC(5, 1),
    drought_index               NUMERIC(5, 2),
    days_since_rain             SMALLINT,
    red_flag_warning            BOOLEAN         NOT NULL DEFAULT FALSE,
    weather_source              VARCHAR(10)     NOT NULL DEFAULT 'NOAA', -- 'RAWS' or 'NOAA'

    created_at                  TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_h3_cells_resolution     ON h3_cells(resolution);
CREATE INDEX IF NOT EXISTS idx_h3_cells_boundary       ON h3_cells USING GIST(boundary);
CREATE INDEX IF NOT EXISTS idx_h3_cells_risk_score     ON h3_cells(current_risk_score DESC);
CREATE INDEX IF NOT EXISTS idx_h3_cells_smoke          ON h3_cells(smoke_present) WHERE smoke_present = TRUE;
CREATE INDEX IF NOT EXISTS idx_h3_cells_raws_station   ON h3_cells(raws_station_id);

-- ============================================================
-- h3_risk_history
-- Hourly risk score snapshots. Enables time-series charts and
-- enterprise trend analysis.
-- Retention: hourly for 90 days; daily aggregates indefinitely.
-- ============================================================
CREATE TABLE IF NOT EXISTS h3_risk_history (
    id                    BIGSERIAL     PRIMARY KEY,
    h3_index              VARCHAR(20)   NOT NULL,
    resolution            SMALLINT      NOT NULL,
    risk_score            NUMERIC(4,2)  NOT NULL,
    risk_category         VARCHAR(20)   NOT NULL,
    wind_speed_mph        NUMERIC(5,1),
    relative_humidity_pct NUMERIC(5,1),
    fuel_moisture_pct     NUMERIC(5,1),
    drought_index         NUMERIC(5,2),
    weather_source        VARCHAR(10),
    scored_at             TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_risk_history_h3_scored ON h3_risk_history(h3_index, scored_at DESC);
CREATE INDEX IF NOT EXISTS idx_risk_history_scored_at ON h3_risk_history(scored_at DESC);

-- ============================================================
-- fire_event_h3_intersections
-- Many-to-many: which H3 cells overlap which fire perimeters.
-- ============================================================
CREATE TABLE IF NOT EXISTS fire_event_h3_intersections (
    fire_event_id   UUID        NOT NULL REFERENCES fire_events(id) ON DELETE CASCADE,
    h3_cell_id      BIGINT      NOT NULL REFERENCES h3_cells(id)    ON DELETE CASCADE,
    overlap_pct     NUMERIC(5, 2),
    PRIMARY KEY (fire_event_id, h3_cell_id)
);

CREATE INDEX IF NOT EXISTS idx_feh3_h3_cell_id    ON fire_event_h3_intersections(h3_cell_id);
CREATE INDEX IF NOT EXISTS idx_feh3_fire_event_id ON fire_event_h3_intersections(fire_event_id);

-- ============================================================
-- active_fire_detections
-- NASA FIRMS live cache — refreshed every 15 minutes (Phase 5).
-- ============================================================
CREATE TABLE IF NOT EXISTS active_fire_detections (
    id              BIGSERIAL       PRIMARY KEY,
    latitude        NUMERIC(10, 7)  NOT NULL,
    longitude       NUMERIC(10, 7)  NOT NULL,
    location        GEOMETRY(Point, 4326)
        GENERATED ALWAYS AS (ST_SetSRID(ST_MakePoint(longitude, latitude), 4326)) STORED,
    brightness      NUMERIC(7, 2),
    frp             NUMERIC(8, 2),
    confidence      VARCHAR(10),                -- 'low', 'nominal', 'high'
    satellite       VARCHAR(20),
    acquired_at     TIMESTAMPTZ     NOT NULL,
    day_night       CHAR(1),                    -- 'D' or 'N'

    -- Origin classification (Phase 5)
    is_colorado     BOOLEAN         NOT NULL DEFAULT TRUE,
    origin_state    CHAR(2),
    impact_type     VARCHAR(20),                -- 'fire', 'smoke_only', 'none'

    inserted_at     TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_active_fires_location    ON active_fire_detections USING GIST(location);
CREATE INDEX IF NOT EXISTS idx_active_fires_acquired_at ON active_fire_detections(acquired_at DESC);
CREATE INDEX IF NOT EXISTS idx_active_fires_is_colorado ON active_fire_detections(is_colorado);

-- ============================================================
-- smoke_events
-- NOAA HMS smoke plume polygons intersecting Colorado. (Phase 5)
-- ============================================================
CREATE TABLE IF NOT EXISTS smoke_events (
    id                          BIGSERIAL       PRIMARY KEY,
    plume_date                  DATE            NOT NULL,
    density                     VARCHAR(10)     NOT NULL,  -- 'coarse', 'medium', 'heavy'
    plume                       GEOMETRY(MultiPolygon, 4326) NOT NULL,
    origin_state                CHAR(2),
    origin_state_name           VARCHAR(50),
    is_colorado_origin          BOOLEAN         NOT NULL DEFAULT FALSE,
    colorado_counties_affected  TEXT[],
    smoke_description           TEXT,
    source                      VARCHAR(20)     NOT NULL DEFAULT 'NOAA_HMS',
    fetched_at                  TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_smoke_events_date   ON smoke_events(plume_date DESC);
CREATE INDEX IF NOT EXISTS idx_smoke_events_plume  ON smoke_events USING GIST(plume);

-- ============================================================
-- co_counties
-- Colorado county boundary polygons. Seeded once from
-- Census TIGER/Line. Used by HmsService and OriginClassifierService.
-- ============================================================
CREATE TABLE IF NOT EXISTS co_counties (
    id              SERIAL          PRIMARY KEY,
    county_fips     VARCHAR(5)      NOT NULL UNIQUE,  -- e.g. '08069' (Larimer)
    county_name     VARCHAR(100)    NOT NULL,
    state_fips      CHAR(2)         NOT NULL DEFAULT '08',
    boundary        GEOMETRY(MultiPolygon, 4326) NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_co_counties_boundary ON co_counties USING GIST(boundary);

-- ============================================================
-- state_boundaries
-- US state boundary polygons for origin classification.
-- Seeded once from Census TIGER/Line.
-- ============================================================
CREATE TABLE IF NOT EXISTS state_boundaries (
    id              SERIAL          PRIMARY KEY,
    state_fips      CHAR(2)         NOT NULL UNIQUE,
    state_abbr      CHAR(2)         NOT NULL UNIQUE,
    state_name      VARCHAR(50)     NOT NULL,
    boundary        GEOMETRY(MultiPolygon, 4326) NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_state_boundaries_boundary ON state_boundaries USING GIST(boundary);

-- ============================================================
-- aqi_observations
-- AirNow hourly AQI + PM2.5 per H3-6 cell center. (Phase 5)
-- ============================================================
CREATE TABLE IF NOT EXISTS aqi_observations (
    id              BIGSERIAL       PRIMARY KEY,
    h3_index        VARCHAR(20)     NOT NULL,
    observed_at     TIMESTAMPTZ     NOT NULL,
    aqi             SMALLINT,
    pm25            NUMERIC(6, 2),
    category        VARCHAR(50),               -- 'Good', 'Moderate', 'Unhealthy', etc.
    smoke_inferred  BOOLEAN         NOT NULL DEFAULT FALSE,
    UNIQUE (h3_index, observed_at)
);

CREATE INDEX IF NOT EXISTS idx_aqi_h3_index    ON aqi_observations(h3_index);
CREATE INDEX IF NOT EXISTS idx_aqi_observed_at ON aqi_observations(observed_at DESC);

-- ============================================================
-- ingestion_log
-- Tracks which data files/sources have been loaded.
-- Enables idempotent re-runs.
-- ============================================================
CREATE TABLE IF NOT EXISTS ingestion_log (
    id              BIGSERIAL       PRIMARY KEY,
    source          VARCHAR(50)     NOT NULL,
    dataset_key     VARCHAR(255)    NOT NULL,
    records_loaded  INT             NOT NULL DEFAULT 0,
    status          VARCHAR(20)     NOT NULL DEFAULT 'pending',  -- 'pending', 'success', 'failed'
    error_message   TEXT,
    started_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    completed_at    TIMESTAMPTZ,
    UNIQUE (source, dataset_key)
);
