/**
 * Fictional demo fixtures - used ONLY when VITE_DEMO_MODE=true. Registrations, people and values
 * are invented. Probabilities are never populated (no validated failure model exists in the demo),
 * and model metrics contain no accuracy figures.
 */
const DAY = 86400000;
const now = Date.now();
const iso = (ms) => new Date(ms).toISOString();

// Deterministic pseudo-random generator so the demo is stable between reloads.
function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s * 1664525 + 1013904223) >>> 0;
    return s / 4294967296;
  };
}
const rand = rng(42);

export const DISCLAIMER =
  'Advisory only. This indicator supports maintenance prioritisation and does not determine airworthiness. Dispatch decisions remain with certified personnel following approved procedures.';
export const SCORE_NOTE =
  'The anomaly score measures how unusual this reading is relative to the training data (higher = more unusual). It is not a probability of failure.';

const MODELS = [
  ['A320neo', 'Airbus', 'Narrowbody'],
  ['737-8', 'Boeing', 'Narrowbody'],
  ['E195-E2', 'Embraer', 'Regional Jet'],
  ['ATR 72-600', 'ATR', 'Turboprop'],
];

// Demo sign-in: username "dispatcher", "technician" or "dataengineer", any password. The others exist so the
// admin screens and technician picker have something to show (they cannot sign in).
export const DISPATCHER_ROLE = 'FlightDispatcher';
export const TECHNICIAN_ROLE = 'MaintenanceTechnician';
export const DATA_ENGINEER_ROLE = 'DataEngineer';
export const roles = [
  { roleId: 1, roleName: DISPATCHER_ROLE },
  { roleId: 2, roleName: TECHNICIAN_ROLE },
  { roleId: 3, roleName: DATA_ENGINEER_ROLE },
];

export const users = [
  { id: 1, username: 'dispatcher', email: 'dispatcher@demo.example', displayName: 'Dana Dispatch', roles: [DISPATCHER_ROLE], canSignIn: true },
  { id: 2, username: 'technician', email: 'technician@demo.example', displayName: 'Toni Technician', roles: [TECHNICIAN_ROLE], canSignIn: true },
  { id: 3, username: 'sam.spanner', email: 'sam.spanner@demo.example', displayName: 'Sam Spanner', roles: [TECHNICIAN_ROLE] },
  { id: 5, username: 'dataengineer', email: 'dataengineer@demo.example', displayName: 'Eli Engineer', roles: [DATA_ENGINEER_ROLE], canSignIn: true },
  { id: 4, username: 'pat.pending', email: 'new.hire@demo.example', displayName: 'Pat Pending', roles: [], status: 'Pending' },
].map((u) => ({
  status: 'Active',
  createdAtUtc: iso(now - 120 * DAY),
  lastLoginAtUtc: u.status === 'Pending' ? null : iso(now - rand() * 3 * DAY),
  approvedAtUtc: u.status === 'Pending' ? null : iso(now - 110 * DAY),
  failedLoginCount: 0,
  lockoutEndUtc: null,
  disabledReason: null,
  ...u,
}));

export const aircraft = Array.from({ length: 12 }, (_, i) => {
  const [model, manufacturer, fleetGroup] = MODELS[i % 4];
  const status = i === 4 ? 'Grounded' : i === 7 || i === 10 ? 'UnderMaintenance' : 'Available';
  return {
    id: `ac-${String(i + 1).padStart(2, '0')}`,
    registrationNumber: `DEMO-A${String(i + 1).padStart(2, '0')}`,
    model,
    manufacturer,
    fleetGroup,
    serialNumber: `SN-DEMO-${1000 + i}`,
    operationalStatus: status,
    statusReason: status === 'Grounded' ? 'Grounding inspection flag raised' : status === 'UnderMaintenance' ? 'Scheduled A-check' : null,
    statusChangedAtUtc: iso(now - (2 + i) * DAY),
    createdAtUtc: iso(now - 400 * DAY),
    lastTelemetryAtUtc: iso(now - (i + 1) * 3600000),
    lastInspectionAtUtc: iso(now - (10 + i * 3) * DAY),
  };
});

