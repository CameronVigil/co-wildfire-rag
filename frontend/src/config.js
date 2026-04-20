export const API_BASE = import.meta.env.VITE_API_BASE ?? 'http://localhost:5000';

export const COLORADO_CENTER = { lng: -105.5, lat: 39.0 };
export const COLORADO_ZOOM   = 7;

// Risk category → fill color
export const RISK_COLORS = {
  'Very Low': '#1a7a1a',
  'Low':      '#7dc67d',
  'Moderate': '#f5e642',
  'High':     '#f5a623',
  'Very High':'#d0021b',
  'Extreme':  '#7b0000',
};
export const RISK_COLOR_UNKNOWN = '#444444';

// MapLibre match expression built from RISK_COLORS
export function riskColorExpression() {
  const pairs = Object.entries(RISK_COLORS).flat();
  return ['match', ['get', 'riskCategory'], ...pairs, RISK_COLOR_UNKNOWN];
}
