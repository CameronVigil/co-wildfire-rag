import { API_BASE } from './config.js';

class ApiError extends Error {
  constructor(status, message, code) {
    super(message);
    this.status = status;
    this.code   = code;
  }
}

async function apiFetch(path, options = {}) {
  const res = await fetch(API_BASE + path, options);
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new ApiError(res.status, body.error ?? res.statusText, body.code);
  }
  return res.json();
}

/**
 * GET /api/risk-grid?resolution=6
 * Returns GeoJSON FeatureCollection of H3 cells with risk scores.
 */
export async function fetchRiskGrid(resolution = 6) {
  return apiFetch(`/api/risk-grid?resolution=${resolution}`);
}

/**
 * GET /api/cell-at-point?lat=&lon=&resolution=6
 * Returns properties for the H3 cell containing this point.
 */
export async function fetchCellAtPoint(lat, lon, resolution = 6) {
  return apiFetch(`/api/cell-at-point?lat=${lat}&lon=${lon}&resolution=${resolution}`);
}

/**
 * GET /api/risk-history/{h3Index}?hours=168
 * Returns time-series risk score data for a cell.
 */
export async function fetchRiskHistory(h3Index, hours = 168) {
  return apiFetch(`/api/risk-history/${encodeURIComponent(h3Index)}?hours=${hours}`);
}

/**
 * POST /api/query
 * Runs the RAG pipeline and returns answer + sources + cell context.
 */
export async function postQuery(question, h3Index = null, location = null, resolution = 6) {
  const body = { question, resolution };
  if (h3Index)  body.h3Index  = h3Index;
  if (location) body.location = location;

  return apiFetch('/api/query', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * POST /api/query/ingest
 * Triggers an InciWeb ingestion run (background, idempotent).
 */
export async function triggerIngest() {
  return apiFetch('/api/query/ingest', { method: 'POST' });
}
