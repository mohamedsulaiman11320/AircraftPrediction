import { useEffect, useState } from 'react';
import { predictionsApi } from '../../api/predictions.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { Link } from '../../router/Link.jsx';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { RiskBadge, StatusBadge } from '../../components/StatusBadge.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { Disclaimer, FailurePredictionStatus, KeyValueList } from '../../components/Misc.jsx';
import { ConfirmDialog, Modal } from '../../components/Modal.jsx';
import { SelectField, TextArea, TextField } from '../../components/FormFields.jsx';
import { ProbabilityValue } from '../../components/Probability.jsx';
import { useToast } from '../../components/Toast.jsx';
import { PredictionPeriod, predictionColumns } from '../shared/columns.jsx';
import { Alert } from '../../components/Misc.jsx';
import { useAircraftOptions } from '../shared/AircraftSelect.jsx';
import { AircraftLink } from '../shared/AircraftLink.jsx';
import { ACTUAL_OUTCOME, APPROVAL_STATUS, PREDICTION_BASIS, RISK_CATEGORY, options } from '../../utils/enums.js';
import { formatDateTime } from '../../utils/format.js';

export function PredictionDetail({ prediction: p }) {
  return (
    <div className="stack">
      <dl className="dl">
        <div>
          <dt>Aircraft</dt>
          <dd>
            <AircraftLink id={p.aircraftId}>{p.registrationNumber}</AircraftLink>
          </dd>
        </div>
        <div>
          <dt>Risk category</dt>
          <dd>
            <RiskBadge value={p.riskCategory} />
          </dd>
        </div>
        <div>
          <dt>Basis</dt>
          <dd>
            <StatusBadge value={p.basis} />
          </dd>
        </div>
        <div>
          <dt>Probability</dt>
          <dd data-testid="prediction-probability">
            <ProbabilityValue prediction={p} />
          </dd>
        </div>
        <div>
          <dt>Prediction state</dt>
          <dd>
            <StatusBadge value={p.predictionState} />
          </dd>
        </div>
        <div>
          <dt>Prediction period</dt>
          <dd>
            <PredictionPeriod prediction={p} />
          </dd>
        </div>
        <div>
          <dt>Telemetry cut-off</dt>
          <dd>{formatDateTime(p.featureCutoffUtc)}</dd>
        </div>
        <div>
          <dt>Model version</dt>
          <dd>{p.modelName ? `${p.modelName} ${p.modelVersion || ''}` : 'None (descriptive rules)'}</dd>
        </div>
        <div>
          <dt>Generated</dt>
          <dd>{formatDateTime(p.predictionTimestampUtc)}</dd>
        </div>
        <div>
          <dt>Review</dt>
          <dd>
            <StatusBadge value={p.approvalStatus} />
            {p.reviewedBy ? (
              <div className="subtle">
                {p.reviewedBy} · {formatDateTime(p.reviewedAtUtc)}
              </div>
            ) : null}
          </dd>
        </div>
        <div>
          <dt>Actual outcome</dt>
          <dd>
            <StatusBadge value={p.actualOutcome} />
            {p.outcomeRecordedAtUtc ? <div className="subtle">{formatDateTime(p.outcomeRecordedAtUtc)}</div> : null}
          </dd>
        </div>
      </dl>
      {p.reviewNotes ? (
        <p>
          <strong>Review notes:</strong> {p.reviewNotes}
        </p>
      ) : null}
      <Disclaimer>{p.disclaimer}</Disclaimer>
      <div>
        <h3 style={{ fontSize: 'var(--fs-sm)' }}>Feature snapshot</h3>
        <KeyValueList data={p.featureSnapshot} />
      </div>
    </div>
  );
}

