const env = import.meta.env || {};

/** Backend base URL without trailing slash. */
export const API_BASE_URL = String(env.VITE_API_BASE_URL || 'http://localhost:5080').replace(/\/+$/, '');

/** Demo mode serves fixtures from src/demo instead of calling the API. Off by default. */
export const DEMO_MODE = String(env.VITE_DEMO_MODE || 'false').toLowerCase() === 'true';

export const API_PREFIX = '/api/v1';
