/**
 * In-memory demo backend. Only loaded when VITE_DEMO_MODE=true (dynamic import from api/client.js).
 * Mirrors the docs/API.md shapes closely enough to exercise every screen offline.
 * Sign in with any password as username `dispatcher` (Flight Dispatcher, all screens) or
 * `technician` (Maintenance Technician, own tickets only) or `dataengineer` (Data Engineer: data,
 * pipelines and models; read-only anomalies/predictions; aircraft list only).
 */
import { ApiError } from '../api/client.js';
import { matchPath } from '../router/matchPath.js';
import { getStoredUser } from '../auth/session.js';
import * as fx from './fixtures.js';

const db = {
  users: fx.users.map((u) => ({ ...u })),
  aircraft: fx.aircraft.map((a) => ({ ...a })),
  anomalies: fx.anomalies.map((a) => ({ ...a })),
  predictions: fx.predictions.map((p) => ({ ...p })),
  workOrders: fx.workOrders.map((w) => ({ ...w, history: [] })),
  flags: fx.inspectionFlags.map((f) => ({ ...f })),
  logs: fx.maintenanceLogs.map((m) => ({ ...m })),
  models: fx.models.map((m) => ({ ...m })),
  imports: fx.imports.map((i) => ({ ...i })),
  runs: fx.runs.map((r) => ({ ...r })),
  audit: [],
};
let seq = 1000;
const nowIso = () => new Date().toISOString();
const delay = (ms = 180) => new Promise((r) => setTimeout(r, ms));

function fail(status, title, code, detail, errors) {
  throw new ApiError({ status, title, code, detail, errors });
}
function paged(items, query) {
  const page = Math.max(1, Number(query?.page) || 1);
  const pageSize = Math.max(1, Number(query?.pageSize) || 25);
  const totalCount = items.length;
  return { items: items.slice((page - 1) * pageSize, page * pageSize), page, pageSize, totalCount, totalPages: Math.ceil(totalCount / pageSize) };
}
const me = () => getStoredUser() || db.users[0];
const isDispatcher = () => (me().roles || []).includes(fx.DISPATCHER_ROLE);

function publicUser(u) {
  const { canSignIn, ...rest } = u; // eslint-disable-line no-unused-vars
  return rest;
}
function tokensFor(u) {
  const n = ++seq;
  return {
    accessToken: `demo-access-${u.id}-${n}`,
    tokenType: 'Bearer',
    expiresAtUtc: new Date(Date.now() + 3600000).toISOString(),
    refreshToken: `demo-refresh-${u.id}-${n}`,
    refreshTokenExpiresAtUtc: new Date(Date.now() + 7 * 86400000).toISOString(),
    user: publicUser(u),
  };
}
function validRoles(roles) {
  const known = fx.roles.map((r) => r.roleName);
  const list = Array.isArray(roles) ? roles.filter((r) => known.includes(r)) : [];
  if (list.length === 0) fail(400, 'Validation failed', 'validation_failed', 'At least one role is required.', { roles: ['At least one valid role is required.'] });
  return Array.from(new Set(list));
}

