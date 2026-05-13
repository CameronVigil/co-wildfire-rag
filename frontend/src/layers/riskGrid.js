import { riskColorExpression } from '../config.js';

const SOURCE_ID     = 'risk-grid';
const FILL_ID       = 'risk-fill';
const LINE_ID       = 'risk-outline';
const PULSE_ID      = 'risk-extreme-pulse';
const HIGH_RING_ID  = 'risk-high-ring';

// Zoom-interpolated base opacity: more transparent at high zoom so basemap shows through
const BASE_OPACITY = [
  'interpolate', ['linear'], ['zoom'],
  5,  0.80,   // state overview — risk dominates
  8,  0.65,   // county level
  11, 0.45,   // city/street level — basemap readable underneath
];

/**
 * Add the H3 risk grid GeoJSON source and all risk visualization layers.
 * Must be called inside map.on('load', ...).
 * Returns a cleanup function that stops the pulse animation.
 */
export function addRiskGridLayer(map, geojson, beforeId) {
  map.addSource(SOURCE_ID, {
    type: 'geojson',
    data: geojson,
  });

  // Base fill layer — color-coded by riskCategory, zoom-faded
  map.addLayer({
    id:     FILL_ID,
    type:   'fill',
    source: SOURCE_ID,
    paint: {
      'fill-color':   riskColorExpression(),
      'fill-opacity': BASE_OPACITY,
    },
  }, beforeId);

  // Outline layer — subtle separator between regions
  map.addLayer({
    id:     LINE_ID,
    type:   'line',
    source: SOURCE_ID,
    paint: {
      'line-color':   '#ffffff',
      'line-width':   0.5,
      'line-opacity': 0.20,
    },
  }, beforeId);

  // Bold border ring for High / Very High / Extreme regions
  map.addLayer({
    id:     HIGH_RING_ID,
    type:   'line',
    source: SOURCE_ID,
    filter: ['in', ['get', 'riskCategory'], ['literal', ['High', 'Very High', 'Extreme']]],
    paint: {
      'line-color': [
        'match', ['get', 'riskCategory'],
        'Extreme',   '#ff2222',
        'Very High', '#d0021b',
        /* High */   '#f5a623',
      ],
      'line-width':   ['match', ['get', 'riskCategory'], 'Extreme', 2.5, 1.5],
      'line-opacity': 0.85,
    },
  }, beforeId);

  // Animated pulse overlay for Extreme regions only
  map.addLayer({
    id:     PULSE_ID,
    type:   'fill',
    source: SOURCE_ID,
    filter: ['==', ['get', 'riskCategory'], 'Extreme'],
    paint: {
      'fill-color':   '#ff0000',
      'fill-opacity': 0.0,
    },
  }, beforeId);

  return startPulse(map);
}

function startPulse(map) {
  let opacity  = 0.15;
  let goingUp  = true;
  const handle = setInterval(() => {
    if (!map.getLayer(PULSE_ID)) { clearInterval(handle); return; }
    opacity  += goingUp ? 0.03 : -0.03;
    if (opacity >= 0.55) goingUp = false;
    if (opacity <= 0.10) goingUp = true;
    map.setPaintProperty(PULSE_ID, 'fill-opacity', opacity);
  }, 60);
  return () => clearInterval(handle);
}

/**
 * Update the GeoJSON data in-place without removing/re-adding layers.
 * Use this for periodic data refreshes.
 */
export function updateRiskGridData(map, geojson) {
  const source = map.getSource(SOURCE_ID);
  if (source) source.setData(geojson);
}

/**
 * Highlight a selected region by bumping its fill-opacity above the zoom-faded base.
 * Pass null to restore the zoom-interpolated default.
 */
export function highlightRegion(map, h3Index) {
  if (!map.getLayer(FILL_ID)) return;

  const opacity = h3Index
    ? ['case', ['==', ['get', 'h3Index'], h3Index], 0.95, BASE_OPACITY]
    : BASE_OPACITY;

  map.setPaintProperty(FILL_ID, 'fill-opacity', opacity);
}

/**
 * Highlight only regions matching `category`, dim the rest.
 * Pass null to restore the default view.
 */
export function filterByRiskCategory(map, category) {
  if (!map.getLayer(FILL_ID)) return;

  if (category === null) {
    map.setPaintProperty(FILL_ID, 'fill-opacity', BASE_OPACITY);
    map.setFilter(PULSE_ID, ['==', ['get', 'riskCategory'], 'Extreme']);
    map.setFilter(HIGH_RING_ID, ['in', ['get', 'riskCategory'], ['literal', ['High', 'Very High', 'Extreme']]]);
  } else {
    map.setPaintProperty(FILL_ID, 'fill-opacity', [
      'case', ['==', ['get', 'riskCategory'], category], 0.95, 0.08,
    ]);
    map.setFilter(PULSE_ID, category === 'Extreme'
      ? ['==', ['get', 'riskCategory'], 'Extreme']
      : ['in', ['get', 'riskCategory'], ['literal', []]]
    );
    if (['High', 'Very High', 'Extreme'].includes(category)) {
      map.setFilter(HIGH_RING_ID, ['==', ['get', 'riskCategory'], category]);
    } else {
      map.setFilter(HIGH_RING_ID, ['in', ['get', 'riskCategory'], ['literal', []]]);
    }
  }
}

export { FILL_ID, LINE_ID, SOURCE_ID, PULSE_ID, HIGH_RING_ID };
