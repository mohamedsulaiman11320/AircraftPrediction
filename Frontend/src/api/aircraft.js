import { api } from './client.js';

export const aircraftApi = {
  list: ({ search, status, fleetGroup, model, page = 1, pageSize = 25 } = {}) =>
    api.get('/aircraft', { search, status, fleetGroup, model, page, pageSize }),
  fleetGroups: () => api.get('/aircraft/fleet-groups'),
  get: (id) => api.get(`/aircraft/${id}`),
  create: (body) => api.post('/aircraft', body),
  update: (id, body) => api.put(`/aircraft/${id}`, body),
  changeStatus: (id, status, reason) => api.post(`/aircraft/${id}/status`, { status, reason }),
  maintenanceHistory: (id, { page = 1, pageSize = 10 } = {}) =>
    api.get(`/aircraft/${id}/maintenance-history`, { page, pageSize }),
  inspectionFlags: (id, activeOnly = true) => api.get(`/aircraft/${id}/inspection-flags`, { activeOnly }),
  raiseFlag: (id, body) => api.post(`/aircraft/${id}/inspection-flags`, body),
  clearFlag: (flagId, clearanceNotes) => api.post(`/inspection-flags/${flagId}/clear`, { clearanceNotes }),
};

export const maintenanceLogsApi = {
  list: ({ aircraftId, eventType, fromUtc, toUtc, page = 1, pageSize = 25 } = {}) =>
    api.get('/maintenance-logs', { aircraftId, eventType, fromUtc, toUtc, page, pageSize }),
  create: (body) => api.post('/maintenance-logs', body),
};