const RISKS = ['Low', 'Low', 'Elevated', 'Low', 'High', 'Low', 'Elevated', 'Undetermined', 'Low', 'Low', 'Elevated', 'Low'];

export const predictions = aircraft.slice(0, 11).map((a, i) => ({
  id: `pr-${i + 1}`,
  aircraftId: a.id,
  registrationNumber: a.registrationNumber,
  componentId: null,
  predictionTimestampUtc: iso(now - 6 * 3600000),
  featureCutoffUtc: iso(now - 7 * 3600000),
  horizonDays: 30,
  predictionState: RISKS[i] === 'Undetermined' ? 'InsufficientTelemetry' : 'Scored',
  riskCategory: RISKS[i],
  basis: 'DescriptiveIndicator',
  probability: null,
  probabilityValidated: false,
  featureSnapshot: { anomalies_30d: [0, 1, 3, 0, 6, 1, 2, 0, 0, 1, 4, 0][i], suspect_ratio_7d: Number((rand() * 0.05).toFixed(3)), unscheduled_events_180d: i % 3 },
  modelName: null,
  modelVersion: null,
  approvalStatus: i % 5 === 0 ? 'Approved' : 'PendingReview',
  reviewedBy: i % 5 === 0 ? 'Toni Technician' : null,
  reviewedAtUtc: i % 5 === 0 ? iso(now - DAY) : null,
  reviewNotes: i % 5 === 0 ? 'Consistent with recent inspection findings.' : null,
  actualOutcome: 'Unknown',
  outcomeRecordedAtUtc: null,
  disclaimer: DISCLAIMER,
}));

export const anomalies = Array.from({ length: 24 }, (_, i) => {
  const a = aircraft[[4, 2, 6, 10, 1, 4, 2, 8, 6, 10, 4, 0][i % 12]];
  const sev = i % 5 === 0 ? 'High' : i % 3 === 0 ? 'Medium' : 'Low';
  const status = i < 6 ? 'New' : i < 10 ? 'Acknowledged' : i < 13 ? 'Investigating' : i < 20 ? 'Dismissed' : 'ConfirmedIssue';
  return {
    id: `an-${i + 1}`,
    aircraftId: a.id,
    registrationNumber: a.registrationNumber,
    sensorId: `ENG${(i % 2) + 1}-OIL`,
    telemetryRecordId: 10000 + i,
    observedAtUtc: iso(now - i * 1.2 * DAY - 3600000),
    anomalyScore: Number((0.55 + rand() * 0.4).toFixed(4)),
    severity: sev,
    modelName: 'oil-temp-pressure-iforest',
    modelVersion: '1.2.0',
    modelVersionId: 'mv-an-2',
    scoredAtUtc: iso(now - i * 1.2 * DAY),
    investigationStatus: status,
    acknowledgedBy: status === 'New' ? null : 'Toni Technician',
    acknowledgedAtUtc: status === 'New' ? null : iso(now - i * DAY),
    investigationNotes: status === 'Dismissed' ? 'Sensor recalibrated; reading within expected envelope after check.' : null,
    featureValues: { temperature_c: Number((95 + rand() * 30).toFixed(2)), pressure_kpa: Number((280 + rand() * 60).toFixed(2)), temp_delta_1h: Number((rand() * 8).toFixed(2)), pressure_delta_1h: Number((rand() * 15 - 7).toFixed(2)) },
    scoreNote: SCORE_NOTE,
  };
});

const techs = users.filter((u) => u.roles.includes(TECHNICIAN_ROLE));

