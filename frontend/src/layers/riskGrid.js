import { riskColorExpression } from '../config.js';

const SOURCE_ID = 'risk-grid';
const FILL_ID   = 'risk-fill';
const LINE_ID   = 'risk-outline';

/**
 * Add the H3 risk grid GeoJSON source and fill/outline layers to the map.
 * Must be called inside map.on('load', ...).
 */
export function addRiskGridLayer(map, geojson) {
  map.addSource(SOURCE_ID, {
    type: 'geojson',
    data: geojson,
  });

  // Fill layer — color-coded by riskCategory
  map.addLayer({
    id:     FILL_ID,
    type:   'fill',
    source: SOURCE_ID,
    paint: {
      'fill-color':   riskColorExpression(),
      'fill-opacity': 0.65,
    },
  });

  // Outline layer — subtle separator between cells
  map.addLayer({
    id:     LINE_ID,
    type:   'line',
    source: SOURCE_ID,
    paint: {
      'line-color':   '#ffffff',
      'line-width':   0.5,
      'line-opacity': 0.25,
    },
  });
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
 * Highlight a selected cell by bumping its fill-opacity.
 * Pass null to clear selection.
 */
export function highlightCell(map, h3Index) {
  if (!map.getLayer(FILL_ID)) return;

  const opacity = h3Index
    ? ['case', ['==', ['get', 'h3Index'], h3Index], 0.92, 0.60]
    : 0.65;

  map.setPaintProperty(FILL_ID, 'fill-opacity', opacity);
}

export { FILL_ID, LINE_ID, SOURCE_ID };
