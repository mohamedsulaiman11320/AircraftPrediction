import { useState } from 'react';
import { workOrdersApi } from '../../api/workOrders.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { useParams } from '../../router/Router.jsx';
import { Link } from '../../router/Link.jsx';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { DiagnosisStatusBadge, PriorityBadge, RiskBadge, StatusBadge, WorkOrderSourceBadge, WorkOrderStatusBadge } from '../../components/StatusBadge.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonPage } from '../../components/Skeleton.jsx';
import { Alert, Disclaimer } from '../../components/Misc.jsx';
import { ProbabilityValue } from '../../components/Probability.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Icon } from '../../components/Icon.jsx';
import { formatDateTime, formatRelative, humanize } from '../../utils/format.js';
import { WorkOrderActionButtons, WorkOrderActionDialog } from './WorkOrderActions.jsx';
import { RecordMaintenanceModal } from '../fleet/AircraftModals.jsx';
import { DiagnosisCardBody, EvidenceSection, TechnicianReports } from './WorkOrderSections.jsx';

function Field({ label, children }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{children ?? '—'}</dd>
    </div>
  );
}

function TextBlock({ title, text }) {
  return (
    <div>
      <h3 style={{ fontSize: 'var(--fs-sm)' }}>{title}</h3>
      <p style={{ whiteSpace: 'pre-wrap' }}>{text || <span className="muted">Not recorded.</span>}</p>
    </div>
  );
}

/** Aircraft summary embedded in the ticket (technicians have no fleet access). */
export function TicketAircraft({ aircraft, linkToFleet }) {
  if (!aircraft) return <p className="muted">Aircraft summary not available.</p>;
  return (
    <dl className="dl">
      <Field label="Registration">{linkToFleet ? <Link to={`/fleet/${aircraft.id}`}>{aircraft.registrationNumber}</Link> : aircraft.registrationNumber}</Field>
      <Field label="Model">
        {aircraft.manufacturer} {aircraft.model}
      </Field>
      <Field label="Fleet group">{aircraft.fleetGroup}</Field>
      <Field label="Status">
        <StatusBadge value={aircraft.operationalStatus} />
        {aircraft.statusReason ? <div className="subtle">{aircraft.statusReason}</div> : null}
      </Field>
      <Field label="Last telemetry">
        <span title={formatDateTime(aircraft.lastTelemetryAtUtc)}>{formatRelative(aircraft.lastTelemetryAtUtc)}</span>
      </Field>
      <Field label="Last inspection">{formatDateTime(aircraft.lastInspectionAtUtc)}</Field>
      <Field label="Active inspection flags">{aircraft.activeInspectionFlagCount}</Field>
      <Field label="Latest risk indicator">
        <RiskBadge value={aircraft.latestRisk?.riskCategory} />
      </Field>
    </dl>
  );
}

/** Risk prediction linked to the ticket (probability only when validated). */
export function RiskPrediction({ prediction }) {
  if (!prediction) return <p className="muted">No risk prediction is linked to this work order.</p>;
  return (
    <section aria-label="Risk prediction">
      <dl className="dl">
        <Field label="Risk category">
          <RiskBadge value={prediction.riskCategory} />
        </Field>
        <Field label="State">{prediction.predictionState ? humanize(prediction.predictionState) : null}</Field>
        <Field label="Basis">
          <StatusBadge value={prediction.basis} />
        </Field>
        <Field label="Probability">
          <span data-testid="ticket-probability">
            <ProbabilityValue prediction={prediction} />
          </span>
        </Field>
        <Field label="Horizon">{prediction.horizonDays ? `${prediction.horizonDays} days` : null}</Field>
        <Field label="Generated">{formatDateTime(prediction.predictionTimestampUtc)}</Field>
        <Field label="Telemetry used up to">{prediction.featureCutoffUtc ? formatDateTime(prediction.featureCutoffUtc) : null}</Field>
        <Field label="Model">{prediction.modelName ? `${prediction.modelName} ${prediction.modelVersion || ''}` : 'None (descriptive rules)'}</Field>
      </dl>
      <Disclaimer>{prediction.disclaimer}</Disclaimer>
    </section>
  );
}