/** RBAC v2: endpoints a technician may call (everything else is 403). */
const TECHNICIAN_ENDPOINTS = [
  ['*', /^\/auth\//],
  ['GET', /^\/dashboards\/technician$/],
  ['GET', /^\/work-orders$/],
  ['GET', /^\/work-orders\/[^/]+$/],
  ['POST', /^\/work-orders\/[^/]+\/(start-inspection|inspection|resolve)$/],
  ['GET', /^\/maintenance-action-codes$/],
  ['POST', /^\/maintenance-logs$/],
];
/** RBAC v2: endpoints a data engineer may call (everything else is 403). */
const DATA_ENGINEER_ENDPOINTS = [
  ['GET', /^\/dashboards\/data-engineer$/],
  ['GET', /^\/aircraft$/],
  ['GET', /^\/aircraft\/fleet-groups$/],
  ['GET', /^\/telemetry(\/series|\/data-quality)?$/],
  ['GET', /^\/telemetry\/imports(\/[^/]+(\/rejected-rows)?)?$/],
  ['POST', /^\/telemetry\/(imports|raw-uploads)$/],
  ['POST', /^\/telemetry\/imports\/[^/]+\/retry$/],
  ['GET', /^\/pipelines\//],
  ['POST', /^\/pipelines\/(jobs|runs\/[^/]+\/cancel)$/],
  ['GET', /^\/models(\/[^/]+(\/evaluation)?)?$/],
  ['POST', /^\/models\/[^/]+\/(register|activate|retire)$/],
  ['GET', /^\/anomalies(\/summary|\/[^/]+)?$/],
  ['GET', /^\/predictions(\/metadata|\/aircraft\/[^/]+)?$/],
  ['GET', /^\/reports\/(data-quality|model-performance)$/],
];
const ROLE_ENDPOINTS = { [fx.TECHNICIAN_ROLE]: TECHNICIAN_ENDPOINTS, [fx.DATA_ENGINEER_ROLE]: DATA_ENGINEER_ENDPOINTS };
function permitted(method, path) {
  if (path.startsWith('/auth/') || isDispatcher()) return true;
  return (me().roles || []).some((r) => (ROLE_ENDPOINTS[r] || []).some(([m, re]) => (m === '*' || m === method) && re.test(path)));
}
function publicImport(imp) {
  const { _polls, _fail, ...rest } = imp; // eslint-disable-line no-unused-vars
  return rest;
}

function audit(action, entityType, entityId, details) {
  db.audit.unshift({ id: ++seq, userId: me().id, userName: me().displayName, action, entityType, entityId, details: details || null, occurredAtUtc: nowIso(), correlationId: null });
}

function riskFor(aircraftId) {
  const p = db.predictions.find((x) => x.aircraftId === aircraftId);
  return p ? { riskCategory: p.riskCategory, basis: p.basis, predictionTimestampUtc: p.predictionTimestampUtc, horizonDays: p.horizonDays } : null;
}
function summary(a) {
  return {
    ...a,
    openAnomalyCount: db.anomalies.filter((x) => x.aircraftId === a.id && ['New', 'Acknowledged', 'Investigating'].includes(x.investigationStatus)).length,
    activeInspectionFlagCount: db.flags.filter((f) => f.aircraftId === a.id && f.isActive).length,
    openWorkOrderCount: db.workOrders.filter((w) => w.aircraftId === a.id && !['Closed', 'Cancelled'].includes(w.status)).length,
    latestRisk: riskFor(a.id),
  };
}
function advisory(a) {
  const flags = db.flags.filter((f) => f.aircraftId === a.id && f.isActive);
  const reasons = flags.map((f) => `${f.severity} flag: ${f.reason}`);
  if (a.operationalStatus !== 'Available') reasons.unshift(`Operational status is ${a.operationalStatus}${a.statusReason ? ` (${a.statusReason})` : ''}.`);
  const readiness = a.operationalStatus !== 'Available' || flags.some((f) => f.severity === 'Grounding') ? 'NotAvailable' : flags.length ? 'Restricted' : 'NoKnownRestrictions';
  return { readiness, reasons, disclaimer: fx.DISCLAIMER };
}
function withTransitions(w) {
  const t = { Open: ['assign', 'cancel'], Assigned: ['assign', 'start-inspection', 'cancel'], InInspection: ['inspection', 'resolve'], Resolved: ['reopen', 'close'] };
  const techActions = ['start-inspection', 'inspection', 'resolve'];
  const dispatcherActions = ['assign', 'reopen', 'close', 'cancel'];
  // Filtered by the caller's role, like the API (a user holding both roles gets both sets).
  const roles = me().roles || [];
  const mine = w.assignedTechnicianId === me().id;
  const allowed = (t[w.status] || []).filter(
    (a) => (roles.includes(fx.DISPATCHER_ROLE) && dispatcherActions.includes(a)) || (roles.includes(fx.TECHNICIAN_ROLE) && mine && techActions.includes(a)),
  );
  return { ...w, allowedTransitions: allowed };
}
function detail(a) {
  return {
    ...summary(a),
    dispatchAdvisory: advisory(a),
    activeInspectionFlags: db.flags.filter((f) => f.aircraftId === a.id && f.isActive),
    recentMaintenance: db.logs.filter((m) => m.aircraftId === a.id).slice(0, 5),
    recentAnomalies: db.anomalies.filter((x) => x.aircraftId === a.id).slice(0, 10),
    latestPredictions: db.predictions.filter((p) => p.aircraftId === a.id),
    openWorkOrders: db.workOrders.filter((w) => w.aircraftId === a.id && !['Closed', 'Cancelled'].includes(w.status)).map(withTransitions),
  };
}
const findOr404 = (list, id, what = 'Item') => list.find((x) => String(x.id) === String(id)) || fail(404, `${what} not found`, 'not_found');

function fleetCounts() {
  const c = { total: db.aircraft.length, available: 0, underMaintenance: 0, grounded: 0 };
  db.aircraft.forEach((a) => {
    if (a.operationalStatus === 'Available') c.available++;
    else if (a.operationalStatus === 'Grounded') c.grounded++;
    else c.underMaintenance++;
  });
  return c;
}
function riskCounts() {
  return ['Low', 'Elevated', 'High', 'Undetermined'].map((r) => ({ riskCategory: r, count: db.predictions.filter((p) => p.riskCategory === r).length }));
}
function dataQuality() {
  const byAircraft = db.aircraft.map((a, i) => ({ aircraftId: a.id, registrationNumber: a.registrationNumber, records: 40000 + i * 1500, suspectRecords: 300 + i * 40, firstRecordUtc: new Date(Date.now() - 180 * 86400000).toISOString(), lastRecordUtc: a.lastTelemetryAtUtc }));
  const total = byAircraft.reduce((s, r) => s + r.records, 0);
  const suspect = byAircraft.reduce((s, r) => s + r.suspectRecords, 0);
  const done = db.imports.filter((i) => i.status === 'Completed');
  return {
    totalRecords: total,
    validRecords: total - suspect,
    suspectRecords: suspect,
    missingTemperature: Math.round(suspect * 0.4),
    missingPressure: Math.round(suspect * 0.6),
    firstRecordUtc: byAircraft[0].firstRecordUtc,
    lastRecordUtc: new Date().toISOString(),
    byAircraft,
    imports: {
      count: db.imports.length,
      acceptedRows: done.reduce((s, i) => s + i.acceptedRows, 0),
      rejectedRows: done.reduce((s, i) => s + i.rejectedRows, 0),
      duplicateRows: done.reduce((s, i) => s + i.duplicateRows, 0),
    },
  };
}
function monthly() {
  const out = [];
  const d = new Date();
  for (let i = 5; i >= 0; i--) {
    const m = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() - i, 1));
    out.push({ month: m.toISOString().slice(0, 7), scheduled: 8 + ((i * 3) % 5), unscheduled: 2 + (i % 3), inspections: 5 + ((i * 2) % 4), workOrdersOpened: 9 + i, workOrdersClosed: 8 + i, meanResolutionHours: 30 + i * 2 });
  }
  return out;
}
function anomalyByDay(days = 30) {
  const out = [];
  for (let i = days - 1; i >= 0; i--) {
    const date = new Date(Date.now() - i * 86400000).toISOString().slice(0, 10);
    out.push({ date, count: db.anomalies.filter((a) => a.observedAtUtc.slice(0, 10) === date).length });
  }
  return out;
}
function toCsv(rows) {
  if (!rows.length) return '';
  const keys = Object.keys(rows[0]);
  const esc = (v) => {
    const s = v === null || v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v);
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  return [keys.join(','), ...rows.map((r) => keys.map((k) => esc(r[k])).join(','))].join('\n');
}

