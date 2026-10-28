import { api, downloadCsv } from './client.js';

export const REPORTS = {
  fleetHealth: '/reports/fleet-health',
  maintenanceHistory: '/reports/maintenance-history',
  dataQuality: '/reports/data-quality',
  modelPerformance: '/reports/model-performance',
};

export const reportsApi = {
  fleetHealth: () => api.get(REPORTS.fleetHealth, { format: 'json' }),
  maintenanceHistory: ({ fromUtc, toUtc } = {}) => api.get(REPORTS.maintenanceHistory, { format: 'json', fromUtc, toUtc }),
  dataQuality: () => api.get(REPORTS.dataQuality, { format: 'json' }),
  modelPerformance: () => api.get(REPORTS.modelPerformance, { format: 'json' }),
  exportCsv: (key, query = {}) => {
    const stamp = new Date().toISOString().slice(0, 10);
    const slug = REPORTS[key].split('/').pop();
    return downloadCsv(REPORTS[key], query, `${slug}-${stamp}.csv`);
  },
};
