import { api } from './client.js';

export const telemetryApi = {
  records: ({ aircraftId, sensorId, fromUtc, toUtc, qualityStatus, page = 1, pageSize = 50 } = {}) =>
    api.get('/telemetry', { aircraftId, sensorId, fromUtc, toUtc, qualityStatus, page, pageSize }),
  series: ({ aircraftId, fromUtc, toUtc, sensorId, bucketMinutes = 60 } = {}) =>
    api.get('/telemetry/series', { aircraftId, fromUtc, toUtc, sensorId, bucketMinutes }),
  upload: (file) => api.upload('/telemetry/imports', file, 'file'),
  /** Raw JSON telemetry for the Spark ETL. 202 { id, status: "Queued", statusUrl } - NOT complete on upload. */
  uploadRawJson: (file) => api.upload('/telemetry/raw-uploads', file, 'file'),
  /** Re-queue a Failed RawJson import. Returns the TelemetryImportDto (status Queued). */
  retryImport: (id) => api.post(`/telemetry/imports/${id}/retry`),
  imports: ({ page = 1, pageSize = 25 } = {}) => api.get('/telemetry/imports', { page, pageSize }),
  getImport: (id) => api.get(`/telemetry/imports/${id}`),
  rejectedRows: (id, { page = 1, pageSize = 25 } = {}) =>
    api.get(`/telemetry/imports/${id}/rejected-rows`, { page, pageSize }),
  /** Read-only preview of what deleting an import would remove, and what it deliberately keeps. */
  deletionPlan: (id) => api.get(`/telemetry/imports/${id}/deletion-plan`),
  /**
   * Delete an import (DataEngineer only). Returns ImportDeletionResultDto: `completed: false` with a
   * non-empty `remainingWork` means the database rows went but object storage still holds data — a
   * partial failure, never a success. The call is resumable, so re-issuing it is safe.
   */
  deleteImport: (id) => api.del(`/telemetry/imports/${id}`),
  dataQuality: ({ fromUtc, toUtc, aircraftId } = {}) => api.get('/telemetry/data-quality', { fromUtc, toUtc, aircraftId }),
};