export const workOrders = Array.from({ length: 10 }, (_, i) => {
  const a = aircraft[[4, 7, 10, 2, 6, 0, 1, 4, 3, 8][i]];
  const status = ['Open', 'Assigned', 'InInspection', 'Resolved', 'Open', 'Closed', 'Cancelled', 'Assigned', 'Open', 'InInspection'][i];
  const priority = ['Critical', 'High', 'Medium', 'High', 'Low', 'Medium', 'Low', 'High', 'Medium', 'Medium'][i];
  const tech = ['Assigned', 'InInspection', 'Resolved', 'Closed'].includes(status) ? techs[i % 3 === 2 ? 1 : 0] : null;
  return {
    id: `wo-${i + 1}`,
    workOrderNumber: `WO-2026-${String(120 + i).padStart(6, '0')}`,
    aircraftId: a.id,
    registrationNumber: a.registrationNumber,
    componentId: null,
    anomalyId: i < 4 ? `an-${i + 1}` : null,
    predictionId: null,
    priority,
    title: ['Inspect engine 1 oil system', 'A-check findings follow-up', 'Hydraulic pressure sensor check', 'Oil temperature trend review', 'Cabin pressure log review', 'Replace oil pressure transducer', 'Duplicate request', 'Borescope inspection engine 2', 'Bleed air temperature review', 'APU oil pressure check'][i],
    description: 'Investigate readings flagged during routine telemetry review and record findings per the maintenance manual.',
    status,
    assignedTechnicianId: tech?.id || null,
    assignedTechnicianName: tech?.displayName || null,
    createdBy: 'Dana Dispatch',
    createdAtUtc: iso(now - (12 - i) * DAY),
    dueAtUtc: iso(now + (i - 3) * DAY),
    isOverdue: i < 3 && !['Resolved', 'Closed', 'Cancelled'].includes(status),
    inspectionFindings: ['InInspection', 'Resolved', 'Closed'].includes(status) && status !== 'InInspection' ? 'Transducer connector corrosion found.' : null,
    correctiveAction: ['Resolved', 'Closed'].includes(status) ? 'Connector cleaned and transducer replaced.' : null,
    actualFailureConfirmed: ['Resolved', 'Closed'].includes(status) ? i === 5 : null,
    resolutionNotes: ['Resolved', 'Closed'].includes(status) ? 'Ground run satisfactory.' : null,
    resolvedAtUtc: ['Resolved', 'Closed'].includes(status) ? iso(now - DAY) : null,
    closedAtUtc: status === 'Closed' ? iso(now - 0.5 * DAY) : null,
    updatedAtUtc: iso(now - DAY),
    concurrencyStamp: `stamp-${i}-1`,
  };
});

/** Demo stand-ins for GET /maintenance-action-codes (the live list comes from the database). */
export const maintenanceActionCodes = [
  { code: 'DEMO_INSP', description: 'Demo inspection' },
  { code: 'DEMO_REPL', description: 'Demo component replacement' },
];

export const inspectionFlags = [
  { id: 'fl-1', aircraftId: 'ac-05', severity: 'Grounding', reason: 'Repeated high-severity oil pressure anomalies pending inspection', workOrderId: 'wo-1' },
  { id: 'fl-2', aircraftId: 'ac-03', severity: 'Restrictive', reason: 'ETOPS restricted until hydraulic sensor check complete', workOrderId: 'wo-4' },
  { id: 'fl-3', aircraftId: 'ac-07', severity: 'Advisory', reason: 'Monitor oil temperature trend on next sectors', workOrderId: null },
].map((f) => {
  const a = aircraft.find((x) => x.id === f.aircraftId);
  return {
    registrationNumber: a.registrationNumber,
    raisedBy: 'Toni Technician',
    raisedAtUtc: iso(now - 2 * DAY),
    isActive: true,
    clearedBy: null,
    clearedAtUtc: null,
    clearanceNotes: null,
    anomalyId: null,
    predictionId: null,
    ...f,
  };
});

export const maintenanceLogs = aircraft.flatMap((a, i) =>
  Array.from({ length: 4 }, (_, j) => ({
    id: `ml-${i}-${j}`,
    aircraftId: a.id,
    registrationNumber: a.registrationNumber,
    componentId: null,
    eventType: ['Scheduled', 'Inspection', 'Unscheduled', 'Scheduled'][(i + j) % 4],
    occurredAtUtc: iso(now - (j * 35 + i) * DAY),
    description: ['A-check completed', 'Visual inspection of engine nacelles', 'Oil pressure transducer replaced', 'Cabin systems check'][(i + j) % 4],
    actualFailureConfirmed: (i + j) % 4 === 2 ? true : null,
    isVerified: true,
    source: 'Manual',
    workOrderId: null,
  })),
);

