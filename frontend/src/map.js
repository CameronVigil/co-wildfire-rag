import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { COLORADO_CENTER, COLORADO_ZOOM } from './config.js';
import { addRiskGridLayer, highlightCell, FILL_ID } from './layers/riskGrid.js';
import { fetchRiskGrid } from './api.js';

let _map = null;

/**
 * Initialize the MapLibre map.
 * @param {string} containerId  - DOM element ID for the map
 * @param {object} callbacks
 * @param {function} callbacks.onCellClick  - called with (feature.properties) on hex click
 */
export function initMap(containerId, { onCellClick } = {}) {
  _map = new maplibregl.Map({
    container: containerId,
    style: buildStyle(),
    center: [COLORADO_CENTER.lng, COLORADO_CENTER.lat],
    zoom:   COLORADO_ZOOM,
    minZoom: 5,
    maxZoom: 14,
  });

  _map.addControl(new maplibregl.NavigationControl(), 'top-right');
  _map.addControl(new maplibregl.ScaleControl({ unit: 'imperial' }), 'bottom-left');

  _map.on('load', async () => {
    try {
      const geojson = await fetchRiskGrid(6);
      addRiskGridLayer(_map, geojson);
      registerInteractions(onCellClick);
    } catch (err) {
      console.error('Failed to load risk grid:', err);
    }
  });

  return _map;
}

function registerInteractions(onCellClick) {
  // Cursor changes
  _map.on('mouseenter', FILL_ID, () => {
    _map.getCanvas().style.cursor = 'pointer';
  });
  _map.on('mouseleave', FILL_ID, () => {
    _map.getCanvas().style.cursor = '';
  });

  // Cell click
  _map.on('click', FILL_ID, (e) => {
    if (!e.features?.length) return;
    const props = e.features[0].properties;
    highlightCell(_map, props.h3Index);
    if (onCellClick) onCellClick(props);
  });

  // Click on empty map — deselect
  _map.on('click', (e) => {
    const features = _map.queryRenderedFeatures(e.point, { layers: [FILL_ID] });
    if (!features.length) {
      highlightCell(_map, null);
      if (onCellClick) onCellClick(null);
    }
  });
}

function buildStyle() {
  return {
    version: 8,
    sources: {
      'carto-dark': {
        type:        'raster',
        tiles:       ['https://basemaps.cartocdn.com/dark_matter_nolabels/{z}/{x}/{y}.png'],
        tileSize:    256,
        attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors © <a href="https://carto.com/attributions">CARTO</a>',
      },
      'carto-labels': {
        type:     'raster',
        tiles:    ['https://basemaps.cartocdn.com/dark_matter_only_labels/{z}/{x}/{y}.png'],
        tileSize: 256,
      },
    },
    layers: [
      { id: 'background', type: 'raster', source: 'carto-dark' },
      // Labels go on top — added after risk layers so they're always readable
    ],
    // Label layer added after risk grid so it renders above hex fills
    glyphs: 'https://demotiles.maplibre.org/font/{fontstack}/{range}.pbf',
  };
}

/**
 * Add city labels on top of hex layer (called after addRiskGridLayer).
 * Separated so label layer is always above the hex fill.
 */
export function addLabelLayer(map) {
  if (!map.getSource('carto-labels')) return;
  if (map.getLayer('labels')) return;
  map.addLayer({
    id:     'labels',
    type:   'raster',
    source: 'carto-labels',
  });
}
