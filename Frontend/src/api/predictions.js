import { api } from './client.js';

export const predictionsApi = {
  list: ({ aircraftId, riskCategory, basis, approvalStatus, page = 1, pageSize = 25 } = {}) =>
    api.get('/predictions', { aircraftId, riskCategory, basis, approvalStatus, page, pageSize }),
  forAircraft: (aircraftId) => api.get(`/predictions/aircraft/${aircraftId}`),
  metadata: () => api.get('/predictions/metadata'),
  review: (id, approvalStatus, notes) => api.post(`/predictions/${id}/review`, { approvalStatus, notes }),
  outcome: (id, actualOutcome, workOrderId) =>
    api.post(`/predictions/${id}/outcome`, workOrderId ? { actualOutcome, workOrderId } : { actualOutcome }),
};
