import { api } from './client.js';

export const pipelinesApi = {
  runs: ({ jobType, status, page = 1, pageSize = 25 } = {}) => api.get('/pipelines/runs', { jobType, status, page, pageSize }),
  getRun: (id) => api.get(`/pipelines/runs/${id}`),
  startJob: (jobType, parameters) => api.post('/pipelines/jobs', parameters ? { jobType, parameters } : { jobType }),
  cancelRun: (id) => api.post(`/pipelines/runs/${id}/cancel`, {}),
  datasetAudit: () => api.get('/pipelines/dataset-audit'),
};
