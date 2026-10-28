import { api } from './client.js';

export const anomaliesApi = {
  // source: IsolationForest | ThresholdRule | FailureModel (optional).
  list: ({ aircraftId, status, severity, fromUtc, toUtc, source, page = 1, pageSize = 25 } = {}) =>
    api.get('/anomalies', { aircraftId, status, severity, fromUtc, toUtc, source, page, pageSize }),
  summary: (days = 30) => api.get('/anomalies/summary', { days }),
  get: (id) => api.get(`/anomalies/${id}`),
  acknowledge: (id, notes) => api.post(`/anomalies/${id}/acknowledge`, notes ? { notes } : {}),
  investigate: (id, status, notes) => api.post(`/anomalies/${id}/investigation`, { status, notes }),
};
