import { api } from './client.js';

export const modelsApi = {
  list: ({ modelType, status } = {}) => api.get('/models', { modelType, status }),
  get: (id) => api.get(`/models/${id}`),
  evaluation: (id) => api.get(`/models/${id}/evaluation`),
  register: (id) => api.post(`/models/${id}/register`, {}),
  approve: (id, notes) => api.post(`/models/${id}/approve`, { notes }),
  reject: (id, notes) => api.post(`/models/${id}/reject`, { notes }),
  activate: (id) => api.post(`/models/${id}/activate`, {}),
  retire: (id) => api.post(`/models/${id}/retire`, {}),
};
