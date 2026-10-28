import { useEffect, useState } from 'react';
import { anomaliesApi } from '../../api/anomalies.js';
import { workOrdersApi } from '../../api/workOrders.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { useParams } from '../../router/Router.jsx';
import { Link } from '../../router/Link.jsx';
import { AircraftLink } from '../shared/AircraftLink.jsx';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { SeverityBadge, StatusBadge, WorkOrderStatusBadge } from '../../components/StatusBadge.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonPage } from '../../components/Skeleton.jsx';
import { KeyValueList } from '../../components/Misc.jsx';
import { ForecastDiagnosis } from './ForecastDiagnosis.jsx';
import { DiagnosisText } from '../workorders/DiagnosisText.jsx';
import { ConfirmDialog } from '../../components/Modal.jsx';
import { SelectField, TextArea } from '../../components/FormFields.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Icon } from '../../components/Icon.jsx';
import { formatDateTime, formatNumber } from '../../utils/format.js';

const INVESTIGATION_OPTIONS = [
  { value: 'Investigating', label: 'Investigating' },
  { value: 'Dismissed', label: 'Dismissed (not an issue)' },
  { value: 'ConfirmedIssue', label: 'Confirmed issue' },
];

function InvestigationDialog({ open, anomaly, onClose, onDone }) {
  const [status, setStatus] = useState('Investigating');
  const [notes, setNotes] = useState('');
  const [error, setError] = useState(null);
  useEffect(() => {
    if (open) {
      setStatus(anomaly?.investigationStatus === 'Investigating' ? 'ConfirmedIssue' : 'Investigating');
      setNotes('');
      setError(null);
    }
  }, [open, anomaly]);
  return (
    <ConfirmDialog
      open={open}
      title="Update investigation"
      confirmLabel="Save"
      onClose={onClose}
      onConfirm={async () => {
        if (!notes.trim()) {
          setError('Notes are required.');
          return;
        }
        onDone(await anomaliesApi.investigate(anomaly.id, status, notes.trim()), 'Investigation updated.');
      }}
    >
      <SelectField label="Status" value={status} onChange={(e) => setStatus(e.target.value)} options={INVESTIGATION_OPTIONS} required />
      <TextArea label="Notes" value={notes} onChange={(e) => setNotes(e.target.value)} error={error} maxLength={4000} required />
    </ConfirmDialog>
  );
}

function AcknowledgeDialog({ open, anomaly, onClose, onDone }) {
  const [notes, setNotes] = useState('');
  useEffect(() => {
    if (open) setNotes('');
  }, [open]);
  return (
    <ConfirmDialog
      open={open}
      title="Acknowledge anomaly"
      message="Acknowledging records that you have seen this anomaly. It does not change aircraft status."
      confirmLabel="Acknowledge"
      onClose={onClose}
      onConfirm={async () => onDone(await anomaliesApi.acknowledge(anomaly.id, notes.trim() || undefined), 'Anomaly acknowledged.')}
    >
      <TextArea label="Notes (optional)" value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={4000} />
    </ConfirmDialog>
  );
}

