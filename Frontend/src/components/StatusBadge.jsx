import { humanize } from '../utils/format.js';
import { workOrderStatusLabel } from '../utils/enums.js';

/** Tone per enum value. Colour is never the only signal - every badge also renders its text label. */
const TONES = {
  // telemetry import source / prediction state
  RawJson: 'info',
  Scored: 'success',
  InsufficientTelemetry: 'warning',
  MissingFeatures: 'warning',
  // aircraft status / derived aircraft state
  Available: 'success',
  AwaitingRelease: 'info',
  UnderMaintenance: 'warning',
  Grounded: 'danger',
  // dispatch readiness
  NoKnownRestrictions: 'success',
  Restricted: 'warning',
  NotAvailable: 'danger',
  // user status
  Active: 'success',
  Pending: 'warning',
  Disabled: 'neutral',
  // work orders
  Open: 'info',
  Assigned: 'purple',
  InInspection: 'warning',
  Resolved: 'success',
  Closed: 'neutral',
  Cancelled: 'neutral',
  // anomalies
  New: 'info',
  Acknowledged: 'purple',
  Investigating: 'warning',
  Dismissed: 'neutral',
  ConfirmedIssue: 'danger',
  // predictions
  PendingReview: 'warning',
  Approved: 'success',
  Rejected: 'neutral',
  Unknown: 'neutral',
  UnscheduledMaintenance: 'danger',
  NoUnscheduledMaintenance: 'success',
  Model: 'info',
  DescriptiveIndicator: 'neutral',
  // pipelines / imports
  Queued: 'neutral',
  Processing: 'info',
  Running: 'info',
  Succeeded: 'success',
  Completed: 'success',
  Failed: 'danger',
  Blocked: 'danger',
  Ready: 'success',
  // models
  Trained: 'neutral',
  Registered: 'info',
  Retired: 'neutral',
  // telemetry
  Valid: 'success',
  Suspect: 'warning',
  // maintenance events
  Scheduled: 'info',
  Unscheduled: 'warning',
  Inspection: 'purple',
  // threshold-based aircraft health (client-defined thresholds, not a model prediction)
  WithinThresholds: 'success',
  ThresholdBreach: 'danger',
  NotAssessable: 'warning',
  // flags
  Advisory: 'info',
  Restrictive: 'warning',
  Grounding: 'danger',
};

const KIND_TONES = {
  risk: { Low: 'success', Elevated: 'warning', High: 'danger', Critical: 'warning', HighCritical: 'danger', Undetermined: 'neutral' },
  severity: { Low: 'neutral', Medium: 'warning', High: 'danger' },
  priority: { Low: 'neutral', Medium: 'info', High: 'warning', Critical: 'danger' },
  // BR-03 30-day failure-risk tiers (validated probabilities only). Not the descriptive Low/Elevated/High indicator.
  tier: { Low: 'success', Medium: 'warning', Critical: 'danger' },
  // work-order origin and the stored deterministic diagnosis
  woSource: { Automatic: 'purple', Manual: 'neutral' },
  diagnosis: { Generated: 'success', Failed: 'danger', NotGenerated: 'neutral' },
};

const DIAGNOSIS_LABELS = { Generated: 'Diagnosis ready', Failed: 'Diagnosis failed', NotGenerated: 'No diagnosis' };

export const STATUS_LABELS = {
  // derived aircraft state (alongside the unchanged operationalStatus)
  Available: 'Available',
  AwaitingRelease: 'Awaiting release',
  UnderMaintenance: 'Under maintenance',
  Grounded: 'Grounded',
  NoKnownRestrictions: 'No known restrictions',
  InInspection: 'In inspection',
  DescriptiveIndicator: 'Descriptive indicator',
  HighCritical: 'High Critical',
  UnscheduledMaintenance: 'Unscheduled maintenance',
  NoUnscheduledMaintenance: 'No unscheduled maintenance',
  Csv: 'CSV',
  RawJson: 'Raw JSON',
  InsufficientTelemetry: 'Insufficient telemetry',
  MissingFeatures: 'Missing features',
  WithinThresholds: 'Within client-defined thresholds',
  ThresholdBreach: 'Threshold breach',
  NotAssessable: 'Not assessable - no reading in window',
};

export function badgeTone(value, kind) {
  if (kind && KIND_TONES[kind]?.[value]) return KIND_TONES[kind][value];
  return TONES[value] || 'neutral';
}

export function StatusBadge({ value, kind, label, title, prefix }) {
  if (value === null || value === undefined || value === '') {
    return <span className="badge badge--neutral badge--plain">—</span>;
  }
  const tone = badgeTone(value, kind);
  const text = label || STATUS_LABELS[value] || humanize(value);
  return (
    <span className={`badge badge--${tone}`} title={title} data-tone={tone}>
      {prefix ? <span className="visually-hidden">{prefix}: </span> : null}
      {text}
    </span>
  );
}

/**
 * A WORK-ORDER status (Open/Assigned/InInspection/Resolved/Closed/Cancelled) with its work-order display
 * label (NEW, ASSIGNED, IN_PROGRESS, REPORTED, COMPLETED, CANCELLED). Scoped to work orders on purpose:
 * a plain StatusBadge for a pipeline run, import, model or user keeps its generic label.
 */
export function WorkOrderStatusBadge({ value, title }) {
  return <StatusBadge value={value} label={value ? workOrderStatusLabel(value) : undefined} title={title} prefix="Work order status" />;
}

/**
 * The backend's additive `derivedState` (Available / AwaitingRelease / UnderMaintenance / Grounded),
 * falling back to the unchanged `operationalStatus` on older payloads so the cell is never blank.
 * The derived state is preferred because an ETL auto-registered aircraft with no work in progress (a NEW or
 * ASSIGNED work order has not started maintenance) is
 * awaiting release, not actively under maintenance.
 */
export function AircraftStateBadge({ aircraft, title }) {
  return <StatusBadge value={aircraft?.derivedState || aircraft?.operationalStatus} title={title} />;
}

export function SeverityBadge({ value }) {
  return <StatusBadge value={value} kind="severity" prefix="Severity" />;
}

export function RiskBadge({ value }) {
  return <StatusBadge value={value || 'Undetermined'} kind="risk" prefix="Risk" />;
}

/** BR-03 tier of a validated 30-day failure probability (Low / Medium / Critical). */
export function TierBadge({ value }) {
  return <StatusBadge value={value} kind="tier" prefix="30-day risk tier" />;
}

/** Work-order origin: "Automatic" (raised from an anomaly) or "Manual" (created by a dispatcher). */
export function WorkOrderSourceBadge({ value }) {
  return <StatusBadge value={value} kind="woSource" prefix="Source" />;
}

/** State of the stored deterministic diagnosis on a work order. */
export function DiagnosisStatusBadge({ value }) {
  return <StatusBadge value={value} kind="diagnosis" label={DIAGNOSIS_LABELS[value]} prefix="Diagnosis" />;
}

export function PriorityBadge({ value }) {
  return <StatusBadge value={value} kind="priority" prefix="Priority" />;
}
