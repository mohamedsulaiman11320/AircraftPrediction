import { api } from './client.js';

export const workOrdersApi = {
  // source: 'Automatic' | 'Manual'; anomalyId: the work order raised for that anomaly (server-side filters).
  list: ({ aircraftId, status, priority, assignedToMe, source, anomalyId, page = 1, pageSize = 25 } = {}) =>
    api.get('/work-orders', { aircraftId, status, priority, source, anomalyId, assignedToMe: assignedToMe ? 'true' : undefined, page, pageSize }),
  get: (id) => api.get(`/work-orders/${id}`),
  create: (body) => api.post('/work-orders', body),
  update: (id, body) => api.patch(`/work-orders/${id}`, body),
  assign: (id, technicianId) => api.post(`/work-orders/${id}/assign`, { technicianId }),
  startInspection: (id) => api.post(`/work-orders/${id}/start-inspection`, {}),
  recordInspection: (id, inspectionFindings) => api.post(`/work-orders/${id}/inspection`, { inspectionFindings }),
  resolve: (id, { actionCode, correctiveAction, actualFailureConfirmed, resolutionNotes }) =>
    api.post(`/work-orders/${id}/resolve`, { actionCode, correctiveAction, actualFailureConfirmed, resolutionNotes }),
  reopen: (id, reason) => api.post(`/work-orders/${id}/reopen`, { reason }),
  close: (id, resolutionNotes) => api.post(`/work-orders/${id}/close`, resolutionNotes ? { resolutionNotes } : {}),
  cancel: (id, reason) => api.post(`/work-orders/${id}/cancel`, { reason }),
  technicians: () => api.get('/technicians'),
  /** Approved maintenance action codes [{ code, description }] (Dispatcher + Technician). */
  actionCodes: () => api.get('/maintenance-action-codes'),
};
