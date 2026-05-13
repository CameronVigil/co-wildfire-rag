import { API_BASE } from './config.js';

// Injected from main.js to keep dependency direction clean
let _onCardEnter = null;
let _onCardLeave = null;

/**
 * Register info-panel callbacks for feed card hover.
 * @param {function} onEnter - called with (type: string) on mouseenter
 * @param {function} onLeave - called on mouseleave
 */
export function setInfoCallback(onEnter, onLeave) {
  _onCardEnter = onEnter;
  _onCardLeave = onLeave;
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
};

// Every named event type the backend emits — onmessage does not fire for named events.
const FEED_EVENT_TYPES = [
  'fire-detection', 'air-quality', 'smoke-alert', 'data_fetch',
  'risk_score', 'rag_query', 'report_ingested', 'alert',
  'out_of_state_fire', 'out_of_state_smoke', 'heartbeat',
];

export function initFeed() {
  const list   = document.getElementById('feed-cards');
  const status = document.getElementById('feed-status');
  if (!list) return;

  function setStatus(text, live = false) {
    if (!status) return;
    status.textContent = text;
    status.classList.toggle('feed-live', live);
  }

  function updateTimestamp() {
    const lastUpdated = document.getElementById('feed-last-updated');
    if (!lastUpdated) return;
    const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    lastUpdated.textContent = `Updated ${time}`;
  }

  function handleEvent(e) {
    const item = JSON.parse(e.data);

    // Heartbeat: update status bar only, no card.
    if (item.type === 'heartbeat') {
      updateTimestamp();
      return;
    }

    const placeholder = list.querySelector('.feed-placeholder');
    if (placeholder) placeholder.remove();

    const card = buildCard(item);
    card.classList.add('feed-card-new');
    setTimeout(() => card.classList.remove('feed-card-new'), 700);
    list.prepend(card);
    while (list.children.length > 50) list.lastElementChild?.remove();

    updateTimestamp();
  }

  const es = new EventSource(`${API_BASE}/api/feed`);
  es.onopen  = () => setStatus('Live', true);
  es.onerror = () => setStatus('Reconnecting…');

  FEED_EVENT_TYPES.forEach(type => es.addEventListener(type, handleEvent));
}

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

  li.innerHTML =
    `<span class="feed-icon">${icon}</span>` +
    `<div class="feed-body">` +
    `<div class="feed-title">${titleHtml}</div>` +
    `<div class="feed-detail">${escHtml(item.detail)}</div>` +
    `<div class="feed-time">${time}</div>` +
    `</div>`;

  return li;
}

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
