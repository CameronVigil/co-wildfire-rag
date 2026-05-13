import './styles/main.css';
import { initMap, getMap } from './map.js';
import { initSidebar, bindSendButton, openWithRegion, showLoading, showError, renderAnswer } from './sidebar.js';
import { postQuery } from './api.js';
import { initFeed, setInfoCallback } from './feed.js';
import { initInfoPanel, setInfoText, resetInfoText } from './info.js';
import { filterByRiskCategory } from './layers/riskGrid.js';

// ── Info panel ────────────────────────────────────────────────────────────────

initInfoPanel();

// Feed card hover → info panel (injected to avoid feed.js → info.js coupling)
setInfoCallback(
  (type) => setInfoText(FEED_TYPE_HTML[type] ?? `<span style="color:var(--text);font-weight:600;">${escHtml(type)}</span>`),
  resetInfoText,
);

// ── Sidebar ───────────────────────────────────────────────────────────────────

initSidebar();

// Patch sidebar close button so info panel resets when sidebar is dismissed
const closeBtn = document.getElementById('sidebar-close-btn');
closeBtn?.addEventListener('click', resetInfoText);

// ── Feed ──────────────────────────────────────────────────────────────────────

initFeed();

// ── Map ───────────────────────────────────────────────────────────────────────

initMap('map', {
  onRegionClick: (props) => {
    openWithRegion(props);
    if (props) {
      setInfoText(buildRegionSelectedHtml(props));
    } else {
      resetInfoText();
    }
  },
  onRegionHover: (props) => {
    if (props) setInfoText(buildRegionHoverHtml(props));
    else resetInfoText();
  },
});

// ── Legend risk filter ────────────────────────────────────────────────────────

let _activeRiskCategory = null;

document.querySelectorAll('.legend-row[data-risk]').forEach(row => {
  row.addEventListener('click', () => {
    const category = row.dataset.risk;
    const map = getMap();
    if (!map) return;

    if (_activeRiskCategory === category) {
      _activeRiskCategory = null;
      filterByRiskCategory(map, null);
      row.classList.remove('active');
    } else {
      document.querySelectorAll('.legend-row[data-risk].active').forEach(r => r.classList.remove('active'));
      _activeRiskCategory = category;
      filterByRiskCategory(map, category);
      row.classList.add('active');
    }
  });
});

// ── RAG send button ───────────────────────────────────────────────────────────

bindSendButton(async (question, h3Index, location) => {
  showLoading(true);
  try {
    const data = await postQuery(question, h3Index, location);
    renderAnswer(data);
  } catch (err) {
    showError(err.message ?? 'Failed to get a response. Is the API running?');
  }
});

// ── Private helpers ───────────────────────────────────────────────────────────

function buildRegionHoverHtml(props) {
  const label = escHtml(props.riskCategory ?? 'Unknown');
  const score = props.riskScore != null ? Number(props.riskScore).toFixed(1) : '—';
  const county = props.county ? `<br>${escHtml(props.county)}` : '';
  return (
    `<span style="color:var(--text);font-weight:600;">${label}</span>` +
    ` — score ${score}/10` +
    county +
    `<br><span style="color:var(--text-muted);">Click to open full region analysis.</span>`
  );
}

function buildRegionSelectedHtml(props) {
  const h3 = escHtml(props.h3Index ?? '—');
  const label = escHtml(props.riskCategory ?? 'Unknown');
  const score = props.riskScore != null ? Number(props.riskScore).toFixed(1) : '—';
  const fires = props.firesLast20yr != null ? Number(props.firesLast20yr).toLocaleString('en-US', { maximumFractionDigits: 0 }) : '—';
  const acres = fmtAcres(props.totalAcresBurned);
  return (
    `<span style="color:var(--text);font-weight:600;">Region selected: ${h3}</span><br>` +
    `${label} (${score}/10)<br>` +
    `${fires} fires in 20 yr · ${acres} acres burned<br>` +
    `<span style="color:var(--text-muted);">Use the sidebar panel to ask AI questions about this region.</span>`
  );
}

function fmtAcres(val) {
  if (val == null || val === 0) return '0';
  const n = Number(val);
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return n.toFixed(0);
}

function escHtml(str) {
  if (!str) return '';
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

// ── Feed card info copy ───────────────────────────────────────────────────────

const FEED_TYPE_HTML = {
  'fire-detection':
    `<span style="color:var(--text);font-weight:600;">Fire Detection</span> — ` +
    `Active fire pixel from NASA FIRMS satellite data. In-state detections contribute to the region's risk score.`,

  'air-quality':
    `<span style="color:var(--text);font-weight:600;">Air Quality</span> — ` +
    `EPA AirNow AQI reading for an H3 region. Displayed in the sidebar; does not affect the risk score directly.`,

  'smoke-alert':
    `<span style="color:var(--text);font-weight:600;">Smoke Alert</span> — ` +
    `NOAA HMS smoke plume detected over Colorado.`,

  'red-flag':
    `<span style="color:var(--text);font-weight:600;">Red Flag Warning</span> — ` +
    `NOAA has issued a Red Flag Warning indicating critical fire weather: high winds, low humidity, and dry fuels.`,

  'data_fetch':
    `<span style="color:var(--text);font-weight:600;">Data Fetch</span> — ` +
    `The backend retrieved fresh data from an external source (NOAA, FIRMS, AirNow, etc.).`,

  'risk_score':
    `<span style="color:var(--text);font-weight:600;">Risk Score Update</span> — ` +
    `A region's score changed by 1+ point or crossed a category boundary. Scores are recomputed hourly.`,

  'rag_query':
    `<span style="color:var(--text);font-weight:600;">AI Query</span> — ` +
    `A RAG (Retrieval-Augmented Generation) query completed. The AI retrieved relevant InciWeb incident documents and generated a grounded response.`,

  'report_ingested':
    `<span style="color:var(--text);font-weight:600;">Report Ingested</span> — ` +
    `A new InciWeb wildfire incident report was chunked, embedded, and indexed into the Qdrant vector store for future AI queries.`,

  'alert':
    `<span style="color:var(--text);font-weight:600;">Critical Alert</span> — ` +
    `A high-priority warning (Red Flag Warning, fire restriction, or evacuation notice) was issued.`,

  'out_of_state_fire':
    `<span style="color:var(--text);font-weight:600;">Out-of-State Fire</span> — ` +
    `Active fire detected outside Colorado that may impact the state. Classified separately — does not affect Colorado risk scores.`,

  'out_of_state_smoke':
    `<span style="color:var(--text);font-weight:600;">Out-of-State Smoke</span> — ` +
    `Smoke plume originating outside Colorado detected over the state. Air quality is surfaced in the sidebar. Risk scores are unaffected.`,
};
