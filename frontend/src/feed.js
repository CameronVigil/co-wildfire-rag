import { API_BASE } from './config.js';

// Injected from main.js to keep dependency direction clean
let _onCardEnter = null;
let _onCardLeave = null;
let _mapFlyTo    = null;
let _mapPulse    = null;

export function setInfoCallback(onEnter, onLeave) {
  _onCardEnter = onEnter;
  _onCardLeave = onLeave;
}

/** Wire map actions from main.js: { flyTo(h3Index), pulse(h3Index) } */
export function setMapHandlers({ flyTo, pulse }) {
  _mapFlyTo  = flyTo;
  _mapPulse  = pulse;
}

const EVENT_ICON = {
  'fire-detection':     '🔥',
  'air-quality':        '😷',
  'smoke-alert':        '🌫️',
  'red-flag':           '🚩',
  'data_fetch':         '📡',
  'risk_score':         '📊',
  'rag_query':          '🤖',
  'report_ingested':    '📄',
  'alert':              '🚨',
  'out_of_state_fire':  '🔥',
  'out_of_state_smoke': '🌫️',
  'fire-weather-watch': '⚡',
  'fire-warning':       '⛔',
  'inciweb-incident':   '📋',
  'active-incident':    '🔥',
  'road-closure':       '🚧',
  'news-article':       '📰',
  'evacuation-alert':   '🚨',
  'spot-forecast':      '🛰️',
  'raws-alert':         '📡',
};

const FEED_EVENT_TYPES = [
  'fire-detection', 'air-quality', 'smoke-alert', 'data_fetch',
  'risk_score', 'rag_query', 'report_ingested', 'alert',
  'out_of_state_fire', 'out_of_state_smoke', 'heartbeat',
  'fire-weather-watch', 'fire-warning',
  'inciweb-incident', 'active-incident', 'road-closure',
  'news-article', 'evacuation-alert', 'spot-forecast', 'raws-alert',
];

// ── State ────────────────────────────────────────────────────────────────────

let _activeSeverity  = 'all'; // 'all' | 'warning' | 'critical'
let _soundEnabled    = localStorage.getItem('feed-sound') === 'true';
let _notifRequested  = false;
const _seenNotifIds  = new Set(); // prevent duplicate browser notifications per session

// ── Init ─────────────────────────────────────────────────────────────────────

export function initFeed() {
  const list   = document.getElementById('feed-cards');
  const status = document.getElementById('feed-status');
  if (!list) return;

  initFilterPills();
  initSoundToggle();
  requestNotificationPermission();

  function setStatus(text, live = false) {
    if (!status) return;
    status.textContent = text;
    status.classList.toggle('feed-live', live);
  }

  function updateTimestamp() {
    const el = document.getElementById('feed-last-updated');
    if (!el) return;
    const t = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    el.textContent = `Updated ${t}`;
  }

  function handleEvent(e) {
    const item = JSON.parse(e.data);

    if (item.type === 'heartbeat') { updateTimestamp(); return; }

    const placeholder = list.querySelector('.feed-placeholder');
    if (placeholder) placeholder.remove();

    // Critical events with an h3Index get special handling
    if (item.severity === 'critical' && item.h3Index) {
      showAlertBanner(item);
      if (_mapPulse) _mapPulse(item.h3Index);
      sendBrowserNotification(item);
      playAlertTone();
    }

    const card = buildCard(item);
    card.classList.add('feed-card-new');
    setTimeout(() => card.classList.remove('feed-card-new'), 700);
    list.prepend(card);

    applySeverityFilter(card, _activeSeverity);
    while (list.children.length > 50) list.lastElementChild?.remove();

    updateTimestamp();
  }

  const es = new EventSource(`${API_BASE}/api/feed`);
  es.onopen  = () => setStatus('Live', true);
  es.onerror = () => setStatus('Reconnecting…');

  FEED_EVENT_TYPES.forEach(type => es.addEventListener(type, handleEvent));
}

// ── Filter pills ─────────────────────────────────────────────────────────────

function initFilterPills() {
  document.querySelectorAll('.feed-filter-pill').forEach(pill => {
    pill.addEventListener('click', () => {
      document.querySelectorAll('.feed-filter-pill').forEach(p => p.classList.remove('active'));
      pill.classList.add('active');
      _activeSeverity = pill.dataset.severity;
      applyFilterToAll(_activeSeverity);
    });
  });
}

function applyFilterToAll(severity) {
  document.querySelectorAll('#feed-cards .feed-card').forEach(card => {
    applySeverityFilter(card, severity);
  });
}

function applySeverityFilter(card, severity) {
  if (severity === 'all') {
    card.hidden = false;
    return;
  }
  const cardSev = card.dataset.severity ?? 'info';
  if (severity === 'critical') {
    card.hidden = cardSev !== 'critical';
  } else if (severity === 'warning') {
    card.hidden = cardSev === 'info';
  }
}

// ── Alert banner ─────────────────────────────────────────────────────────────

