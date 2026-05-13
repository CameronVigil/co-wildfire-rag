import { API_BASE } from '../config.js';

const SOURCE_ID = 'county-borders';
const LINE_ID   = 'county-line';
const LABEL_ID  = 'county-label';

export async function addCountyBordersLayer(map, beforeId) {
  let geojson;
  try {
    const res = await fetch(`${API_BASE}/api/county-bounds`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    geojson = await res.json();
  } catch (err) {
    console.warn('County bounds unavailable:', err.message);
    return;
  }

  if (!geojson?.features?.length) return;

  map.addSource(SOURCE_ID, { type: 'geojson', data: geojson });

  map.addLayer({
    id:     LINE_ID,
    type:   'line',
    source: SOURCE_ID,
    paint:  {
      'line-color':   '#7a8090',
      'line-width':   1,
      'line-opacity': 0.7,
    },
  }, beforeId);

  // County labels render above hex fills — no beforeId so they sit on top
  map.addLayer({
    id:       LABEL_ID,
    type:     'symbol',
    source:   SOURCE_ID,
    minzoom:  6,
    layout:   {
      'text-field':         ['get', 'name'],
      'text-font':          ['Open Sans Regular'],
      'text-size':          11,
      'text-anchor':        'center',
      'text-allow-overlap': false,
    },
    paint: {
      'text-color':      '#c0c4d0',
      'text-halo-color': '#000000',
      'text-halo-width': 1.2,
    },
  });
}
