import { dashboardsApi } from '../../api/dashboards.js';
import { useAsync } from '../../hooks/useAsync.js';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { KpiCard } from '../../components/KpiCard.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { StatusBadge } from '../../components/StatusBadge.jsx';
import { EmptyState, ErrorState } from '../../components/States.jsx';
import { SkeletonKpis, SkeletonTable } from '../../components/Skeleton.jsx';
import { FailurePredictionStatus, KeyValueList } from '../../components/Misc.jsx';
import { Link } from '../../router/Link.jsx';
import { formatDateTime, formatNumber, formatPercent, humanize } from '../../utils/format.js';
import { importColumns, modelColumns, runColumns } from '../shared/columns.jsx';
import { Icon } from '../../components/Icon.jsx';
import { AuditReadinessPanel, getAuditReadiness } from '../../components/AuditReadiness.jsx';

const READINESS_KEYS = ['failure_prediction_readiness', 'failurePredictionReadiness'];

function RunSummary({ run, emptyText }) {
  if (!run) return <EmptyState title={emptyText} />;
  return (
    <div className="stack">
      <div className="row">
        <StatusBadge value={run.status} />
        <span className="subtle">
          {humanize(run.jobType)} · {formatDateTime(run.completedAtUtc || run.requestedAtUtc)}
        </span>
      </div>
      {run.errorMessage ? <p className="text-danger">{run.errorMessage}</p> : null}
      {run.resultSummary ? <KeyValueList data={flatten(run.resultSummary)} /> : <p className="muted">No result summary.</p>}
    </div>
  );
}

/** Flatten one level so nested summaries render as readable key/value pairs. */
function flatten(obj) {
  const out = {};
  Object.entries(obj || {}).forEach(([k, v]) => {
    if (READINESS_KEYS.includes(k)) return; // rendered by AuditReadinessPanel
    if (Array.isArray(v)) out[k] = v.length && typeof v[0] !== 'object' ? v.join(', ') : `${v.length} item(s)`;
    else if (v && typeof v === 'object') Object.entries(v).forEach(([k2, v2]) => (out[`${k}.${k2}`] = typeof v2 === 'object' ? JSON.stringify(v2) : v2));
    else out[k] = v;
  });
  return out;
}

export function DataEngineerDashboard() {
  const { data, error, loading, reload } = useAsync(() => dashboardsApi.dataEngineer(), []);
  const header = (
    <PageHeader
      eyebrow="Data & models"
      title="Data & model pipeline"
      subtitle="Telemetry quality, imports, pipeline runs and the model registry."
      actions={
        <>
          <Link to="/imports" className="btn btn--primary">
            <Icon name="upload" size={16} /> Import telemetry
          </Link>
          <Link to="/pipelines" className="btn btn--secondary">
            <Icon name="play" size={16} /> Pipelines
          </Link>
          <button type="button" className="btn btn--secondary" onClick={reload} disabled={loading}>
            <Icon name="refresh" size={16} /> Refresh
          </button>
        </>
      }
    />
  );
  if (error && !data)
    return (
      <>
        {header}
        <div className="card">
          <ErrorState error={error} onRetry={reload} />
        </div>
      </>
    );
  if (!data)
    return (
      <>
        {header}
        <SkeletonKpis />
        <div className="card">
          <SkeletonTable />
        </div>
      </>
    );
  const q = data.dataQuality || {};
  const fp = data.featurePreparation || {};
  const auditReadiness = getAuditReadiness(data.latestAudit?.resultSummary);
  return (
    <>
      {header}
      <div className="grid grid--kpi">
        <KpiCard label="Telemetry records" value={formatNumber(q.totalRecords)} tone="accent" />
        <KpiCard label="Valid records" value={formatNumber(q.validRecords)} tone="success" />
        <KpiCard label="Suspect records" value={formatNumber(q.suspectRecords)} tone="warning" />
        <KpiCard label="Valid share" value={formatPercent(q.validPercent)} tone="neutral" />
      </div>
      <div className="stack stack--lg">
        <FailurePredictionStatus status={data.failurePredictionStatus} />
        <div className="grid grid--2">
          <Card title="Latest dataset audit" actions={<Link to="/pipelines?tab=audit">Audit details</Link>}>
            <RunSummary run={data.latestAudit} emptyText="No dataset audit has run yet" />
            {auditReadiness ? (
              <div className="section-gap">
                <AuditReadinessPanel readiness={auditReadiness} />
              </div>
            ) : null}
          </Card>
          <Card title="Latest scoring run">
            <RunSummary run={data.latestScoring} emptyText="No scoring run yet" />
          </Card>
        </div>
        <Card title="Feature preparation">
          <div className="row" style={{ marginBottom: 12 }}>
            <StatusBadge value={fp.status} label={fp.status ? humanize(fp.status) : 'Unknown'} />
            <span className="subtle">Last prepared: {formatDateTime(fp.lastPreparedAtUtc)}</span>
          </div>
          {fp.featureNames?.length ? (
            <div className="chips" aria-label="Feature names">
              {fp.featureNames.map((f) => (
                <span className="chip" key={f}>
                  {f}
                </span>
              ))}
            </div>
          ) : (
            <p className="muted">No features prepared yet.</p>
          )}
        </Card>
        <Card title="Latest telemetry imports" flush actions={<Link to="/imports">Import history</Link>}>
          <DataTable columns={importColumns()} rows={data.latestImports} caption="Latest imports" emptyTitle="No imports yet" />
        </Card>
        <Card title="Recent pipeline runs" flush actions={<Link to="/pipelines">All runs</Link>}>
          <DataTable columns={runColumns()} rows={data.recentRuns} caption="Recent pipeline runs" emptyTitle="No pipeline runs yet" />
        </Card>
        <Card title="Model registry" flush actions={<Link to="/models">Registry</Link>}>
          <DataTable columns={modelColumns()} rows={data.models} caption="Models" emptyTitle="No models trained yet" />
        </Card>
      </div>
    </>
  );
}
