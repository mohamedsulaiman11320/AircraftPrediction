import { api } from './client.js';

export const forecastApi = {
  /**
   * Future sensor readings for one aircraft. horizon: 'OneDay' | 'SevenDays' | 'ThirtyDays'.
   * -> { status: 'Available' | 'NotAvailable', reason, supportedHorizons, modelName, modelVersion, generatedAtUtc,
   *      dataAsOfUtc, points: [{ timestampUtc, temperatureC, pressurePsi, vibrationIndex }] }
   * Never contains recorded telemetry (that is GET /telemetry/series).
   */
  sensorForecast: (aircraftId, horizon) => api.get(`/aircraft/${aircraftId}/sensor-forecast`, { horizon }),
};