export function WorkOrderDetailPage() {
  const { id } = useParams();
  const { can } = useAuth();
  const toast = useToast();
  const { data: wo, error, reload } = useAsync(() => workOrdersApi.get(id), [id]);
  const [action, setAction] = useState(null);
  const [logOpen, setLogOpen] = useState(false);
  const hasReports = (wo?.reports?.length ?? 0) > 0;
  // Descriptions for the reported action codes; a failure only drops the description, never the code.
  const codes = useAsync(() => workOrdersApi.actionCodes(), [], { enabled: hasReports });

  if (error && !wo) return <ErrorState error={error} onRetry={reload} title="Could not load work order" />;
  if (!wo) return <SkeletonPage />;

  const onDone = (_updated, msg) => {
    setAction(null);
    setLogOpen(false);
    toast.success(msg);
    reload();
  };
  const isDispatcherView = can(ACCESS.fleet);
  const aircraft = wo.aircraft || { id: wo.aircraftId, registrationNumber: wo.registrationNumber };
  const canLog = can(ACCESS.maintenanceLogCreate) && !['Closed', 'Cancelled'].includes(wo.status);

  return (
    <>
      <PageHeader
        eyebrow={isDispatcherView ? <Link to="/work-orders">Work orders</Link> : <Link to="/my-tickets">My tickets</Link>}
        title={`${wo.workOrderNumber} · ${wo.title}`}
        documentTitle={wo.workOrderNumber}
        subtitle={
          <>
            {isDispatcherView ? <Link to={`/fleet/${wo.aircraftId}`}>{wo.registrationNumber}</Link> : wo.registrationNumber} · created by {wo.createdBy} on{' '}
            {formatDateTime(wo.createdAtUtc)}
          </>
        }
        actions={
          <>
            <WorkOrderActionButtons workOrder={wo} canAct={can(ACCESS.workOrderActions)} canEdit={can(ACCESS.workOrderEdit)} onAction={setAction} />
            {canLog ? (
              <button type="button" className="btn btn--secondary" onClick={() => setLogOpen(true)}>
                <Icon name="file" size={16} /> Log maintenance
              </button>
            ) : null}
          </>
        }
      />
      <div className="stack stack--lg">
        {wo.isOverdue ? (
          <Alert tone="danger" title="Overdue">
            This work order passed its due date ({formatDateTime(wo.dueAtUtc)}).
          </Alert>
        ) : null}
        <div className="grid grid--2-1">
          <Card title="Details">
            <dl className="dl">
              <Field label="Status">
                <WorkOrderStatusBadge value={wo.status} />
              </Field>
              <Field label="Priority">
                <PriorityBadge value={wo.priority} />
              </Field>
              <Field label="Source">
                <WorkOrderSourceBadge value={wo.source} />
              </Field>
              <Field label="Diagnosis">
                <DiagnosisStatusBadge value={wo.diagnosisStatus} />
              </Field>
              <Field label="Assigned to">{wo.assignedTechnicianName || 'Unassigned'}</Field>
              <Field label="Due">{formatDateTime(wo.dueAtUtc)}</Field>
              <Field label="Component">{wo.componentId}</Field>
              {isDispatcherView ? (
                <>
                  <Field label="Linked anomaly">{wo.anomalyId ? <Link to={`/anomalies/${wo.anomalyId}`}>View anomaly</Link> : null}</Field>
                  <Field label="Linked prediction">
                    {wo.predictionId && can(ACCESS.predictions) ? <Link to={`/predictions?aircraftId=${wo.aircraftId}`}>View predictions</Link> : null}
                  </Field>
                </>
              ) : null}
              <Field label="Actual failure confirmed">
                {wo.actualFailureConfirmed === null || wo.actualFailureConfirmed === undefined ? null : wo.actualFailureConfirmed ? 'Yes' : 'No'}
              </Field>
              <Field label="Resolved">{wo.resolvedAtUtc ? formatDateTime(wo.resolvedAtUtc) : null}</Field>
              <Field label="Closed">{wo.closedAtUtc ? formatDateTime(wo.closedAtUtc) : null}</Field>
              <Field label="Last updated">{formatDateTime(wo.updatedAtUtc)}</Field>
            </dl>
            <div className="section-gap stack">
              <TextBlock title="Description" text={wo.description} />
              <TextBlock title="Inspection findings" text={wo.inspectionFindings} />
              <TextBlock title="Corrective action" text={wo.correctiveAction} />
              <TextBlock title="Resolution notes" text={wo.resolutionNotes} />
            </div>
          </Card>
          <Card title="Aircraft">
            <TicketAircraft aircraft={wo.aircraft} linkToFleet={isDispatcherView} />
          </Card>
        </div>
        <Card title="Diagnosis" id="diagnosis">
          <DiagnosisCardBody workOrder={wo} />
        </Card>
        <Card title="Evidence" id="evidence">
          <EvidenceSection anomaly={wo.anomaly} />
        </Card>
        {wo.prediction ? (
          <Card title="Risk prediction">
            <RiskPrediction prediction={wo.prediction} />
          </Card>
        ) : null}
        <Card title="Technician reports" id="technician-reports">
          <TechnicianReports reports={wo.reports} actionCodes={codes.data} />
        </Card>
        {wo.dispatcherReviewNotes ? (
          <Card title="Dispatcher review" id="dispatcher-review">
            <p style={{ whiteSpace: 'pre-wrap', margin: 0 }} data-testid="dispatcher-review-notes">
              {wo.dispatcherReviewNotes}
            </p>
          </Card>
        ) : null}
        <Card title="History">
          {wo.history?.length ? (
            <ol className="timeline" aria-label="Work order history">
              {[...wo.history]
                .sort((a, b) => Date.parse(b.occurredAtUtc) - Date.parse(a.occurredAtUtc))
                .map((h) => (
                  <li key={h.id}>
                    <strong>{humanize(h.action)}</strong>
                    <div className="subtle">
                      {h.userName || 'System'} · {formatDateTime(h.occurredAtUtc)}
                    </div>
                    {h.details && Object.keys(h.details).length ? (
                      <div className="subtle mono" style={{ wordBreak: 'break-word' }}>
                        {Object.entries(h.details)
                          .map(([k, v]) => `${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`)
                          .join(' · ')}
                      </div>
                    ) : null}
                  </li>
                ))}
            </ol>
          ) : (
            <p className="muted">No history recorded.</p>
          )}
        </Card>
      </div>
      <WorkOrderActionDialog action={action} workOrder={wo} onClose={() => setAction(null)} onDone={onDone} onConflict={reload} />
      {canLog ? <RecordMaintenanceModal open={logOpen} aircraft={aircraft} onClose={() => setLogOpen(false)} onDone={onDone} /> : null}
    </>
  );
}
