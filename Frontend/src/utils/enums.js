import { humanize } from './format.js';

export const AIRCRAFT_STATUS = ['Available', 'UnderMaintenance', 'Grounded'];
export const FLAG_SEVERITY = ['Advisory', 'Restrictive', 'Grounding'];
export const ANOMALY_SEVERITY = ['Low', 'Medium', 'High'];
export const ANOMALY_STATUS = ['New', 'Acknowledged', 'Investigating', 'Dismissed', 'ConfirmedIssue'];
export const RISK_CATEGORY = ['Low', 'Elevated', 'High', 'Critical', 'HighCritical', 'Undetermined'];
export const PREDICTION_BASIS = ['Model', 'DescriptiveIndicator'];
export const APPROVAL_STATUS = ['PendingReview', 'Approved', 'Rejected'];
export const ACTUAL_OUTCOME = ['Unknown', 'UnscheduledMaintenance', 'NoUnscheduledMaintenance'];
export const WO_PRIORITY = ['Low', 'Medium', 'High', 'Critical'];
export const WO_STATUS = ['Open', 'Assigned', 'InInspection', 'Resolved', 'Closed', 'Cancelled'];
/**
 * Display labels for WORK-ORDER statuses only (docs/API.md, Maintenance). The backend values above are
 * unchanged and are what every API call sends; other entities' "Completed"/"Cancelled"/... keep their
 * own labels, so these are applied only through workOrderStatusLabel / WorkOrderStatusBadge.
 */
export const WO_STATUS_LABELS = {
  Open: 'NEW',
  Assigned: 'ASSIGNED',
  InInspection: 'IN_PROGRESS',
  Resolved: 'REPORTED',
  Closed: 'COMPLETED',
  Cancelled: 'CANCELLED',
};
export const JOB_TYPES_STARTABLE = ['DatasetAudit', 'TrainAnomaly', 'Score', 'TrainFailure', 'TrainSensorForecast', 'ForecastSensors'];
/** Work-order source (backend WorkOrderSource): raised by anomaly processing or by a dispatcher. */
export const WO_SOURCE = ['Automatic', 'Manual'];
export const JOB_TYPES = ['DatasetAudit', 'TrainAnomaly', 'RegisterModel', 'Score', 'TrainFailure', 'TelemetryImport', 'ScoreCandidate', 'SimulateTelemetry', 'AnomalyProcessing', 'TrainSensorForecast', 'ForecastSensors'];
export const RUN_STATUS = ['Queued', 'Running', 'Succeeded', 'Failed', 'Blocked', 'Cancelled'];
/** Sensor-forecast horizons (backend SensorForecastHorizon) with their filter labels. */
export const SENSOR_FORECAST_HORIZON = ['OneDay', 'SevenDays', 'ThirtyDays'];
export const SENSOR_FORECAST_HORIZON_LABELS = { OneDay: '1 Day', SevenDays: '7 Days', ThirtyDays: '1 Month' };
/** The ETL's placeholder for an aircraft with no recorded fleet group - shown as no fleet group at all. */
export const UNASSIGNED_FLEET_GROUP = 'Unassigned';
export const MODEL_TYPES = ['AnomalyDetection', 'FailurePrediction', 'SensorForecast'];
export const MODEL_STATUS = ['Trained', 'Registered', 'Approved', 'Active', 'Retired', 'Rejected'];
export const USER_STATUS = ['Pending', 'Active', 'Disabled'];
export const QUALITY_STATUS = ['Valid', 'Suspect'];
export const MAINTENANCE_EVENT = ['Scheduled', 'Unscheduled', 'Inspection'];

/** [{ value, label }] for selects. */
export function options(values) {
  return values.map((v) => ({ value: v, label: humanize(v) }));
}

/** Work-order status display label; unknown values fall back to the generic humanized form. */
export function workOrderStatusLabel(value) {
  return WO_STATUS_LABELS[value] || humanize(value);
}

/** [{ value, label }] for work-order status selects: backend value, work-order display label. */
export function workOrderStatusOptions() {
  return WO_STATUS.map((v) => ({ value: v, label: workOrderStatusLabel(v) }));
}
