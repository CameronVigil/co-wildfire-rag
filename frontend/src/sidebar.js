import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { RISK_COLORS, RISK_COLOR_UNKNOWN } from './config.js';

// Configure marked for safe rendering
marked.setOptions({ breaks: true, gfm: true });

// DOM refs — populated in init()
let els = {};
let _currentH3 = null;
let _currentLat = null;
let _currentLon = null;

export function initSidebar() {
  els = {
    sidebar:       document.getElementById('sidebar'),
    closeBtn:      document.getElementById('sidebar-close-btn'),
    h3Index:       document.getElementById('cell-h3index'),
    riskBadge:     document.getElementById('cell-risk-badge'),
    riskScore:     document.getElementById('cell-risk-score'),
    firesCount:    document.getElementById('cell-fires-count'),
    acresBurned:   document.getElementById('cell-acres-burned'),
    lastFireYear:  document.getElementById('cell-last-fire-year'),
    windSpeed:     document.getElementById('cond-wind'),
    humidity:      document.getElementById('cond-humidity'),
    fuelMoisture:  document.getElementById('cond-fuel'),
    redFlag:       document.getElementById('cond-red-flag'),
    questionInput: document.getElementById('question-input'),
    sendBtn:       document.getElementById('send-btn'),
    ragAnswer:     document.getElementById('rag-answer'),
    sourcesList:   document.getElementById('sources-list'),
    processingMs:  document.getElementById('processing-ms'),
    loading:       document.getElementById('rag-loading'),
    error:         document.getElementById('rag-error'),
    condSection:   document.getElementById('conditions-section'),
    statsSection:  document.getElementById('stats-section'),
  };

  els.closeBtn?.addEventListener('click', close);
}

export function bindSendButton(onSubmit) {
  const handler = () => {
    const q = els.questionInput?.value?.trim();
    if (!q) return;
    onSubmit(q, _currentH3, _currentLat != null ? { lat: _currentLat, lon: _currentLon } : null);
  };

  els.sendBtn?.addEventListener('click', handler);
  els.questionInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handler();
    }
  });
}

/**
 * Open the sidebar and populate it with cell properties from the GeoJSON feature.
 * Pass null to close the sidebar.
 */
export function openWithCell(props) {
  if (!props) { close(); return; }

  _currentH3  = props.h3Index;
  _currentLat = props.centerLat;
  _currentLon = props.centerLon;

  // Cell stats
  setText(els.h3Index, props.h3Index ?? '—');
  setRiskBadge(props.riskCategory, props.riskScore);
  setText(els.firesCount,   fmtNum(props.firesLast20yr, 0));
  setText(els.acresBurned,  fmtAcres(props.totalAcresBurned));
  setText(els.lastFireYear, props.lastFireYear ?? 'N/A');

  // Conditions
  setText(els.windSpeed,    props.windSpeedMph    != null ? `${fmtNum(props.windSpeedMph, 0)} mph`    : '—');
  setText(els.humidity,     props.relativeHumidityPct != null ? `${fmtNum(props.relativeHumidityPct, 0)}%` : '—');
  setText(els.fuelMoisture, props.fuelMoisturePct != null ? `${fmtNum(props.fuelMoisturePct, 0)}%`    : '—');
  setRedFlag(props.redFlagWarning);

  // Pre-fill question
  if (els.questionInput) {
    els.questionInput.value =
      `What is the current wildfire risk and what conditions are most dangerous for cell ${props.h3Index}?`;
  }

  // Clear previous RAG result
  clearRagResult();

  // Open
  els.sidebar?.classList.add('open');
  els.questionInput?.focus();
}

export function close() {
  els.sidebar?.classList.remove('open');
  _currentH3 = null;
}

export function showLoading(on) {
  if (els.loading)  els.loading.hidden  = !on;
  if (els.sendBtn)  els.sendBtn.disabled = on;
  if (els.error)    els.error.hidden    = true;
}

export function showError(message) {
  if (els.error) {
    els.error.textContent = message;
    els.error.hidden      = false;
  }
  if (els.loading) els.loading.hidden = true;
  if (els.sendBtn) els.sendBtn.disabled = false;
}

