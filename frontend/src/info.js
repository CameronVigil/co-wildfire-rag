/**
 * info.js — Context-sensitive info panel (Ableton Info View style).
 *
 * Exports:
 *   initInfoPanel()  — call once from main.js; sets default text and wires legend hover.
 *   setInfoText(html) — update #info-text innerHTML.
 *   resetInfoText()   — restore default content.
 */

// ── Static copy ──────────────────────────────────────────────────────────────

const DEFAULT_HTML =
  `<span style="color:var(--text);font-weight:600;">About</span><br>` +
  `Colorado Wildfire Risk Analyzer combines 20 years of MTBS burn data with live weather, ` +
  `fuel moisture, and terrain to compute an hourly risk score (0–10) per H3 hex region across Colorado.` +
  `<br><br>` +
  `<span style="color:var(--text);font-weight:600;">Risk Scores</span><br>` +
  `Scores are driven by: wind speed (22%), low humidity (18%), dry fuel moisture (18%), ` +
  `fire history (12%), slope (9%), vegetation flammability (9%), drought index (8%), ` +
  `days since rain (4%). Updated hourly. Out-of-state fires never affect the score.` +
  `<br><br>` +
  `<span style="color:var(--text);font-weight:600;">Feed Alerts</span><br>` +
  `🔥 Fire detected · 😷 Air quality · 🌫️ Smoke · 🚩 Red Flag · ⚡ Fire Weather Watch · ` +
  `⛔ Fire Warning · 📋 InciWeb incident · 🔥 Active incident · 🚧 Road closure · ` +
  `📊 Score change · 🤖 AI query · 📄 Report ingested · 🚨 Critical alert`;

const LEGEND_HTML =
  `Risk scores run 0–10. Colors represent: ` +
  `<span style="color:var(--risk-very-low);font-weight:600;">Very Low</span> (0–2), ` +
  `<span style="color:var(--risk-low);font-weight:600;">Low</span> (2–4), ` +
  `<span style="color:var(--text);font-weight:600;">Moderate</span> (4–6), ` +
  `<span style="color:var(--risk-high);font-weight:600;">High</span> (6–8), ` +
  `<span style="color:var(--risk-very-high);font-weight:600;">Very High</span> (8–9), ` +
  `<span style="color:#b44;font-weight:600;">Extreme</span> (9–10). ` +
  `Scores update hourly.`;

// ── DOM ref ──────────────────────────────────────────────────────────────────

let _infoText = null;

// ── Public API ───────────────────────────────────────────────────────────────

export function initInfoPanel() {
  _infoText = document.getElementById('info-text');
  resetInfoText();

  // Legend hover
  const legend = document.getElementById('legend');
  legend?.addEventListener('mouseenter', () => setInfoText(LEGEND_HTML));
  legend?.addEventListener('mouseleave', resetInfoText);
}

export function setInfoText(html) {
  if (_infoText) _infoText.innerHTML = html;
}

export function resetInfoText() {
  if (_infoText) _infoText.innerHTML = DEFAULT_HTML;
}