const reports = {
  'fleet-health': () => ({
    aircraft: db.aircraft.map((a) => {
      const s = summary(a);
      return { registrationNumber: a.registrationNumber, model: a.model, fleetGroup: a.fleetGroup, operationalStatus: a.operationalStatus, riskCategory: s.latestRisk?.riskCategory || 'Undetermined', openAnomalies: s.openAnomalyCount, openWorkOrders: s.openWorkOrderCount };
    }),
    totals: { ...fleetCounts(), openWorkOrders: db.workOrders.filter((w) => !['Closed', 'Cancelled'].includes(w.status)).length },
  }),
  'maintenance-history': () => ({ months: monthly() }),
  'data-quality': () => dataQuality(),
  'model-performance': () => ({
    models: db.models.map((m) => ({ modelName: m.modelName, version: m.version, modelType: m.modelType, status: m.status, metrics: m.metrics })),
    anomalyOutcomes: Object.fromEntries(['New', 'Acknowledged', 'Investigating', 'Dismissed', 'ConfirmedIssue'].map((s) => [s, db.anomalies.filter((a) => a.investigationStatus === s).length])),
    predictionOutcomes: Object.fromEntries(['Unknown', 'UnscheduledMaintenance', 'NoUnscheduledMaintenance'].map((s) => [s, db.predictions.filter((p) => p.actualOutcome === s).length])),
  }),
};

function filterBy(list, query, map) {
  return list.filter((x) => Object.entries(map).every(([q, f]) => !query?.[q] || f(x, query[q])));
}

