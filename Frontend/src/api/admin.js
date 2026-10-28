import { api } from './client.js';

export const adminApi = {
  listUsers: ({ status, role, search, page = 1, pageSize = 25 } = {}) =>
    api.get('/admin/users', { status, role, search, page, pageSize }),
  /** Role catalogue: [{ roleId, roleName }] - the only source for role pickers. */
  roles: () => api.get('/admin/roles'),
  approve: (id, roles) => api.post(`/admin/users/${id}/approve`, { roles }),
  setRoles: (id, roles) => api.put(`/admin/users/${id}/roles`, { roles }),
  disable: (id, reason) => api.post(`/admin/users/${id}/disable`, { reason }),
  enable: (id) => api.post(`/admin/users/${id}/enable`, {}),
};