export function AnomalyDetailPage() {
  const { id } = useParams();
  const { can } = useAuth();
  const toast = useToast();
  const { data: a, error, reload, setData } = useAsync(() => anomaliesApi.get(id), [id]);
  // The work order raised for this anomaly (at most one - enforced by the database), for roles that may see it.
  const canSeeOrders = can(ACCESS.workOrderDetail);
  const linked = useAsync(() => workOrdersApi.list({ anomalyId: id, page: 1, pageSize: 1 }), [id], { enabled: canSeeOrders });
  const linkedOrder = linked.data?.items?.[0] || null;
  const [dialog, setDialog] = useState(null);

  if (error && !a) return <ErrorState error={error} onRetry={reload} title="Could not load anomaly" />;
  if (!a) return <SkeletonPage />;

  const done = (updated, msg) => {
    setDialog(null);
    toast.success(msg);
    setData((prev) => ({ ...prev, ...updated }));
  };
  const isMT = can(ACCESS.anomalyActions);
  const closed = ['Dismissed', 'ConfirmedIssue'].includes(a.investigationStatus);
  const t = a.telemetry;

  return (
    <>
      <PageHeader
        eyebrow={<Link to="/anomalies">Anomalies</Link>}
        title={`Anomaly on ${a.registrationNumber}`}
        subtitle={`Observed ${formatDateTime(a.observedAtUtc)}${a.sensorId ? ` · sensor ${a.sensorId}` : ''}`}
        actions={
          <>
            {isMT && a.investigationStatus === 'New' ? (
              <button type="button" className="btn btn--primary" onClick={() => setDialog('ack')}>
                Acknowledge
              </button>
            ) : null}
            {isMT && !closed ? (
              <button type="button" className="btn btn--secondary" onClick={() => setDialog('investigate')}>
                Update investigation
              </button>
            ) : null}
            {linkedOrder ? (
              <Link to={`/work-orders/${linkedOrder.id}`} className="btn btn--primary" data-testid="linked-work-order">
                <Icon name="wrench" size={16} /> Open work order {linkedOrder.workOrderNumber}
              </Link>
            ) : can(ACCESS.workOrderCreate) && !linked.loading ? (
              <Link to={`/work-orders/new?aircraftId=${a.aircraftId}&anomalyId=${a.id}`} className="btn btn--secondary">
                <Icon name="wrench" size={16} /> Create work order
              </Link>
            ) : null}
          </>
        }
      />
      <div className="stack stack--lg">

        {linkedOrder ? (
          <Card
            title="Work order"
            actions={
              <Link to={`/work-orders/${linkedOrder.id}`} className="btn btn--primary btn--sm" data-testid="open-linked-work-order">
                Open {linkedOrder.workOrderNumber}
              </Link>
            }
          >
            <div data-testid="linked-work-order-details" className="stack">
              <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
                <strong>{linkedOrder.workOrderNumber}</strong>
                <WorkOrderStatusBadge value={linkedOrder.status} />
                <StatusBadge value={linkedOrder.priority} />
                <span className="subtle">{linkedOrder.title}</span>
              </div>
              {linkedOrder.diagnosisText ? <DiagnosisText text={linkedOrder.diagnosisText} testId="linked-diagnosis-text" /> : null}
            </div>
          </Card>
        ) : null}
        <div className="grid grid--2">
          <Card title="Detection">
            <dl className="dl">
              <div>
                <dt>Aircraft</dt>
                <dd>
                  <AircraftLink id={a.aircraftId}>{a.registrationNumber}</AircraftLink>
                </dd>
              </div>
              <div>
                <dt>Severity</dt>
                <dd>
                  <SeverityBadge value={a.severity} />
                </dd>
              </div>
              {a.anomalyScore != null ? (
                <div>
                  <dt>Anomaly score</dt>
                  <dd className="mono">{formatNumber(a.anomalyScore, 4)}</dd>
                </div>
              ) : null}
              <div>
                <dt>Model</dt>
                <dd>
                  {a.modelName} <span className="mono">{a.modelVersion}</span>
                </dd>
              </div>
              <div>
                <dt>Scored</dt>
                <dd>{formatDateTime(a.scoredAtUtc)}</dd>
              </div>
              <div>
                <dt>Observed</dt>
                <dd>{formatDateTime(a.observedAtUtc)}</dd>
              </div>
            </dl>
          </Card>
          <Card title="Investigation">
            <dl className="dl">
              <div>
                <dt>Status</dt>
                <dd>
                  <StatusBadge value={a.investigationStatus} />
                </dd>
              </div>
              <div>
                <dt>Acknowledged by</dt>
                <dd>{a.acknowledgedBy || '—'}</dd>
              </div>
              <div>
                <dt>Acknowledged</dt>
                <dd>{formatDateTime(a.acknowledgedAtUtc)}</dd>
              </div>
            </dl>
            <h3 className="section-gap" style={{ fontSize: 'var(--fs-sm)' }}>
              Notes
            </h3>
            <p style={{ whiteSpace: 'pre-wrap' }}>{a.investigationNotes || <span className="muted">No notes yet.</span>}</p>
          </Card>
        </div>
        {a.source === 'ForecastModel' ? (
          <Card title="Forecast diagnosis">
            <ForecastDiagnosis evidence={a.featureValues} />
          </Card>
        ) : (
          <Card title="Feature values at detection">
            <KeyValueList data={a.featureValues} />
          </Card>
        )}
        {t ? (
          <Card title="Source telemetry record">
            <dl className="dl">
              <div>
                <dt>Recorded</dt>
                <dd>{formatDateTime(t.recordedAtUtc)}</dd>
              </div>
              <div>
                <dt>Temperature</dt>
                <dd>
                  {formatNumber(t.temperatureC, 2)} °C{' '}
                  {t.temperatureValue !== null && t.temperatureValue !== undefined ? <span className="subtle">({t.temperatureValue} {t.temperatureUnit})</span> : null}
                </dd>
              </div>
              <div>
                <dt>Pressure</dt>
                <dd>
                  {formatNumber(t.pressureKpa, 2)} kPa{' '}
                  {t.pressureValue !== null && t.pressureValue !== undefined ? <span className="subtle">({t.pressureValue} {t.pressureUnit})</span> : null}
                </dd>
              </div>
              <div>
                <dt>Quality</dt>
                <dd>
                  <StatusBadge value={t.qualityStatus} /> {t.qualityFlags?.length ? <span className="mono subtle">{t.qualityFlags.join(', ')}</span> : null}
                </dd>
              </div>
              <div>
                <dt>Source</dt>
                <dd>{t.dataSource}</dd>
              </div>
            </dl>
          </Card>
        ) : null}
      </div>
      <AcknowledgeDialog open={dialog === 'ack'} anomaly={a} onClose={() => setDialog(null)} onDone={done} />
      <InvestigationDialog open={dialog === 'investigate'} anomaly={a} onClose={() => setDialog(null)} onDone={done} />
    </>
  );
}