/** Route table: [method, pattern, handler(params, query, body)] */
const handlers = [
  ['POST', '/auth/login', (p, q, b) => {
    const u = db.users.find((x) => x.username === String(b.username || '').trim().toLowerCase());
    if (!u || (!u.canSignIn && u.status === 'Active')) fail(401, 'Invalid credentials', 'invalid_credentials', 'Demo accounts: username "dispatcher", "technician" or "dataengineer" (any password).');
    if (u.status === 'Pending') fail(403, 'Account pending approval', 'account_pending');
    if (u.status === 'Disabled') fail(403, 'Account disabled', 'account_disabled');
    return tokensFor(u);
  }],
  ['POST', '/auth/refresh', (p, q, b) => {
    const m = /^demo-refresh-(\d+)-/.exec(String(b.refreshToken || ''));
    const u = m && db.users.find((x) => x.id === Number(m[1]));
    if (!u) fail(401, 'Invalid refresh token', 'invalid_refresh_token');
    return tokensFor(u);
  }],
  ['POST', '/auth/register', () => ({ message: 'If the details are valid, your registration has been received and is awaiting administrator approval.' })],
  ['POST', '/auth/forgot-password', () => ({ message: 'If an account exists, a reset link has been sent.' })],
  ['POST', '/auth/reset-password', () => null],
  ['POST', '/auth/logout', () => null],
  ['GET', '/auth/me', () => publicUser(db.users.find((u) => u.id === me().id) || me())],

  ['GET', '/admin/roles', () => fx.roles],
  ['GET', '/admin/users', (p, q) =>
    paged(filterBy(db.users, q, { status: (u, v) => u.status === v, role: (u, v) => u.roles.includes(v), search: (u, v) => (u.username + u.email + u.displayName).toLowerCase().includes(v.toLowerCase()) }).map(publicUser), q)],
  ['POST', '/admin/users/:id/approve', (p, q, b) => publicUser(Object.assign(findOr404(db.users, p.id, 'User'), { status: 'Active', roles: validRoles(b.roles), approvedAtUtc: nowIso() }))],
  ['PUT', '/admin/users/:id/roles', (p, q, b) => publicUser(Object.assign(findOr404(db.users, p.id, 'User'), { roles: validRoles(b.roles) }))],
  ['POST', '/admin/users/:id/disable', (p, q, b) => {
    if (Number(p.id) === me().id) fail(400, 'You cannot disable your own account', 'business_rule_violation');
    return Object.assign(findOr404(db.users, p.id, 'User'), { status: 'Disabled', disabledReason: b.reason });
  }],
  ['POST', '/admin/users/:id/enable', (p) => Object.assign(findOr404(db.users, p.id, 'User'), { status: 'Active', disabledReason: null })],

  ['GET', '/dashboards/dispatcher', () => ({
    fleetCounts: fleetCounts(),
    readiness: db.aircraft.map((a) => {
      const s = summary(a);
      const adv = advisory(a);
      return { aircraftId: a.id, registrationNumber: a.registrationNumber, model: a.model, fleetGroup: a.fleetGroup, operationalStatus: a.operationalStatus, dispatchReadiness: adv.readiness, restrictions: adv.reasons, latestRiskCategory: s.latestRisk?.riskCategory || null, openAnomalyCount: s.openAnomalyCount, lastTelemetryAtUtc: a.lastTelemetryAtUtc };
    }),
    riskOverview: riskCounts(),
    recentAlerts: db.anomalies.slice(0, 8),
    activeRestrictions: db.flags.filter((f) => f.isActive),
  })],
  ['GET', '/dashboards/technician', () => {
    const own = db.workOrders.filter((w) => w.assignedTechnicianId === me().id);
    const active = own.filter((w) => ['Assigned', 'InInspection'].includes(w.status));
    return {
      counts: {
        assignedOpen: active.length,
        inInspection: own.filter((w) => w.status === 'InInspection').length,
        overdue: active.filter((w) => w.isOverdue).length,
        awaitingClosure: own.filter((w) => w.status === 'Resolved').length,
      },
      assignedWorkOrders: active.map(withTransitions),
      recentlyResolved: own.filter((w) => ['Resolved', 'Closed'].includes(w.status)).map(withTransitions),
    };
  }],
  ['GET', '/dashboards/data-engineer', () => {
    const dq = dataQuality();
    return {
      dataQuality: { totalRecords: dq.totalRecords, validRecords: dq.validRecords, suspectRecords: dq.suspectRecords, validPercent: (dq.validRecords / dq.totalRecords) * 100 },
      latestImports: db.imports.slice(0, 5),
      recentRuns: db.runs.slice(0, 6),
      latestAudit: db.runs.find((r) => r.jobType === 'DatasetAudit' && r.status === 'Succeeded') || null,
      latestScoring: db.runs.find((r) => r.jobType === 'Score') || null,
      featurePreparation: { status: 'Ready', lastPreparedAtUtc: db.runs[0].completedAtUtc, featureNames: db.models[1].featureNames },
      models: db.models,
      failurePredictionStatus: fx.failurePredictionStatus,
    };
  }],
  ['GET', '/dashboards/leadership', () => ({
    fleetCounts: fleetCounts(),
    riskDistribution: riskCounts(),
    maintenanceTrend: monthly(),
    unscheduled: { last30Days: 3, previous30Days: 5, last90Days: 11 },
    anomalyTrend: anomalyByDay(30),
    openWorkOrders: db.workOrders.filter((w) => !['Closed', 'Cancelled'].includes(w.status)).length,
    modelSummaries: db.models,
    failurePredictionStatus: fx.failurePredictionStatus,
  })],

  ['GET', '/aircraft/fleet-groups', () => Array.from(new Set(db.aircraft.map((a) => a.fleetGroup)))],
  ['GET', '/aircraft', (p, q) =>
    paged(filterBy(db.aircraft, q, { search: (a, v) => (a.registrationNumber + a.model + a.manufacturer).toLowerCase().includes(v.toLowerCase()), status: (a, v) => a.operationalStatus === v, fleetGroup: (a, v) => a.fleetGroup === v, model: (a, v) => a.model === v }).map(summary), q)],
  ['POST', '/aircraft', (p, q, b) => {
    if (db.aircraft.some((a) => a.registrationNumber === b.registrationNumber)) fail(409, 'Duplicate registration number', 'conflict');
    const a = { id: `ac-${++seq}`, ...b, serialNumber: b.serialNumber || null, operationalStatus: 'Available', statusReason: null, statusChangedAtUtc: nowIso(), createdAtUtc: nowIso(), lastTelemetryAtUtc: null, lastInspectionAtUtc: null };
    db.aircraft.push(a);
    return detail(a);
  }],
  ['GET', '/aircraft/:id', (p) => detail(findOr404(db.aircraft, p.id, 'Aircraft'))],
  ['PUT', '/aircraft/:id', (p, q, b) => detail(Object.assign(findOr404(db.aircraft, p.id, 'Aircraft'), b))],
  ['POST', '/aircraft/:id/status', (p, q, b) => {
    const a = findOr404(db.aircraft, p.id, 'Aircraft');
    if (b.status === 'Available') {
      if (db.flags.some((f) => f.aircraftId === a.id && f.isActive && f.severity === 'Grounding'))
        fail(400, 'Status change refused', 'business_rule_violation', 'An active Grounding inspection flag must be cleared before the aircraft can be set to Available.');
      if (db.workOrders.some((w) => w.aircraftId === a.id && w.priority === 'Critical' && !['Closed', 'Cancelled'].includes(w.status)))
        fail(400, 'Status change refused', 'business_rule_violation', 'A Critical work order is still open for this aircraft.');
    }
    Object.assign(a, { operationalStatus: b.status, statusReason: b.reason, statusChangedAtUtc: nowIso() });
    audit('AircraftStatusChanged', 'Aircraft', a.id, { status: b.status, reason: b.reason });
    return detail(a);
  }],
  ['GET', '/aircraft/:id/maintenance-history', (p, q) => paged(db.logs.filter((m) => m.aircraftId === p.id), q)],
  ['GET', '/aircraft/:id/inspection-flags', (p) => db.flags.filter((f) => f.aircraftId === p.id && f.isActive)],
  ['POST', '/aircraft/:id/inspection-flags', (p, q, b) => {
    const a = findOr404(db.aircraft, p.id, 'Aircraft');
    const f = { id: `fl-${++seq}`, aircraftId: a.id, registrationNumber: a.registrationNumber, raisedBy: me().displayName, raisedAtUtc: nowIso(), isActive: true, clearedBy: null, clearedAtUtc: null, clearanceNotes: null, anomalyId: null, predictionId: null, workOrderId: null, ...b };
    db.flags.push(f);
    if (b.severity === 'Grounding') Object.assign(a, { operationalStatus: 'Grounded', statusReason: b.reason, statusChangedAtUtc: nowIso() });
    return f;
  }],
  ['POST', '/inspection-flags/:id/clear', (p, q, b) => Object.assign(findOr404(db.flags, p.id, 'Flag'), { isActive: false, clearedBy: me().displayName, clearedAtUtc: nowIso(), clearanceNotes: b.clearanceNotes })],

  ['GET', '/telemetry/series', (p, q) => {
    const from = q.fromUtc ? Date.parse(q.fromUtc) : Date.now() - 7 * 86400000;
    const to = q.toUtc ? Math.min(Date.parse(q.toUtc), Date.now()) : Date.now();
    const bucket = Number(q.bucketMinutes) || 60;
    return { aircraftId: q.aircraftId, fromUtc: new Date(from).toISOString(), toUtc: new Date(to).toISOString(), bucketMinutes: bucket, points: fx.seriesFor(q.aircraftId, from, to, bucket) };
  }],
  ['GET', '/telemetry/data-quality', () => dataQuality()],
  ['GET', '/telemetry/imports', (p, q) => paged(db.imports.map(publicImport), q)],
  ['POST', '/telemetry/imports', (p, q, b, formData) => {
    const file = formData?.get?.('file');
    const imp = { id: `im-${++seq}`, source: 'Csv', fileName: file?.name || 'upload.csv', fileSha256: 'demo'.padEnd(64, String(seq % 10)), status: 'Queued', totalRows: 0, acceptedRows: 0, rejectedRows: 0, duplicateRows: 0, warningCount: 0, warnings: [], errorMessage: null, requestedBy: me().displayName, requestedAtUtc: nowIso(), startedAtUtc: null, completedAtUtc: null, _polls: 0 };
    db.imports.unshift(imp);
    return { id: imp.id, status: 'Queued', statusUrl: `/api/v1/telemetry/imports/${imp.id}` };
  }],
  ['POST', '/telemetry/raw-uploads', (p, q, b, formData) => {
    const file = formData?.get?.('file');
    const name = file?.name || 'upload.json';
    if (!/\.json$/i.test(name)) fail(400, 'Validation failed', 'validation_failed', 'The uploaded file is not valid.', { file: ['Only .json files are accepted.'] });
    if (db.imports.some((i) => i.source === 'RawJson' && i.fileName === name)) fail(409, 'Duplicate file', 'conflict', 'This exact file has already been uploaded.');
    // Demo convention: a file name containing "fail" ends in Failed, so the Retry path can be tried.
    const imp = { id: `im-${++seq}`, source: 'RawJson', fileName: name, fileSha256: 'demoraw'.padEnd(64, String(seq % 10)), status: 'Queued', totalRows: 0, acceptedRows: 0, rejectedRows: 0, duplicateRows: 0, warningCount: 0, warnings: [], errorMessage: null, requestedBy: me().displayName, requestedAtUtc: nowIso(), startedAtUtc: null, completedAtUtc: null, _polls: 0, _fail: /fail/i.test(name) };
    db.imports.unshift(imp);
    return { id: imp.id, status: 'Queued', statusUrl: `/api/v1/telemetry/imports/${imp.id}` };
  }],
  ['POST', '/telemetry/imports/:id/retry', (p) => {
    const imp = findOr404(db.imports, p.id, 'Import');
    if (imp.source !== 'RawJson' || imp.status !== 'Failed') fail(400, 'Retry not allowed', 'business_rule_violation', 'Only failed raw JSON imports can be retried.');
    Object.assign(imp, { status: 'Queued', errorMessage: null, startedAtUtc: null, completedAtUtc: null, _polls: 0, _fail: false });
    return publicImport(imp);
  }],
  ['GET', '/telemetry/imports/:id', (p) => {
    const imp = findOr404(db.imports, p.id, 'Import');
    if (imp._polls !== undefined && !['Completed', 'Failed'].includes(imp.status)) {
      imp._polls++;
      if (imp._polls === 1) Object.assign(imp, { status: 'Processing', startedAtUtc: nowIso() });
      if (imp._polls >= 3 && imp._fail) Object.assign(imp, { status: 'Failed', errorMessage: 'ETL job failed: 3 records could not be parsed (demo).', completedAtUtc: nowIso() });
      else if (imp._polls >= 3) Object.assign(imp, { status: 'Completed', totalRows: 1200, acceptedRows: 1168, rejectedRows: 20, duplicateRows: 12, warningCount: 1, warnings: ['8 rows were missing pressure and were stored as Suspect.'], completedAtUtc: nowIso() });
    }
    return publicImport(imp);
  }],
  ['GET', '/telemetry/imports/:id/rejected-rows', (p, q) =>
    paged(Array.from({ length: findOr404(db.imports, p.id, 'Import').rejectedRows > 0 ? 20 : 0 }, (_, i) => ({ rowNumber: 10 + i * 37, rawLine: `DEMO-Z99,2026-05-01T10:${String(i).padStart(2, '0')}:00Z,101.2,C,301.5,kPa`, reasons: i % 2 ? 'UNKNOWN_AIRCRAFT' : 'MISSING_UNIT' })), q)],
  ['GET', '/telemetry', (p, q) => {
    const from = q.fromUtc ? Date.parse(q.fromUtc) : Date.now() - 7 * 86400000;
    const to = q.toUtc ? Math.min(Date.parse(q.toUtc), Date.now()) : Date.now();
    const pts = fx.seriesFor(q.aircraftId, from, to, 30).reverse();
    const rows = pts.map((pt, i) => ({ id: i + 1, aircraftId: q.aircraftId, sensorId: 'ENG1-OIL', recordedAtUtc: pt.timestampUtc, temperatureValue: pt.temperatureC, temperatureUnit: pt.temperatureC === null ? null : 'C', temperatureC: pt.temperatureC, pressureValue: pt.pressureKpa, pressureUnit: 'kPa', pressureKpa: pt.pressureKpa, dataSource: 'demo', qualityStatus: pt.temperatureC === null ? 'Suspect' : 'Valid', qualityFlags: pt.temperatureC === null ? ['MISSING_TEMPERATURE'] : [], ingestedAtUtc: pt.timestampUtc, importId: i % 2 ? 'im-raw-2' : 'im-5', sourceRecordId: i % 2 ? `demo-src-${q.aircraftId}-${i}` : null, vibrationIndex: i % 2 ? Number((0.2 + ((i * 37) % 60) / 100).toFixed(3)) : null, sourceStatusFlag: i % 2 ? (i % 11 === 1 ? 'WARN' : 'OK') : null }));
    return paged(filterBy(rows, q, { qualityStatus: (r, v) => r.qualityStatus === v }), q);
  }],

  ['GET', '/anomalies/summary', () => ({
    total: db.anomalies.length,
    open: db.anomalies.filter((a) => ['New', 'Acknowledged', 'Investigating'].includes(a.investigationStatus)).length,
    bySeverity: { Low: db.anomalies.filter((a) => a.severity === 'Low').length, Medium: db.anomalies.filter((a) => a.severity === 'Medium').length, High: db.anomalies.filter((a) => a.severity === 'High').length },
    byDay: anomalyByDay(30),
  })],
  ['GET', '/anomalies', (p, q) => paged(filterBy(db.anomalies, q, { aircraftId: (a, v) => a.aircraftId === v, status: (a, v) => a.investigationStatus === v, severity: (a, v) => a.severity === v, fromUtc: (a, v) => a.observedAtUtc >= v, toUtc: (a, v) => a.observedAtUtc <= v }), q)],
  ['GET', '/anomalies/:id', (p) => {
    const a = findOr404(db.anomalies, p.id, 'Anomaly');
    return { ...a, telemetry: { id: a.telemetryRecordId, aircraftId: a.aircraftId, sensorId: a.sensorId, recordedAtUtc: a.observedAtUtc, temperatureValue: a.featureValues.temperature_c, temperatureUnit: 'C', temperatureC: a.featureValues.temperature_c, pressureValue: a.featureValues.pressure_kpa, pressureUnit: 'kPa', pressureKpa: a.featureValues.pressure_kpa, dataSource: 'demo', qualityStatus: 'Valid', qualityFlags: [], ingestedAtUtc: a.observedAtUtc, importId: null } };
  }],
  ['POST', '/anomalies/:id/acknowledge', (p, q, b) => {
    const a = findOr404(db.anomalies, p.id, 'Anomaly');
    if (a.investigationStatus !== 'New') fail(400, 'Invalid transition', 'invalid_transition', 'Only New anomalies can be acknowledged.');
    return Object.assign(a, { investigationStatus: 'Acknowledged', acknowledgedBy: me().displayName, acknowledgedAtUtc: nowIso(), investigationNotes: b.notes || a.investigationNotes });
  }],
  ['POST', '/anomalies/:id/investigation', (p, q, b) => Object.assign(findOr404(db.anomalies, p.id, 'Anomaly'), { investigationStatus: b.status, investigationNotes: b.notes })],

  ['GET', '/predictions/metadata', () => ({
    configuredHorizonDays: 30,
    failurePredictionStatus: fx.failurePredictionStatus,
    activeAnomalyModel: db.models.find((m) => m.status === 'Active' && m.modelType === 'AnomalyDetection') || null,
    activeFailureModel: null,
    riskCategories: [
      { code: 'Low', description: 'No recent anomaly or maintenance signals above baseline.', displayOrder: 1 },
      { code: 'Elevated', description: 'Recent anomalies or suspect data warrant closer monitoring.', displayOrder: 2 },
      { code: 'High', description: 'Multiple high-severity signals; prioritise inspection.', displayOrder: 3 },
      { code: 'Undetermined', description: 'Insufficient recent data to form an indicator.', displayOrder: 4 },
    ],
  })],
  ['GET', '/predictions/aircraft/:id', (p) => db.predictions.filter((x) => x.aircraftId === p.id)],
  ['GET', '/predictions', (p, q) => paged(filterBy(db.predictions, q, { aircraftId: (x, v) => x.aircraftId === v, riskCategory: (x, v) => x.riskCategory === v, basis: (x, v) => x.basis === v, approvalStatus: (x, v) => x.approvalStatus === v }), q)],
  ['POST', '/predictions/:id/review', (p, q, b) => Object.assign(findOr404(db.predictions, p.id, 'Prediction'), { approvalStatus: b.approvalStatus, reviewNotes: b.notes, reviewedBy: me().displayName, reviewedAtUtc: nowIso() })],
  ['POST', '/predictions/:id/outcome', (p, q, b) => Object.assign(findOr404(db.predictions, p.id, 'Prediction'), { actualOutcome: b.actualOutcome, outcomeRecordedAtUtc: nowIso() })],

  ['GET', '/technicians', () =>
    db.users.filter((u) => u.roles.includes(fx.TECHNICIAN_ROLE) && u.status === 'Active').map((u) => ({ id: u.id, displayName: u.displayName, username: u.username }))],
  ['GET', '/maintenance-action-codes', () => fx.maintenanceActionCodes],
  ['GET', '/work-orders', (p, q) =>
    paged(
      filterBy(db.workOrders, q, { aircraftId: (w, v) => w.aircraftId === v, status: (w, v) => w.status === v, priority: (w, v) => w.priority === v, assignedToMe: (w, v) => v !== 'true' || w.assignedTechnicianId === me().id })
        .filter((w) => isDispatcher() || w.assignedTechnicianId === me().id)
        .map(withTransitions),
      q,
    )],
  ['POST', '/work-orders', (p, q, b) => {
    const a = findOr404(db.aircraft, b.aircraftId, 'Aircraft');
    const tech = b.assignedTechnicianId ? db.users.find((u) => u.id === Number(b.assignedTechnicianId)) : null;
    const w = { id: `wo-${++seq}`, workOrderNumber: `WO-2026-${String(seq).padStart(6, '0')}`, registrationNumber: a.registrationNumber, componentId: null, anomalyId: null, predictionId: null, dueAtUtc: null, ...b, status: tech ? 'Assigned' : 'Open', assignedTechnicianName: tech?.displayName || null, assignedTechnicianId: tech?.id || null, createdBy: me().displayName, createdAtUtc: nowIso(), isOverdue: false, inspectionFindings: null, correctiveAction: null, actualFailureConfirmed: null, resolutionNotes: null, resolvedAtUtc: null, closedAtUtc: null, updatedAtUtc: nowIso(), concurrencyStamp: `stamp-${seq}`, history: [] };
    db.workOrders.unshift(w);
    w.history.push({ id: ++seq, userId: me().id, userName: me().displayName, action: 'WorkOrderCreated', entityType: 'WorkOrder', entityId: w.id, details: { priority: w.priority }, occurredAtUtc: nowIso(), correlationId: null });
    return withTransitions(w);
  }],
  ['GET', '/work-orders/:id', (p) => {
    const w = findOr404(db.workOrders, p.id, 'Work order');
    if (!isDispatcher() && w.assignedTechnicianId !== me().id) fail(404, 'Work order not found', 'not_found');
    const a = db.aircraft.find((x) => x.id === w.aircraftId);
    return {
      ...withTransitions(w),
      aircraft: a ? summary(a) : null,
      anomaly: w.anomalyId ? db.anomalies.find((x) => x.id === w.anomalyId) || null : null,
      prediction: db.predictions.find((x) => x.id === w.predictionId) || db.predictions.find((x) => x.aircraftId === w.aircraftId) || null,
    };
  }],
  ['PATCH', '/work-orders/:id', (p, q, b) => {
    const w = findOr404(db.workOrders, p.id, 'Work order');
    if (b.concurrencyStamp !== w.concurrencyStamp) fail(409, 'Concurrency conflict', 'concurrency_conflict', 'The work order was modified by another user.');
    const { concurrencyStamp, ...rest } = b; // eslint-disable-line no-unused-vars
    Object.assign(w, rest, { updatedAtUtc: nowIso(), concurrencyStamp: `stamp-${++seq}` });
    return withTransitions(w);
  }],
  ['POST', '/work-orders/:id/:action', (p, q, b) => {
    const w = findOr404(db.workOrders, p.id, 'Work order');
    const t = withTransitions(w).allowedTransitions;
    if (!t.includes(p.action)) fail(400, 'Invalid transition', 'invalid_transition', `Cannot ${p.action} a work order that is ${w.status}.`);
    const patch = {
      assign: () => {
        const tech = db.users.find((u) => u.id === Number(b.technicianId));
        return { status: 'Assigned', assignedTechnicianId: tech?.id, assignedTechnicianName: tech?.displayName };
      },
      'start-inspection': () => ({ status: 'InInspection' }),
      inspection: () => ({ inspectionFindings: b.inspectionFindings }),
      resolve: () => {
        if (!w.inspectionFindings) fail(400, 'Inspection findings required', 'business_rule_violation', 'Record inspection findings before resolving.');
        return { status: 'Resolved', correctiveAction: b.correctiveAction, actualFailureConfirmed: b.actualFailureConfirmed, resolutionNotes: b.resolutionNotes, resolvedAtUtc: nowIso() };
      },
      reopen: () => ({ status: 'Assigned', resolvedAtUtc: null }),
      close: () => ({ status: 'Closed', closedAtUtc: nowIso(), dispatcherReviewNotes: b.resolutionNotes || null }),
      cancel: () => ({ status: 'Cancelled' }),
    }[p.action]();
    Object.assign(w, patch, { updatedAtUtc: nowIso(), concurrencyStamp: `stamp-${++seq}` });
    w.history.push({ id: ++seq, userId: me().id, userName: me().displayName, action: `WorkOrder.${p.action}`, entityType: 'WorkOrder', entityId: w.id, details: b && Object.keys(b).length ? b : null, occurredAtUtc: nowIso(), correlationId: null });
    return withTransitions(w);
  }],
  ['POST', '/maintenance-logs', (p, q, b) => {
    const a = findOr404(db.aircraft, b.aircraftId, 'Aircraft');
    if (!isDispatcher() && !db.workOrders.some((w) => w.aircraftId === a.id && w.assignedTechnicianId === me().id && !['Closed', 'Cancelled'].includes(w.status)))
      fail(403, 'Forbidden', 'forbidden', 'You can only log maintenance for aircraft with an open work order assigned to you.');
    const errors = {};
    if (!['Scheduled', 'Unscheduled', 'Inspection'].includes(b.eventType)) errors.eventType = ['Event type is invalid.'];
    if (!b.occurredAtUtc || Number.isNaN(Date.parse(b.occurredAtUtc))) errors.occurredAtUtc = ['Occurred time is required.'];
    else if (Date.parse(b.occurredAtUtc) > Date.now() + 5 * 60000) errors.occurredAtUtc = ['Occurred time cannot be in the future.'];
    if (!b.description || !String(b.description).trim()) errors.description = ['Description is required.'];
    else if (b.description.length > 4000) errors.description = ['Description must be at most 4000 characters.'];
    if (Object.keys(errors).length) fail(400, 'Validation failed', 'validation_failed', 'One or more fields are invalid.', errors);
    const log = {
      id: `ml-${++seq}`,
      aircraftId: a.id,
      registrationNumber: a.registrationNumber,
      componentId: b.componentId || null,
      eventType: b.eventType,
      occurredAtUtc: b.occurredAtUtc,
      description: b.description,
      actualFailureConfirmed: b.actualFailureConfirmed ?? null,
      isVerified: !!b.isVerified,
      source: 'Manual',
      workOrderId: null,
    };
    db.logs.unshift(log);
    db.logs.sort((x, y) => Date.parse(y.occurredAtUtc) - Date.parse(x.occurredAtUtc));
    if (b.eventType === 'Inspection' && (!a.lastInspectionAtUtc || b.occurredAtUtc > a.lastInspectionAtUtc)) a.lastInspectionAtUtc = b.occurredAtUtc;
    audit('MaintenanceLogRecorded', 'MaintenanceLog', log.id, { eventType: log.eventType });
    return log;
  }],
  ['GET', '/maintenance-logs', (p, q) => paged(filterBy(db.logs, q, { aircraftId: (m, v) => m.aircraftId === v, eventType: (m, v) => m.eventType === v }), q)],

  ['GET', '/pipelines/dataset-audit', () => db.runs.find((r) => r.jobType === 'DatasetAudit' && r.status === 'Succeeded') || fail(404, 'No dataset audit', 'not_found')],
  ['GET', '/pipelines/runs', (p, q) => paged(filterBy(db.runs, q, { jobType: (r, v) => r.jobType === v, status: (r, v) => r.status === v }), q)],
  ['GET', '/pipelines/runs/:id', (p) => {
    const r = findOr404(db.runs, p.id, 'Run');
    if (r._polls !== undefined && ['Queued', 'Running'].includes(r.status)) {
      r._polls++;
      if (r._polls === 1) Object.assign(r, { status: 'Running', startedAtUtc: nowIso() });
      if (r._polls >= 3) {
        if (r.jobType === 'TrainFailure') Object.assign(r, { status: 'Blocked', errorMessage: 'Failure-prediction training is blocked: readiness requirements are not met.', resultSummary: { failure_prediction_readiness: fx.auditReadiness }, completedAtUtc: nowIso() });
        else Object.assign(r, { status: 'Succeeded', recordsProcessed: 1000, resultSummary: { demo: true, recordsProcessed: 1000 }, completedAtUtc: nowIso() });
      }
    }
    const { _polls, ...rest } = r; // eslint-disable-line no-unused-vars
    return rest;
  }],
  ['POST', '/pipelines/jobs', (p, q, b) => {
    if (db.runs.some((r) => r.jobType === b.jobType && ['Queued', 'Running'].includes(r.status))) fail(409, 'Job already queued', 'conflict', `A ${b.jobType} run is already queued or running.`);
    const r = { id: `run-${++seq}`, jobType: b.jobType, status: 'Queued', trigger: 'Api', requestedBy: me().displayName, requestedAtUtc: nowIso(), startedAtUtc: null, completedAtUtc: null, parameters: b.parameters || {}, resultSummary: null, errorMessage: null, modelVersionId: null, recordsProcessed: null, _polls: 0 };
    db.runs.unshift(r);
    return { id: r.id, status: 'Queued', statusUrl: `/api/v1/pipelines/runs/${r.id}` };
  }],
  ['POST', '/pipelines/runs/:id/cancel', (p) => Object.assign(findOr404(db.runs, p.id, 'Run'), { status: 'Cancelled', completedAtUtc: nowIso() })],

  ['GET', '/models', (p, q) => filterBy(db.models, q, { modelType: (m, v) => m.modelType === v, status: (m, v) => m.status === v })],
  ['GET', '/models/:id/evaluation', (p) => {
    const m = findOr404(db.models, p.id, 'Model');
    return { modelVersionId: m.id, modelName: m.modelName, version: m.version, modelType: m.modelType, metrics: m.metrics, limitations: m.limitations, evaluationAvailable: false };
  }],
  ['GET', '/models/:id', (p) => findOr404(db.models, p.id, 'Model')],
  ['POST', '/models/:id/:action', (p) => {
    const m = findOr404(db.models, p.id, 'Model');
    const t = { register: ['Trained', 'Registered'], approve: ['Registered', 'Approved'], reject: [null, 'Rejected'], activate: ['Approved', 'Active'], retire: ['Active', 'Retired'] }[p.action];
    if (!t || (t[0] && m.status !== t[0])) fail(400, 'Invalid transition', 'invalid_transition', `Cannot ${p.action} a ${m.status} model.`);
    if (p.action === 'activate') db.models.forEach((x) => x.modelType === m.modelType && x.status === 'Active' && Object.assign(x, { status: 'Retired', retiredAtUtc: nowIso() }));
    return Object.assign(m, { status: t[1], [`${t[1].toLowerCase()}AtUtc`]: nowIso() });
  }],

  ['GET', '/reports/:name', (p, q) => {
    const fn = reports[p.name] || fail(404, 'Report not found', 'not_found');
    const data = fn();
    if (q.format === 'csv') {
      const rows = Array.isArray(data) ? data : Object.values(data).find(Array.isArray) || [data];
      return new Blob([toCsv(rows)], { type: 'text/csv' });
    }
    return data;
  }],
  ['GET', '/audit/actions', (p, q) => paged(db.audit.concat(db.workOrders.flatMap((w) => w.history)), q)],
  ['GET', '/audit/auth', (p, q) =>
    paged(db.users.filter((u) => u.lastLoginAtUtc).map((u, i) => ({ id: i + 1, userId: u.id, email: u.email, eventType: 'LoginSucceeded', succeeded: true, ipAddress: '192.0.2.10', userAgent: 'Demo', detail: null, occurredAtUtc: u.lastLoginAtUtc })), q)],
];

export async function demoRequest(method, path, { query = {}, body, formData } = {}) {
  await delay();
  const clean = path.replace(/^\/api\/v1/, '').split('?')[0];
  if (!permitted(method, clean)) {
    fail(403, 'Forbidden', 'forbidden', 'Your role does not permit this request.');
  }
  for (const [m, pattern, handler] of handlers) {
    if (m !== method) continue;
    const params = matchPath(pattern, clean);
    if (params) {
      const result = handler(params, query || {}, body || {}, formData);
      if (result === undefined || result === null) return null;
      if (result instanceof Blob) return result;
      return JSON.parse(JSON.stringify(result)); // return copies, like a real network response
    }
  }
  return fail(404, 'Not available in demo mode', 'not_found', `${method} ${clean} has no demo fixture.`);
}
