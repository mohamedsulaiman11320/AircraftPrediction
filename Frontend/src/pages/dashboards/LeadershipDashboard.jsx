import { useState } from 'react';
import { dashboardsApi } from '../../api/dashboards.js';
import { reportsApi } from '../../api/reports.js';
import { useAsync } from '../../hooks/useAsync.js';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { KpiCard } from '../../components/KpiCard.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { DonutChart } from '../../components/charts/DonutChart.jsx';
import { BarChart } from '../../components/charts/BarChart.jsx';
import { LineChart } from '../../components/charts/LineChart.jsx';
import { RISK_COLORS } from '../../components/charts/Sparkline.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonKpis, SkeletonTable } from '../../components/Skeleton.jsx';
import { FailurePredictionStatus } from '../../components/Misc.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Link } from '../../router/Link.jsx';
import { formatNumber } from '../../utils/format.js';
import { modelColumns } from '../shared/columns.jsx';
import { Icon } from '../../components/Icon.jsx';

const EXPORTS = [
  { key: 'fleetHealth', label: 'Fleet health' },
  { key: 'maintenanceHistory', label: 'Maintenance history' },
  { key: 'dataQuality', label: 'Data quality' },
  { key: 'modelPerformance', label: 'Model performance' },
];

export function ExportButtons({ keys = EXPORTS.map((e) => e.key) }) {
  const toast = useToast();
  const [busy, setBusy] = useState(null);
  const run = async (key, label) => {
    setBusy(key);
    try {
      await reportsApi.exportCsv(key);
      toast.success(`${label} CSV downloaded.`);
    } catch (err) {
      toast.error(err);
    } finally {
      setBusy(null);
    }
  };
  return (
    <div className="row">
      {EXPORTS.filter((e) => keys.includes(e.key)).map((e) => (
        <button key={e.key} type="button" className="btn btn--secondary btn--sm" onClick={() => run(e.key, e.label)} disabled={busy === e.key}>
          {busy === e.key ? <span className="spinner" aria-hidden="true" /> : <Icon name="download" size={14} />}
          {e.label} CSV
        </button>
      ))}
    </div>
  );
}

export function LeadershipDashboard() {
  const { data, error, loading, reload } = useAsync(() => dashboardsApi.leadership(), []);
  const header = (
    <PageHeader
      eyebrow="Fleet health"
      title="Fleet health overview"
      subtitle="Availability, maintenance trends, anomaly activity and model governance."
      actions={
        <>
          <Link to="/reports" className="btn btn--primary">
            <Icon name="file" size={16} /> Reports
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
        <SkeletonKpis count={5} />
        <div className="card">
          <SkeletonTable />
        </div>
      </>
    );
  const c = data.fleetCounts || {};
  const u = data.unscheduled || {};
  const delta = (u.last30Days ?? 0) - (u.previous30Days ?? 0);
  const availability = c.total ? `${Math.round(((c.available || 0) / c.total) * 100)}% of fleet` : undefined;
  return (
    <>
      {header}
      <div className="grid grid--kpi">
        <KpiCard label="Available aircraft" value={c.available} hint={availability} tone="success" />
        <KpiCard label="Under maintenance" value={c.underMaintenance} tone="warning" />
        <KpiCard label="Grounded" value={c.grounded} tone="danger" />
        <KpiCard label="Open work orders" value={formatNumber(data.openWorkOrders)} tone="accent" />
        <KpiCard
          label="Unscheduled (30 days)"
          value={u.last30Days}
          hint={`${delta >= 0 ? '+' : ''}${delta} vs previous 30 days · ${u.last90Days ?? '—'} in 90 days`}
          tone="neutral"
        />
      </div>
      <div className="stack stack--lg">
        <FailurePredictionStatus status={data.failurePredictionStatus} />
        <div className="grid grid--2-1">
          <Card title="Maintenance trend">
            <BarChart
              ariaLabel="Monthly maintenance events by type"
              data={(data.maintenanceTrend || []).map((m) => ({ label: m.month, scheduled: m.scheduled, unscheduled: m.unscheduled, inspections: m.inspections }))}
              series={[
                { key: 'scheduled', name: 'Scheduled', color: 'var(--chart-1)' },
                { key: 'unscheduled', name: 'Unscheduled', color: 'var(--chart-2)' },
                { key: 'inspections', name: 'Inspections', color: 'var(--chart-4)' },
              ]}
              emptyText="No maintenance events recorded."
            />
          </Card>
          <Card title="Risk distribution">
            <DonutChart
              ariaLabel="Aircraft by latest risk indicator"
              centerLabel="Aircraft"
              data={(data.riskDistribution || []).map((r) => ({ label: r.riskCategory, value: r.count, color: RISK_COLORS[r.riskCategory] || 'var(--slate-400)' }))}
            />
          </Card>
        </div>
        <Card title="Anomaly trend (detections per day)">
          <LineChart
            ariaLabel="Anomalies detected per day"
            unit="count"
            series={[{ name: 'Anomalies', color: 'var(--chart-1)', data: (data.anomalyTrend || []).map((d) => ({ x: d.date, y: d.count })) }]}
            emptyText="No anomalies detected in this period."
          />
        </Card>
        <Card title="Model summaries" flush actions={<Link to="/models">Model registry</Link>}>
          <DataTable columns={modelColumns()} rows={data.modelSummaries} caption="Model summaries" emptyTitle="No models registered" />
        </Card>
        <Card title="Export reports (CSV)">
          <ExportButtons />
        </Card>
      </div>
    </>
  );
}
