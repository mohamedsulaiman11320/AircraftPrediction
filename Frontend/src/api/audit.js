import { api } from './client.js';

export const auditApi = {
  actions: ({ entityType, entityId, userId, fromUtc, toUtc, page = 1, pageSize = 25 } = {}) =>
    api.get('/audit/actions', { entityType, entityId, userId, fromUtc, toUtc, page, pageSize }),
  auth: ({ email, eventType, fromUtc, toUtc, page = 1, pageSize = 25 } = {}) =>
    api.get('/audit/auth', { email, eventType, fromUtc, toUtc, page, pageSize }),
};