function ReviewDialog({ open, prediction, onClose, onDone }) {
  const [status, setStatus] = useState('Approved');
  const [notes, setNotes] = useState('');
  const [error, setError] = useState(null);
  useEffect(() => {
    if (open) {
      setStatus('Approved');
      setNotes('');
      setError(null);
    }
  }, [open]);
  return (
    <ConfirmDialog
      open={open}
      title={`Review prediction · ${prediction?.registrationNumber || ''}`}
      message="Reviewing records whether this risk indicator is accepted for maintenance planning. It does not change aircraft status."
      confirmLabel="Submit review"
      onClose={onClose}
      onConfirm={async () => {
        if (!notes.trim()) {
          setError('Review notes are required.');
          return;
        }
        onDone(await predictionsApi.review(prediction.id, status, notes.trim()), 'Review recorded.');
      }}
    >
      <SelectField label="Decision" value={status} onChange={(e) => setStatus(e.target.value)} options={options(['Approved', 'Rejected'])} required />
      <TextArea label="Notes" value={notes} onChange={(e) => setNotes(e.target.value)} error={error} maxLength={4000} required />
    </ConfirmDialog>
  );
}

function OutcomeDialog({ open, prediction, onClose, onDone }) {
  const [outcome, setOutcome] = useState('UnscheduledMaintenance');
  const [workOrderId, setWorkOrderId] = useState('');
  useEffect(() => {
    if (open) {
      setOutcome(prediction?.actualOutcome !== 'Unknown' ? prediction?.actualOutcome : 'UnscheduledMaintenance');
      setWorkOrderId('');
    }
  }, [open, prediction]);
  return (
    <ConfirmDialog
      open={open}
      title={`Record actual outcome · ${prediction?.registrationNumber || ''}`}
      message="Outcomes are the ground truth used to evaluate future models."
      confirmLabel="Record outcome"
      onClose={onClose}
      onConfirm={async () => onDone(await predictionsApi.outcome(prediction.id, outcome, workOrderId.trim() || undefined), 'Outcome recorded.')}
    >
      <SelectField label="Actual outcome" value={outcome} onChange={(e) => setOutcome(e.target.value)} options={options(ACTUAL_OUTCOME)} required />
      <TextField label="Related work order ID" value={workOrderId} onChange={(e) => setWorkOrderId(e.target.value)} hint="Optional" />
    </ConfirmDialog>
  );
}

function MetadataPanel({ meta }) {
  const { data, error, reload } = meta;
  if (error && !data) return <ErrorState error={error} onRetry={reload} title="Could not load prediction metadata" />;
  if (!data) return null;
  return (
    <div className="stack">
      <FailurePredictionStatus status={data.failurePredictionStatus} />
      <Card title="Prediction configuration">
        <dl className="dl">
          <div>
            <dt>Configured horizon</dt>
            <dd>{data.configuredHorizonDays} days</dd>
          </div>
          <div>
            <dt>Active anomaly model</dt>
            <dd>{data.activeAnomalyModel ? <Link to={`/models/${data.activeAnomalyModel.id}`}>{`${data.activeAnomalyModel.modelName} ${data.activeAnomalyModel.version}`}</Link> : 'None active'}</dd>
          </div>
          <div>
            <dt>Active failure model</dt>
            <dd>{data.activeFailureModel ? <Link to={`/models/${data.activeFailureModel.id}`}>{`${data.activeFailureModel.modelName} ${data.activeFailureModel.version}`}</Link> : 'None active'}</dd>
          </div>
        </dl>
        {data.riskCategories?.length ? (
          <ul className="simple-list section-gap" aria-label="Risk categories">
            {[...data.riskCategories]
              .sort((x, y) => x.displayOrder - y.displayOrder)
              .map((r) => (
                <li key={r.code} style={{ padding: '8px 0' }}>
                  <RiskBadge value={r.code} />
                  <span className="subtle" style={{ flex: 1 }}>
                    {r.description}
                  </span>
                </li>
              ))}
          </ul>
        ) : null}
      </Card>
    </div>
  );
}

function distinct(list) {
  return Array.from(new Set(list.filter(Boolean)));
}

