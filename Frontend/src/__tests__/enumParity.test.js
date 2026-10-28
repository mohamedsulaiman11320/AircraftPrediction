import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { JOB_TYPES, MODEL_TYPES, RUN_STATUS, SENSOR_FORECAST_HORIZON } from '../utils/enums.js';

// The backend enums are the source of truth; a value missing here leaves runs unfilterable in the UI.
const enumsCs = readFileSync(
  resolve(process.cwd(), '../Backend/src/AFP.Domain/Enums/Enums.cs'), // vitest runs from Frontend/
  'utf8',
);

function backendEnum(name) {
  const match = enumsCs.match(new RegExp(`public enum ${name}\\s*\\{([^}]*)\\}`));
  return match[1].split(',').map((v) => v.trim()).filter(Boolean);
}

describe('frontend enums match the backend', () => {
  it('lists every pipeline job type', () => {
    expect(JOB_TYPES).toEqual(backendEnum('PipelineJobType'));
  });

  it('lists every pipeline run status', () => {
    expect(RUN_STATUS).toEqual(backendEnum('PipelineRunStatus'));
  });

  it('lists every model type', () => {
    expect(MODEL_TYPES).toEqual(backendEnum('ModelType'));
  });

  it('lists every sensor-forecast horizon', () => {
    expect(SENSOR_FORECAST_HORIZON).toEqual(backendEnum('SensorForecastHorizon'));
  });
});