export const models = [
  {
    id: 'mv-an-1',
    modelName: 'oil-temp-pressure-iforest',
    version: '1.1.0',
    modelType: 'AnomalyDetection',
    algorithm: 'IsolationForest',
    status: 'Retired',
    retiredAtUtc: iso(now - 20 * DAY),
    activatedAtUtc: iso(now - 80 * DAY),
  },
  {
    id: 'mv-an-2',
    modelName: 'oil-temp-pressure-iforest',
    version: '1.2.0',
    modelType: 'AnomalyDetection',
    algorithm: 'IsolationForest',
    status: 'Active',
    activatedAtUtc: iso(now - 20 * DAY),
  },
  {
    id: 'mv-an-3',
    modelName: 'oil-temp-pressure-iforest',
    version: '1.3.0',
    modelType: 'AnomalyDetection',
    algorithm: 'IsolationForest',
    status: 'Trained',
  },
].map((m) => ({
  artifactSha256: 'demo0000000000000000000000000000000000000000000000000000000000' + m.version.replace(/\./g, ''),
  featureNames: ['temperature_c', 'pressure_kpa', 'temp_delta_1h', 'pressure_delta_1h'],
  hyperparameters: { n_estimators: 200, contamination: 0.01, random_state: 42 },
  metrics: { training_rows: 184320, score_threshold: 0.55, flagged_share_training: 0.01 },
  trainingDataFromUtc: iso(now - 200 * DAY),
  trainingDataToUtc: iso(now - 30 * DAY),
  trainingRowCount: 184320,
  predictionHorizonDays: null,
  limitations: 'Unsupervised: flags statistically unusual readings only. No labelled failures were used, so detection performance against real failures is unknown.',
  notes: null,
  createdAtUtc: iso(now - 30 * DAY),
  registeredAtUtc: m.status === 'Trained' ? null : iso(now - 29 * DAY),
  approvedBy: m.status === 'Trained' ? null : 'Lee Leadership',
  approvedAtUtc: m.status === 'Trained' ? null : iso(now - 25 * DAY),
  activatedAtUtc: null,
  retiredAtUtc: null,
  ...m,
}));

export const failurePredictionStatus = {
  status: 'Blocked',
  reasons: [
    'Fewer than the required number of verified unscheduled maintenance events with linked telemetry.',
    'No approved engineering thresholds are configured.',
  ],
  horizonDays: 30,
};

/** Shape written by the Python dataset-audit job (snake_case, passed through unchanged by the API). */
export const auditReadiness = {
  status: 'Blocked',
  reasons: failurePredictionStatus.reasons,
  horizon_days: 30,
  positive_examples: 4,
  negative_examples: 1820,
  aircraft_with_positives: 3,
};

export const imports = Array.from({ length: 5 }, (_, i) => ({
  id: `im-${i + 1}`,
  fileName: `demo_fleet_telemetry_2026_0${i + 4}.csv`,
  fileSha256: `demo${i}`.padEnd(64, 'a'),
  source: 'Csv',
  status: i === 0 ? 'Failed' : 'Completed',
  totalRows: i === 0 ? 0 : 40000 + i * 1200,
  acceptedRows: i === 0 ? 0 : 39500 + i * 1100,
  rejectedRows: i === 0 ? 0 : 320 + i * 20,
  duplicateRows: i === 0 ? 0 : 180 + i * 80,
  warningCount: i === 0 ? 0 : 2,
  warnings: i === 0 ? [] : ['12 rows had pressure in psi and were normalised to kPa.', '3 rows were missing a sensor_id.'],
  errorMessage: i === 0 ? 'Header row is missing required column "pressure_unit".' : null,
  requestedBy: 'Eli Engineer',
  requestedAtUtc: iso(now - (5 - i) * 7 * DAY),
  startedAtUtc: iso(now - (5 - i) * 7 * DAY + 2000),
  completedAtUtc: iso(now - (5 - i) * 7 * DAY + 90000),
})).reverse();

