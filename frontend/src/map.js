import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { COLORADO_CENTER, COLORADO_ZOOM } from './config.js';
import { addRiskGridLayer, highlightRegion, FILL_ID } from './layers/riskGrid.js';
import { addCountyBordersLayer } from './layers/countyBorders.js';
import { fetchRiskGrid } from './api.js';

function getBeforeSymbolLayer(map) {
  return map.getStyle().layers.find(l => l.type === 'symbol')?.id;
}

let _map = null;

export function getMap() { return _map; }

/**
 * Initialize the MapLibre map.
 * @param {string} containerId  - DOM element ID for the map
 * @param {object} callbacks
 * @param {function} callbacks.onRegionClick  - called with (feature.properties) on hex click
 * @param {function} callbacks.onRegionHover  - called with (feature.properties) on hex hover, null on leave
 */
export function initMap(containerId, { onRegionClick, onRegionHover } = {}) {
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
      const beforeId = getBeforeSymbolLayer(_map);
      addTerrainLayer(_map, beforeId);
      const geojson = await fetchRiskGrid(6);
      addRiskGridLayer(_map, geojson, beforeId);
      await addCountyBordersLayer(_map, beforeId);
      styleCityLabels(_map);
      registerInteractions(onRegionClick, onRegionHover);
      updateRiskBadge(_map, geojson);
    } catch (err) {
      console.error('Failed to load risk grid:', err);
    }
  });

  return _map;
}

function registerInteractions(onRegionClick, onRegionHover) {
  // Cursor changes
  _map.on('mouseenter', FILL_ID, () => {
    _map.getCanvas().style.cursor = 'pointer';
  });
  _map.on('mouseleave', FILL_ID, () => {
    _map.getCanvas().style.cursor = '';
  });

  // Region hover — update info panel
  _map.on('mousemove', FILL_ID, (e) => {
    if (e.features?.length && onRegionHover) onRegionHover(e.features[0].properties);
  });
  _map.on('mouseleave', FILL_ID, () => {
    if (onRegionHover) onRegionHover(null);
  });

  // Region click
  _map.on('click', FILL_ID, (e) => {
    if (!e.features?.length) return;
    const props = e.features[0].properties;
    highlightRegion(_map, props.h3Index);
    if (onRegionClick) onRegionClick(props);
  });

  // Click on empty map — deselect
  _map.on('click', (e) => {
    const features = _map.queryRenderedFeatures(e.point, { layers: [FILL_ID] });
    if (!features.length) {
      highlightRegion(_map, null);
      if (onRegionClick) onRegionClick(null);
    }
  });
}

function addTerrainLayer(map, beforeId) {
  map.addSource('terrain-dem', {
    type:     'raster-dem',
    tiles:    ['https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png'],
    tileSize: 256,
    encoding: 'terrarium',
    maxzoom:  15,
    attribution: 'Terrain data © <a href="https://aws.amazon.com/public-datasets/terrain/">AWS</a>',
  });

  map.addLayer({
    id:     'hillshade',
    type:   'hillshade',
    source: 'terrain-dem',
    paint: {
      'hillshade-shadow-color':      '#1a1a2e',
      'hillshade-highlight-color':   '#ffffff',
      'hillshade-accent-color':      '#4a4060',
      'hillshade-illumination-direction': 335,
      'hillshade-exaggeration':      0.35,
    },
  }, beforeId);
}

function buildStyle() {
  return 'https://tiles.openfreemap.org/styles/dark';
}

const PLACE_LAYERS = ['place_city_large', 'place_city', 'place_town', 'place_village', 'place_suburb', 'place_other'];

function styleCityLabels(map) {
  for (const id of PLACE_LAYERS) {
    if (!map.getLayer(id)) continue;
    map.setPaintProperty(id, 'text-color', '#c0c4d0');
    map.setPaintProperty(id, 'text-halo-color', '#000000');
    map.setPaintProperty(id, 'text-halo-width', 1.2);
  }
}

function updateRiskBadge(map, geojson) {
  const el = document.getElementById('risk-badge-overlay');
  if (!el || !geojson?.features) return;

  const features = geojson.features;
  const extremeCount  = features.filter(f => f.properties?.riskCategory === 'Extreme').length;
  const veryHighCount = features.filter(f => f.properties?.riskCategory === 'Very High').length;
  const highCount     = features.filter(f => f.properties?.riskCategory === 'High').length;

  el.innerHTML = '';
  if (extremeCount === 0 && veryHighCount === 0 && highCount === 0) {
    el.classList.remove('has-alerts');
    return;
  }

  // Sort features by riskScore descending to find the top region for fly-to
  const topFeature = [...features]
    .filter(f => f.properties?.riskScore != null)
    .sort((a, b) => Number(b.properties.riskScore) - Number(a.properties.riskScore))[0];

  const flyToTop = () => {
    if (!topFeature?.geometry?.coordinates?.[0]?.[0]) return;
    const coords = topFeature.geometry.coordinates[0];
    const lngs = coords.map(c => c[0]);
    const lats = coords.map(c => c[1]);
    map.flyTo({
      center: [
        (Math.min(...lngs) + Math.max(...lngs)) / 2,
        (Math.min(...lats) + Math.max(...lats)) / 2,
      ],
      zoom:     9,
      duration: 1200,
    });
  };

  if (extremeCount > 0) {
    const chip = document.createElement('div');
    chip.className = 'risk-badge-chip extreme';
    chip.textContent = `⚠ ${extremeCount} Extreme`;
    chip.addEventListener('click', flyToTop);
    el.appendChild(chip);
  }
  if (veryHighCount + highCount > 0) {
    const chip = document.createElement('div');
    chip.className = 'risk-badge-chip high';
    chip.textContent = `${veryHighCount + highCount} High+`;
    chip.addEventListener('click', flyToTop);
    el.appendChild(chip);
  }
  el.classList.add('has-alerts');
}

