import { UNASSIGNED_FLEET_GROUP } from '../../utils/enums.js';
import { aircraftModelText } from '../../utils/format.js';
import { useState } from 'react';
import { aircraftApi } from '../../api/aircraft.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { useParams } from '../../router/Router.jsx';
import { Link } from '../../router/Link.jsx';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { RiskBadge, StatusBadge } from '../../components/StatusBadge.jsx';
import { EmptyState, ErrorState } from '../../components/States.jsx';
import { SkeletonPage } from '../../components/Skeleton.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Icon } from '../../components/Icon.jsx';
import { formatDateTime, formatRelative, toInputDate, daysAgo, fromInputDate } from '../../utils/format.js';
import { anomalyColumns, maintenanceColumns, predictionColumns, workOrderColumns } from '../shared/columns.jsx';
import { FleetHealthTelemetryPanel } from './FleetHealthGraph.jsx';
import { ChangeStatusModal, ClearFlagModal, RaiseFlagModal, RecordMaintenanceModal } from './AircraftModals.jsx';
import { AircraftHealthCard } from './AircraftHealth.jsx';

const OPEN_INVESTIGATION = ['New', 'Acknowledged', 'Investigating'];   // as the API's open-incident rule

function MaintenanceHistory({ aircraftId, refreshKey = 0 }) {
  const [page, setPage] = useState(1);
  const { data, error, reload } = useAsync(() => aircraftApi.maintenanceHistory(aircraftId, { page, pageSize: 10 }), [aircraftId, page, refreshKey]);
  if (error && !data) return <ErrorState error={error} onRetry={reload} />;
  if (!data) return <p className="muted card__body">Loading maintenance history…</p>;
  return (
    <>
      <DataTable columns={maintenanceColumns()} rows={data.items} caption="Maintenance history" emptyTitle="No maintenance history" />
      {data.totalCount > 0 ? <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} /> : null}
    </>
  );
}

