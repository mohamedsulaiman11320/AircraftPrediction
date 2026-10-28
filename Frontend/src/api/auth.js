import { api } from './client.js';

export const authApi = {
  login: (username, password) => api.post('/auth/login', { username, password }, { auth: false }),
  register: ({ username, email, displayName, password, confirmPassword }) =>
    api.post('/auth/register', { username, email, displayName, password, confirmPassword }, { auth: false }),
  forgotPassword: (email) => api.post('/auth/forgot-password', { email }, { auth: false }),
  resetPassword: ({ token, newPassword, confirmPassword }) =>
    api.post('/auth/reset-password', { token, newPassword, confirmPassword }, { auth: false }),
  logout: (refreshToken) => api.post('/auth/logout', refreshToken ? { refreshToken } : {}),
  me: () => api.get('/auth/me'),
};