export function PredictionsPage() {
  const { can } = useAuth();
  const toast = useToast();
  const canAct = can(ACCESS.predictionActions);
  const meta = useAsync(() => predictionsApi.metadata(), []);
  const blocked = meta.data?.failurePredictionStatus?.status === 'Blocked';
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery();
  const aircraft = useAircraftOptions();
  const [selected, setSelected] = useState(null);
  const [dialog, setDialog] = useState(null);
  const { data, error, loading, reload, setData } = useAsync(
    () =>
      predictionsApi.list({
        aircraftId: values.aircraftId || undefined,
        riskCategory: values.riskCategory || undefined,
        basis: values.basis || undefined,
        approvalStatus: values.approvalStatus || undefined,
        page,
        pageSize,
      }),
    [values.aircraftId, values.riskCategory, values.basis, values.approvalStatus, page, pageSize],
  );

  const done = (updated, msg) => {
    setDialog(null);
    toast.success(msg);
    setData((d) => (d ? { ...d, items: d.items.map((x) => (x.id === updated.id ? updated : x)) } : d));
    if (selected?.id === updated.id) setSelected(updated);
  };

  const columns = [
    ...predictionColumns(),
    {
      key: 'actions',
      header: <span className="visually-hidden">Actions</span>,
      render: (p) => (
        <div className="row" style={{ flexWrap: 'nowrap' }}>
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => setSelected(p)} aria-label={`View prediction for ${p.registrationNumber}`}>
            View
          </button>
          {canAct && p.approvalStatus === 'PendingReview' ? (
            <button type="button" className="btn btn--secondary btn--sm" onClick={() => setDialog({ type: 'review', p })}>
              Review
            </button>
          ) : null}
          {canAct ? (
            <button type="button" className="btn btn--secondary btn--sm" onClick={() => setDialog({ type: 'outcome', p })}>
              Outcome
            </button>
          ) : null}
        </div>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        eyebrow="Operations"
        title="Risk predictions"
        subtitle="Risk categories per aircraft. Probabilities are shown only when the model has been validated."
      />
      <div className="stack stack--lg">
        <MetadataPanel meta={meta} />
        <div className="card">
          <SearchFilterBar
            values={values}
            onChange={setFilters}
            onReset={reset}
            filters={[
              { name: 'aircraftId', label: 'Aircraft', type: 'select', options: aircraft.data || [] },
              { name: 'riskCategory', label: 'Risk category', type: 'select', options: options(RISK_CATEGORY) },
              { name: 'basis', label: 'Basis', type: 'select', options: options(PREDICTION_BASIS) },
              { name: 'approvalStatus', label: 'Review status', type: 'select', options: options(APPROVAL_STATUS) },
            ]}
          />
          {error && !data ? (
            <ErrorState error={error} onRetry={reload} />
          ) : !data ? (
            <SkeletonTable />
          ) : (
            <div aria-busy={loading}>
              {blocked ? (
                <div style={{ padding: '12px 16px 0' }}>
                  <Alert tone="warning" title="No validated failure-prediction model">
                    No validated failure-prediction model — showing descriptive anomaly-based risk indicators only.
                  </Alert>
                </div>
              ) : null}
              <DataTable
                columns={columns}
                rows={data.items}
                caption="Predictions"
                emptyTitle={values.aircraftId ? 'No prediction available for this aircraft yet' : 'No predictions match these filters'}
                emptyMessage={
                  values.aircraftId
                    ? 'Nothing has been scored for the selected aircraft. No values are estimated or filled in.'
                    : undefined
                }
              />
              {distinct(data.items.map((x) => x.disclaimer)).map((d) => (
                <div key={d} style={{ padding: '0 16px' }}>
                  <Disclaimer>{d}</Disclaimer>
                </div>
              ))}
              <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
            </div>
          )}
        </div>
      </div>
      <Modal open={!!selected} title={selected ? `Prediction · ${selected.registrationNumber}` : ''} onClose={() => setSelected(null)} size="lg">
        {selected ? <PredictionDetail prediction={selected} /> : null}
      </Modal>
      <ReviewDialog open={dialog?.type === 'review'} prediction={dialog?.p} onClose={() => setDialog(null)} onDone={done} />
      <OutcomeDialog open={dialog?.type === 'outcome'} prediction={dialog?.p} onClose={() => setDialog(null)} onDone={done} />
    </>
  );
}

