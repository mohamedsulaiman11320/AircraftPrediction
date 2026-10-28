import { api } from './client.js';

export const dashboardsApi = {
  // fleetGroup undefined = all fleets; the server validates the name (400 for an unknown fleet).
  dispatcher: (fleetGroup) => api.get('/dashboards/dispatcher', { fleetGroup }),
  technician: () => api.get('/dashboards/technician'),
  dataEngineer: () => api.get('/dashboards/data-engineer'),
  leadership: () => api.get('/dashboards/leadership'),
};
