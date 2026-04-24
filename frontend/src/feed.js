import { API_BASE } from './config.js';

const EVENT_ICON = {
  'fire-detection': '🔥',
  'air-quality':    '😷',
  'smoke-alert':    '🌫️',
  'red-flag':       '🚩',
};

export function initFeed() {
  const list   = document.getElementById('feed-cards');
  const status = document.getElementById('feed-status');
  if (!list) return;

  function setStatus(text, live = false) {
    if (!status) return;
    status.textContent = text;
    status.classList.toggle('feed-live', live);
  }

  const es = new EventSource(`${API_BASE}/api/feed`);

  es.onopen = () => setStatus('Live', true);

  es.onmessage = (e) => {
    const item = JSON.parse(e.data);
    const placeholder = list.querySelector('.feed-placeholder');
    if (placeholder) placeholder.remove();
    const card = buildCard(item);
    card.classList.add('feed-card-new');
    setTimeout(() => card.classList.remove('feed-card-new'), 700);
    list.prepend(card);
    while (list.children.length > 50) list.lastElementChild?.remove();

    const lastUpdated = document.getElementById('feed-last-updated');
    if (lastUpdated) {
      const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
      lastUpdated.textContent = `Updated ${time}`;
    }
  };

  es.onerror = () => setStatus('Reconnecting…');
}

function buildCard(item) {
  const li = document.createElement('li');
  li.className = 'feed-card';
  li.dataset.severity = item.severity;

  const icon   = EVENT_ICON[item.eventType] ?? '📡';
  const time   = new Date(item.detectedAt)
    .toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

  li.innerHTML =
    `<span class="feed-icon">${icon}</span>` +
    `<div class="feed-body">` +
    `<div class="feed-title">${escHtml(item.title)}</div>` +
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
