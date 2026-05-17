import { cellToLatLng } from 'h3-js';

const PULSE_TTL_MS = 5 * 60 * 1000; // 5 minutes

/**
 * Adds a pulsing red circle at the center of an H3 cell to signal a critical event.
 * Auto-removes after 5 minutes. Safe to call multiple times for the same cell.
 */
export function addCriticalPulse(map, h3Index) {
  if (!map || !h3Index) return;

  const layerId  = `critical-pulse-${h3Index}`;
  const sourceId = layerId;

  // Clean up any existing pulse for this cell
  if (map.getLayer(layerId))  map.removeLayer(layerId);
  if (map.getSource(sourceId)) map.removeSource(sourceId);

  const [lat, lng] = cellToLatLng(h3Index);

  map.addSource(sourceId, {
    type: 'geojson',
    data: {
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [lng, lat] },
      properties: {},
    },
  });

  map.addLayer({
    id:     layerId,
    type:   'circle',
    source: sourceId,
    paint: {
      'circle-radius':        28,
      'circle-color':         '#ff2222',
      'circle-opacity':       0.35,
      'circle-stroke-width':  2,
      'circle-stroke-color':  '#ff2222',
      'circle-stroke-opacity': 0.8,
    },
  });

  // Opacity pulse animation
  let high = false;
  const interval = setInterval(() => {
    if (!map.getLayer(layerId)) { clearInterval(interval); return; }
    high = !high;
    map.setPaintProperty(layerId, 'circle-opacity', high ? 0.55 : 0.10);
  }, 600);

  // Auto-remove after TTL
  setTimeout(() => {
    clearInterval(interval);
    if (map.getLayer(layerId))  map.removeLayer(layerId);
    if (map.getSource(sourceId)) map.removeSource(sourceId);
  }, PULSE_TTL_MS);
}