imports.unshift(
  {
    id: 'im-raw-1',
    fileName: 'demo_raw_sensor_dump_2026_09.json',
    source: 'RawJson',
    fileSha256: 'demoraw1'.padEnd(64, 'b'),
    status: 'Failed',
    totalRows: 0,
    acceptedRows: 0,
    rejectedRows: 0,
    duplicateRows: 0,
    warningCount: 0,
    warnings: [],
    errorMessage: 'ETL job failed: Spark executor lost while reading the source file (demo).',
    requestedBy: 'Eli Engineer',
    requestedAtUtc: iso(now - 2 * DAY),
    startedAtUtc: iso(now - 2 * DAY + 4000),
    completedAtUtc: iso(now - 2 * DAY + 60000),
  },
  {
    id: 'im-raw-2',
    fileName: 'demo_raw_sensor_dump_2026_10.json',
    source: 'RawJson',
    fileSha256: 'demoraw2'.padEnd(64, 'c'),
    status: 'Completed',
    totalRows: 18240,
    acceptedRows: 18011,
    rejectedRows: 96,
    duplicateRows: 133,
    warningCount: 1,
    warnings: ['41 records had no vibration_index; stored without vibration.'],
    errorMessage: null,
    requestedBy: 'ETL (placed directly in object storage)',
    requestedAtUtc: iso(now - DAY),
    startedAtUtc: iso(now - DAY + 3000),
    completedAtUtc: iso(now - DAY + 240000),
  },
);

export const runs = [
  { jobType: 'Score', status: 'Succeeded', recordsProcessed: 48210, resultSummary: { recordsScored: 48210, anomaliesFlagged: 37, riskIndicatorsUpdated: 12 } },
  {
    jobType: 'TrainFailure',
    status: 'Blocked',
    recordsProcessed: null,
    resultSummary: { failure_prediction_readiness: auditReadiness },
    errorMessage: 'Failure-prediction training is blocked: readiness requirements are not met.',
  },
  {
    jobType: 'DatasetAudit',
    status: 'Succeeded',
    recordsProcessed: 512000,
    resultSummary: {
      telemetryRecords: 512000,
      suspectRecords: 6210,
      aircraftWithTelemetry: 12,
      verifiedUnscheduledEvents: 4,
      failure_prediction_readiness: auditReadiness,
    },
  },
  { jobType: 'TrainAnomaly', status: 'Succeeded', recordsProcessed: 184320, resultSummary: { modelVersion: '1.3.0', trainingRows: 184320 }, modelVersionId: 'mv-an-3' },
  { jobType: 'TelemetryImport', status: 'Succeeded', recordsProcessed: 44300, resultSummary: { importId: 'im-5' } },
].map((r, i) => ({
  id: `run-${i + 1}`,
  trigger: i === 4 ? 'Api' : 'Cli',
  requestedBy: 'Eli Engineer',
  requestedAtUtc: iso(now - (i + 1) * 0.8 * DAY),
  startedAtUtc: iso(now - (i + 1) * 0.8 * DAY + 5000),
  completedAtUtc: iso(now - (i + 1) * 0.8 * DAY + 600000),
  parameters: {},
  errorMessage: null,
  modelVersionId: null,
  ...r,
}));

/** Smooth synthetic series for an aircraft. */
export function seriesFor(aircraftId, fromMs, toMs, bucketMinutes) {
  const seed = parseInt(aircraftId.replace(/\D/g, ''), 10) || 1;
  const step = bucketMinutes * 60000;
  const pts = [];
  const max = 2000;
  const start = Math.ceil(fromMs / step) * step;
  for (let t = start, n = 0; t <= toMs && n < max; t += step, n++) {
    const h = t / 3600000;
    const temp = 98 + seed * 0.8 + 6 * Math.sin(h / 5 + seed) + 2 * Math.sin(h / 1.3);
    const pres = 300 + seed * 1.5 + 12 * Math.cos(h / 7 + seed) + 3 * Math.sin(h / 2.1);
    const vib = 0.35 + 0.08 * Math.sin(h / 3.7 + seed) + 0.02 * Math.cos(h / 1.9);
    const gap = (n + seed) % 97 === 0;
    const kpa = Number(pres.toFixed(2));
    pts.push({
      timestampUtc: iso(t),
      temperatureC: gap ? null : Number(temp.toFixed(2)),
      pressureKpa: kpa,
      // Converted here exactly as the API does it, so the UI never converts a second time.
      pressurePsi: Number((kpa * 0.1450377377).toFixed(2)),
      count: 12,
      vibrationIndex: gap ? null : Number(vib.toFixed(3)),
    });
  }
  return pts;
}
