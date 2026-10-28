import { api } from './client.js';

/**
 * The isolated development (synthetic) environment - GET/POST /api/v1/development (docs/API.md).
 * Read-only views plus job queueing; there is deliberately no register/approve/activate here.
 */
export const developmentApi = {
  status: () => api.get('/development'),
  models: () => api.get('/development/models'),
  evaluation: (id) => api.get(`/development/models/${id}/evaluation`),
  runs: () => api.get('/development/runs'),
  predictions: () => api.get('/development/predictions'),
  queue: (jobType, modelVersionId) =>
    api.post('/development/jobs', modelVersionId ? { jobType, modelVersionId } : { jobType }),
};