export function AircraftDetailPage() {
  const { id } = useParams();
  const { can } = useAuth();
  const toast = useToast();
  const { data: a, error, reload, setData } = useAsync(() => aircraftApi.get(id), [id]);
  const [modal, setModal] = useState(null); // 'status' | 'flag' | 'maintenance' | { clear: flag }
  const [historyKey, setHistoryKey] = useState(0);
  const [range, setRange] = useState({ from: toInputDate(daysAgo(7)), to: toInputDate(new Date()), bucket: 60 });

  if (error && !a) return <ErrorState error={error} onRetry={reload} title="Could not load aircraft" />;
  if (!a) return <SkeletonPage />;

  const done = (updated, msg) => {
    setModal(null);
    toast.success(msg);
    if (updated && updated.dispatchAdvisory) setData(updated);
    else reload();
  };
  const isMT = can(ACCESS.fleetStatus);

  return (
    <>
      <PageHeader
        eyebrow={<Link to="/fleet">Fleet</Link>}
        title={a.registrationNumber}
        documentTitle={`${a.registrationNumber} · Fleet`}
        subtitle={[aircraftModelText(a), a.fleetGroup && a.fleetGroup !== UNASSIGNED_FLEET_GROUP ? a.fleetGroup : null, a.serialNumber ? `S/N ${a.serialNumber}` : null]
          .filter(Boolean)
          .join(' · ') || undefined}
        actions={
          <>
            {isMT ? (
              <>
                <button type="button" className="btn btn--primary" onClick={() => setModal('status')}>
                  Change status
                </button>
                <button type="button" className="btn btn--secondary" onClick={() => setModal('maintenance')}>
                  Record maintenance event
                </button>
                <button type="button" className="btn btn--secondary" onClick={() => setModal('flag')}>
                  <Icon name="flag" size={16} /> Raise flag
                </button>
              </>
            ) : null}
            {can(ACCESS.workOrderCreate) ? (
              <Link to={`/work-orders/new?aircraftId=${a.id}`} className="btn btn--secondary">
                <Icon name="wrench" size={16} /> New work order
              </Link>
            ) : null}
            {can(ACCESS.fleetEdit) ? (
              <Link to={`/fleet/${a.id}/edit`} className="btn btn--secondary">
                Edit
              </Link>
            ) : null}
          </>
        }
      />

      <div className="stack stack--lg">
        <Card title="Aircraft summary">
            <dl className="dl">
              <div>
                <dt>Registration</dt>
                <dd>{a.registrationNumber}</dd>
              </div>
              {aircraftModelText(a) ? (
                <div>
                  <dt>Model</dt>
                  <dd>{aircraftModelText(a)}</dd>
                </div>
              ) : null}
              {a.fleetGroup && a.fleetGroup !== UNASSIGNED_FLEET_GROUP ? (
                <div>
                  <dt>Fleet group</dt>
                  <dd>{a.fleetGroup}</dd>
                </div>
              ) : null}
              <div>
                <dt>Operational status</dt>
                <dd>
                  <StatusBadge value={a.operationalStatus} />
                  {a.statusReason ? <div className="subtle">{a.statusReason}</div> : null}
                  <div className="subtle">since {formatDateTime(a.statusChangedAtUtc)}</div>
                </dd>
              </div>
              <div>
                <dt>Last telemetry</dt>
                <dd title={formatDateTime(a.lastTelemetryAtUtc)}>{formatRelative(a.lastTelemetryAtUtc)}</dd>
              </div>
              <div>
                <dt>Last inspection</dt>
                <dd>{formatDateTime(a.lastInspectionAtUtc)}</dd>
              </div>
              <div>
                <dt>Latest risk indicator</dt>
                <dd>
                  <RiskBadge value={a.latestRisk?.riskCategory} />
                  {a.latestRisk ? (
                    <div className="subtle">
                      {a.latestRisk.basis === 'Model' ? 'Model' : 'Descriptive indicator'} · {a.latestRisk.horizonDays}-day horizon
                    </div>
                  ) : null}
                </dd>
              </div>
              <div>
                <dt>Open items</dt>
                <dd>
                  {a.openAnomalyCount} anomalies · {a.activeInspectionFlagCount} flags · {a.openWorkOrderCount} work orders
                </dd>
              </div>
            </dl>
        </Card>

        <AircraftHealthCard health={a.health} />

        <Card title="Active inspection flags" flush>
          {a.activeInspectionFlags?.length ? (
            <ul className="simple-list">
              {a.activeInspectionFlags.map((f) => (
                <li key={f.id}>
                  <div>
                    <div className="row">
                      <StatusBadge value={f.severity} />
                      <strong>{f.reason}</strong>
                    </div>
                    <div className="subtle">
                      Raised by {f.raisedBy} · {formatDateTime(f.raisedAtUtc)}
                      {f.workOrderId ? (
                        <>
                          {' · '}
                          <Link to={`/work-orders/${f.workOrderId}`}>work order</Link>
                        </>
                      ) : null}
                      {f.anomalyId ? (
                        <>
                          {' · '}
                          <Link to={`/anomalies/${f.anomalyId}`}>anomaly</Link>
                        </>
                      ) : null}
                    </div>
                  </div>
                  {can(ACCESS.inspectionFlags) ? (
                    <button type="button" className="btn btn--secondary btn--sm" onClick={() => setModal({ clear: f })}>
                      Clear flag
                    </button>
                  ) : null}
                </li>
              ))}
            </ul>
          ) : (
            <EmptyState title="No active inspection flags" />
          )}
        </Card>

        <Card title="Telemetry">
          <div className="row" style={{ marginBottom: 16, alignItems: 'flex-end' }}>
            <div className="field" style={{ marginBottom: 0 }}>
              <label className="field__label" htmlFor="ac-from">
                From (UTC)
              </label>
              <input id="ac-from" type="date" className="input" value={range.from} onChange={(e) => setRange((r) => ({ ...r, from: e.target.value }))} />
            </div>
            <div className="field" style={{ marginBottom: 0 }}>
              <label className="field__label" htmlFor="ac-to">
                To (UTC)
              </label>
              <input id="ac-to" type="date" className="input" value={range.to} onChange={(e) => setRange((r) => ({ ...r, to: e.target.value }))} />
            </div>
            <div className="field" style={{ marginBottom: 0 }}>
              <label className="field__label" htmlFor="ac-bucket">
                Bucket
              </label>
              <select id="ac-bucket" className="select" value={range.bucket} onChange={(e) => setRange((r) => ({ ...r, bucket: Number(e.target.value) }))}>
                <option value={15}>15 min</option>
                <option value={60}>1 hour</option>
                <option value={360}>6 hours</option>
                <option value={1440}>1 day</option>
              </select>
            </div>
            {can(ACCESS.telemetry) ? (
              <Link to={`/telemetry?aircraftId=${a.id}`} className="btn btn--ghost btn--sm">
                Open in telemetry explorer
              </Link>
            ) : null}
          </div>
          {/* The same panel the Fleet Health graph uses, driven by this page's own range controls. */}
          <FleetHealthTelemetryPanel aircraft={a} fromUtc={fromInputDate(range.from)} toUtc={fromInputDate(range.to, true)} bucketMinutes={range.bucket} />
        </Card>

        <Card title="Latest risk predictions" flush actions={can(ACCESS.predictions) ? <Link to={`/predictions?aircraftId=${a.id}`}>All predictions</Link> : null}>
          <DataTable columns={predictionColumns({ showAircraft: false })} rows={a.latestPredictions} caption="Latest predictions" emptyTitle="No predictions yet" />
        </Card>

        <Card title="Forecast anomalies" flush actions={<Link to={`/anomalies?aircraftId=${a.id}&source=ForecastModel`}>All anomalies</Link>}>
          <DataTable
            columns={anomalyColumns({ showAircraft: false })}
            rows={(a.recentAnomalies || []).filter((x) => x.source === 'ForecastModel' && OPEN_INVESTIGATION.includes(x.investigationStatus))}
            caption="Active forecast anomalies"
            emptyTitle="No active forecast anomalies"
          />
        </Card>

        <Card title="Open work orders" flush>
          <DataTable columns={workOrderColumns({ showAircraft: false })} rows={a.openWorkOrders} caption="Open work orders" emptyTitle="No open work orders" />
        </Card>

        <Card title="Maintenance history" flush>
          <MaintenanceHistory aircraftId={a.id} refreshKey={historyKey} />
        </Card>
      </div>

      <ChangeStatusModal open={modal === 'status'} aircraft={a} onClose={() => setModal(null)} onDone={done} />
      {can(ACCESS.maintenanceLogCreate) ? (
        <RecordMaintenanceModal
          open={modal === 'maintenance'}
          aircraft={a}
          onClose={() => setModal(null)}
          onDone={(log, m) => {
            setHistoryKey((k) => k + 1);
            done(null, m);
          }}
        />
      ) : null}
      <RaiseFlagModal open={modal === 'flag'} aircraft={a} onClose={() => setModal(null)} onDone={(f, m) => done(null, m)} />
      <ClearFlagModal open={!!modal?.clear} flag={modal?.clear} onClose={() => setModal(null)} onDone={(f, m) => done(null, m)} />
    </>
  );
}