function showAlertBanner(item) {
  const banner = document.getElementById('alert-banner');
  if (!banner) return;

  banner.hidden = false;

  const div = document.createElement('div');
  div.className = 'alert-banner-item';
  div.dataset.type = item.type;

  const icon  = EVENT_ICON[item.type] ?? '🚨';
  const label = escHtml(item.source ?? item.type);
  const text  = escHtml(item.detail);

  div.innerHTML =
    `<span class="alert-banner-icon">${icon}</span>` +
    `<div class="alert-banner-body">` +
    `<strong>${label}</strong> — ${text}` +
    `</div>` +
    (item.h3Index
      ? `<button class="alert-banner-fly" data-h3="${escAttr(item.h3Index)}">Fly to region</button>`
      : '') +
    `<button class="alert-banner-dismiss" aria-label="Dismiss">✕</button>`;

  div.querySelector('.alert-banner-fly')?.addEventListener('click', () => {
    if (_mapFlyTo) _mapFlyTo(item.h3Index);
  });

  div.querySelector('.alert-banner-dismiss').addEventListener('click', () => {
    div.remove();
    if (!banner.children.length) banner.hidden = true;
  });

  banner.prepend(div);

  // Auto-dismiss after 60 seconds
  setTimeout(() => {
    div.remove();
    if (!banner.children.length) banner.hidden = true;
  }, 60_000);
}

// ── Sound toggle ─────────────────────────────────────────────────────────────

function initSoundToggle() {
  const btn = document.getElementById('feed-sound-btn');
  if (!btn) return;

  updateSoundBtn(btn);

  btn.addEventListener('click', () => {
    _soundEnabled = !_soundEnabled;
    localStorage.setItem('feed-sound', _soundEnabled);
    updateSoundBtn(btn);
  });
}

function updateSoundBtn(btn) {
  btn.textContent  = _soundEnabled ? '🔔' : '🔇';
  btn.title        = _soundEnabled ? 'Sound alerts on — click to mute' : 'Sound alerts off — click to enable';
  btn.setAttribute('aria-pressed', String(_soundEnabled));
}

function playAlertTone() {
  if (!_soundEnabled) return;
  try {
    const ctx  = new AudioContext();
    const osc  = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.frequency.value = 880;
    gain.gain.setValueAtTime(0.25, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.4);
    osc.start(ctx.currentTime);
    osc.stop(ctx.currentTime + 0.4);
  } catch { /* audio not supported */ }
}

// ── Browser notifications ─────────────────────────────────────────────────────

async function requestNotificationPermission() {
  if (!('Notification' in window)) return;
  if (Notification.permission === 'default' && !_notifRequested) {
    _notifRequested = true;
    await Notification.requestPermission().catch(() => {});
  }
}

function sendBrowserNotification(item) {
  if (!('Notification' in window) || Notification.permission !== 'granted') return;

  const dedupKey = `${item.type}-${item.h3Index ?? ''}-${item.detail}`;
  if (_seenNotifIds.has(dedupKey)) return;
  _seenNotifIds.add(dedupKey);

  const title = item.source ? `⚠ ${item.source}` : '⚠ Critical Alert';
  new Notification(title, { body: item.detail, tag: dedupKey });
}

// ── Card builder ──────────────────────────────────────────────────────────────

function buildCard(item) {
  const li = document.createElement('li');
  li.className = 'feed-card';
  li.dataset.severity = item.severity;
  li.dataset.type = item.type;

  li.addEventListener('mouseenter', () => { if (_onCardEnter) _onCardEnter(item.type); });
  li.addEventListener('mouseleave', () => { if (_onCardLeave) _onCardLeave(); });

  const icon  = EVENT_ICON[item.type] ?? '📡';
  const label = item.source ?? item.type;
  const time  = new Date(item.timestamp)
    .toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

  const titleHtml = item.sourceUrl
    ? `<a href="${escAttr(item.sourceUrl)}" target="_blank" rel="noopener noreferrer" class="feed-source-link">${escHtml(label)}</a>`
    : escHtml(label);

  // "Fly to region" link on h3Index-tagged cards
  const flyHtml = (item.h3Index && _mapFlyTo)
    ? `<button class="feed-fly-btn" title="Fly to region">📍</button>`
    : '';

  li.innerHTML =
    `<span class="feed-icon">${icon}</span>` +
    `<div class="feed-body">` +
    `<div class="feed-title">${titleHtml}${flyHtml}</div>` +
    `<div class="feed-detail">${escHtml(item.detail)}</div>` +
    `<div class="feed-time">${time}</div>` +
    `</div>`;

  li.querySelector('.feed-fly-btn')?.addEventListener('click', (e) => {
    e.stopPropagation();
    if (_mapFlyTo) _mapFlyTo(item.h3Index);
  });

  return li;
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function escHtml(str) {
  if (!str) return '';
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function escAttr(str) {
  if (!str) return '';
  return str
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