export function renderAnswer(data) {
  if (els.loading) els.loading.hidden = true;
  if (els.sendBtn) els.sendBtn.disabled = false;
  if (els.error)   els.error.hidden = true;

  // Answer text (markdown → sanitized HTML)
  if (els.ragAnswer) {
    els.ragAnswer.innerHTML = DOMPurify.sanitize(marked.parse(data.answer ?? ''));
  }

  // Sources
  if (els.sourcesList) {
    els.sourcesList.innerHTML = '';
    (data.sources ?? []).forEach((src, i) => {
      const li = document.createElement('li');
      li.innerHTML = `
        <span class="source-num">[${i + 1}]</span>
        <a href="${escHtml(src.sourceUrl)}" target="_blank" rel="noopener noreferrer">
          ${escHtml(src.documentTitle)}
        </a>
        <span class="source-sim">${(src.similarity * 100).toFixed(0)}% match</span>
        <p class="source-excerpt">${escHtml(src.excerpt)}</p>`;
      els.sourcesList.appendChild(li);
    });
  }

  // Processing info
  if (els.processingMs) {
    const secs = (data.processingMs / 1000).toFixed(1);
    els.processingMs.textContent = `${secs}s · ${data.modelUsed} · ${data.chunksRetrieved} chunks`;
  }

  // Update conditions from RAG response (more detailed than GeoJSON properties)
  const cond = data.currentConditions;
  if (cond) {
    setText(els.windSpeed,    cond.windSpeedMph    != null ? `${fmtNum(cond.windSpeedMph, 0)} mph`    : els.windSpeed?.textContent);
    setText(els.humidity,     cond.relativeHumidityPct != null ? `${fmtNum(cond.relativeHumidityPct, 0)}%` : els.humidity?.textContent);
    setText(els.fuelMoisture, cond.fuelMoisturePct != null ? `${fmtNum(cond.fuelMoisturePct, 0)}%`    : els.fuelMoisture?.textContent);
    setRedFlag(cond.redFlagWarning);
  }

  // Update stats from RAG response
  const stats = data.cellStats;
  if (stats) {
    setRiskBadge(stats.riskCategory, stats.riskScore);
    setText(els.firesCount,  fmtNum(stats.firesLast20yr, 0));
    setText(els.acresBurned, fmtAcres(stats.totalAcresBurned));
  }
}

// ── Private helpers ──────────────────────────────────────────────────────────

function clearRagResult() {
  if (els.ragAnswer)   els.ragAnswer.innerHTML = '<p class="placeholder">Ask a question about this cell to get an AI-powered risk analysis.</p>';
  if (els.sourcesList) els.sourcesList.innerHTML = '';
  if (els.processingMs) els.processingMs.textContent = '';
  if (els.error)       els.error.hidden = true;
  if (els.loading)     els.loading.hidden = true;
  if (els.sendBtn)     els.sendBtn.disabled = false;
}

function setRiskBadge(category, score) {
  if (!els.riskBadge) return;
  const color = RISK_COLORS[category] ?? RISK_COLOR_UNKNOWN;
  els.riskBadge.textContent = category ?? 'Unknown';
  els.riskBadge.style.background = color;
  els.riskBadge.style.color = isLightColor(color) ? '#000' : '#fff';

  if (els.riskScore) {
    els.riskScore.textContent = score != null ? `${Number(score).toFixed(1)}/10` : '—';
  }
}

function setRedFlag(active) {
  if (!els.redFlag) return;
  els.redFlag.textContent = active ? '🔴 RED FLAG WARNING' : 'None';
  els.redFlag.className   = 'cond-value' + (active ? ' red-flag-active' : '');
}

function setText(el, val) {
  if (el) el.textContent = val ?? '—';
}

function fmtNum(val, decimals = 1) {
  if (val == null) return '—';
  return Number(val).toLocaleString('en-US', { maximumFractionDigits: decimals });
}

function fmtAcres(val) {
  if (val == null || val === 0) return '0';
  const n = Number(val);
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return n.toFixed(0);
}

function escHtml(str) {
  if (!str) return '';
  return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function isLightColor(hex) {
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return (r * 299 + g * 587 + b * 114) / 1000 > 128;
}
