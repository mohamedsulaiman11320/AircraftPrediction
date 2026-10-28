import { telemetryApi } from '../../api/telemetry.js';
import { reportsApi } from '../../api/reports.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { KpiCard } from '../../components/KpiCard.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { EmptyState, ErrorState } from '../../components/States.jsx';
import { SkeletonKpis, SkeletonTable } from '../../components/Skeleton.jsx';
import { TextField } from '../../components/FormFields.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Icon } from '../../components/Icon.jsx';
import { AircraftSelect } from '../shared/AircraftSelect.jsx';
import { AircraftLink } from '../shared/AircraftLink.jsx';
import { formatDateTime, formatNumber, formatPercent, fromInputDate } from '../../utils/format.js';

export function DataQualityView({ data }) {
  const pct = data.totalRecords ? (data.validRecords / data.totalRecords) * 100 : null;
  return (
    <div className="stack stack--lg">
      <div className="grid grid--kpi" style={{ marginBottom: 0 }}>
        <KpiCard label="Total records" value={formatNumber(data.totalRecords)} tone="accent" />
        <KpiCard label="Valid" value={formatNumber(data.validRecords)} hint={pct !== null ? formatPercent(pct) : undefined} tone="success" />
        <KpiCard label="Suspect" value={formatNumber(data.suspectRecords)} tone="warning" />
        <KpiCard label="Missing temperature" value={formatNumber(data.missingTemperature)} tone="neutral" />
        <KpiCard label="Missing pressure" value={formatNumber(data.missingPressure)} tone="neutral" />
      </div>
      <Card title="Coverage">
        <dl className="dl">
          <div>
            <dt>First record</dt>
            <dd>{formatDateTime(data.firstRecordUtc)}</dd>
          </div>
          <div>
            <dt>Last record</dt>
            <dd>{formatDateTime(data.lastRecordUtc)}</dd>
          </div>
          <div>
            <dt>Imports</dt>
            <dd>{formatNumber(data.imports?.count)}</dd>
          </div>
          <div>
            <dt>Rows accepted</dt>
            <dd>{formatNumber(data.imports?.acceptedRows)}</dd>
          </div>
          <div>
            <dt>Rows rejected</dt>
            <dd>{formatNumber(data.imports?.rejectedRows)}</dd>
          </div>
          <div>
            <dt>Duplicate rows</dt>
            <dd>{formatNumber(data.imports?.duplicateRows)}</dd>
          </div>
        </dl>
      </Card>
      <Card title="By aircraft" flush>
        <DataTable
          rowKey="aircraftId"
          caption="Data quality by aircraft"
          columns={[
            { key: 'registrationNumber', header: 'Aircraft', sortable: true, render: (r) => <AircraftLink id={r.aircraftId}>{r.registrationNumber}</AircraftLink> },
            { key: 'records', header: 'Records', numeric: true, sortable: true, render: (r) => formatNumber(r.records) },
            { key: 'suspectRecords', header: 'Suspect', numeric: true, sortable: true, render: (r) => formatNumber(r.suspectRecords) },
            {
              key: 'suspectPct',
              header: 'Suspect %',
              numeric: true,
              sortable: true,
              sortValue: (r) => (r.records ? r.suspectRecords / r.records : 0),
              render: (r) => (r.records ? formatPercent((r.suspectRecords / r.records) * 100) : '—'),
            },
            { key: 'firstRecordUtc', header: 'First', sortable: true, render: (r) => formatDateTime(r.firstRecordUtc) },
            { key: 'lastRecordUtc', header: 'Last', sortable: true, render: (r) => formatDateTime(r.lastRecordUtc) },
          ]}
          rows={data.byAircraft}
          emptyTitle="No telemetry yet"
        />
      </Card>
    </div>
  );
}

/** Bounded fan-out: only imports that actually rejected rows are scanned, and only their first page. */
const MAX_SCANNED_IMPORTS = 10;
const REJECTED_PAGE_SIZE = 100;

/** The exact reason the ETL records when it rejects a repeat of a telemetry id within one file. */
export const IN_FILE_DUPLICATE_REASON = 'duplicate telemetry_id in this file';
export const DUPLICATE_RULE_NOTE =
  'ETL rule: the first occurrence of a telemetry id is retained and every later repeat in the same file is rejected with the reason "duplicate telemetry_id in this file". Rows counted as already stored were skipped because that telemetry id had been loaded by an earlier import, so the retained record is the one already in the database. Which individual row was kept is not recorded per row, and repeated sensor values are never treated as duplicates — only the telemetry id business key is.';

export function rejectionReasons(row) {
  if (Array.isArray(row?.reasons)) return row.reasons.map(String);
  return row?.reasons ? [String(row.reasons)] : [];
}

export function isDuplicateRejection(row) {
  return rejectionReasons(row).some((r) => /duplicate/i.test(r));
}

/** The ETL writes a rejected duplicate's business key as the raw line: {"telemetry_id": "..."}. */
export function duplicateTelemetryId(row) {
  const raw = row?.rawLine;
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw);
    const id = parsed?.telemetry_id ?? parsed?.telemetryId;
    if (id !== undefined && id !== null && String(id) !== '') return String(id);
  } catch {
    // not JSON — fall through to the textual form
  }
  const match = /"?telemetry[_ ]?id"?\s*[:=]\s*"?([^",}\s]+)/i.exec(String(raw));
  return match ? match[1] : null;
}

/**
 * Duplicate evidence, built only from what the API already returns: the per-import counters from
 * GET /telemetry/imports and the per-row rejection evidence from GET /telemetry/imports/{id}/rejected-rows.
 * Nothing is inferred — a business key that cannot be read from the stored raw line reads "Unknown".
 */
export function DuplicateDetails() {
  const { data, error, loading, reload } = useAsync(async () => {
    const imports = await telemetryApi.imports({ page: 1, pageSize: 100 });
    const items = imports?.items || [];
    const scanned = items.filter((i) => (i.rejectedRows || 0) > 0).slice(0, MAX_SCANNED_IMPORTS);
    const pages = await Promise.all(scanned.map((i) => telemetryApi.rejectedRows(i.id, { page: 1, pageSize: REJECTED_PAGE_SIZE })));
    const rows = [];
    scanned.forEach((imp, idx) => {
      for (const r of pages[idx]?.items || []) {
        if (!isDuplicateRejection(r)) continue;
        const reason = rejectionReasons(r).join('; ');
        rows.push({
          key: `${imp.id}:${r.rowNumber}`,
          importName: imp.fileName,
          rowNumber: r.rowNumber,
          telemetryId: duplicateTelemetryId(r),
          reason,
          // Only the documented in-file duplicate reason maps to a known ETL stage.
          stage: reason === IN_FILE_DUPLICATE_REASON ? 'Deduplication (in-file)' : 'Unknown',
        });
      }
    });
    return { imports: items, rows };
  }, []);

  if (error && !data) return <ErrorState error={error} onRetry={reload} title="Could not load duplicate details" />;
  if (!data) return <SkeletonTable rows={3} cols={5} />;

  const alreadyStored = data.imports.reduce((sum, i) => sum + (i.duplicateRows || 0), 0);
  const importsWithDuplicates = data.imports.filter((i) => (i.duplicateRows || 0) > 0 || data.rows.some((r) => r.importName === i.fileName));
  const knownIds = new Set(data.rows.map((r) => r.telemetryId).filter(Boolean));
  if (data.rows.length === 0 && alreadyStored === 0) {
    return <EmptyState icon="check" title="No duplicate records recorded" message="No import reported a duplicate telemetry id." />;
  }

  return (
    <div className="stack" aria-busy={loading}>
      <dl className="dl">
        <div>
          <dt>Duplicate rows rejected</dt>
          <dd>{formatNumber(data.rows.length)}</dd>
        </div>
        <div>
          <dt>Already stored (skipped)</dt>
          <dd>{formatNumber(alreadyStored)}</dd>
        </div>
        <div>
          <dt>Duplicate groups (distinct telemetry ids)</dt>
          <dd>{knownIds.size ? formatNumber(knownIds.size) : <span className="muted">Unknown</span>}</dd>
        </div>
        <div>
          <dt>Imports affected</dt>
          <dd>{formatNumber(importsWithDuplicates.length)}</dd>
        </div>
      </dl>
      <p className="help-text" style={{ margin: 0 }}>
        {DUPLICATE_RULE_NOTE}
      </p>
      <DataTable
        rowKey="key"
        caption="Duplicate records"
        columns={[
          { key: 'telemetryId', header: 'Telemetry id', render: (r) => (r.telemetryId ? <span className="mono">{r.telemetryId}</span> : <span className="muted">Unknown</span>) },
          { key: 'importName', header: 'Import', render: (r) => <span className="mono">{r.importName}</span> },
          { key: 'rowNumber', header: 'Row', numeric: true },
          { key: 'reason', header: 'Rejection reason' },
          { key: 'stage', header: 'Stage', render: (r) => (r.stage === 'Unknown' ? <span className="muted">Unknown</span> : r.stage) },
        ]}
        rows={data.rows}
        emptyTitle="No duplicate rows were stored for review"
      />
      <DataTable
        rowKey="id"
        caption="Duplicate counters by import"
        columns={[
          { key: 'fileName', header: 'Import', render: (i) => <span className="mono">{i.fileName}</span> },
          { key: 'totalRows', header: 'Total', numeric: true, render: (i) => formatNumber(i.totalRows) },
          { key: 'acceptedRows', header: 'Accepted', numeric: true, render: (i) => formatNumber(i.acceptedRows) },
          { key: 'rejectedRows', header: 'Rejected', numeric: true, render: (i) => formatNumber(i.rejectedRows) },
          { key: 'duplicateRows', header: 'Already stored', numeric: true, render: (i) => formatNumber(i.duplicateRows) },
        ]}
        rows={importsWithDuplicates}
        emptyTitle="No import reported duplicate counters"
      />
    </div>
  );
}

export function DataQualityPage() {
  const toast = useToast();
  const { values, setFilters } = useListQuery();
  const fromUtc = fromInputDate(values.from);
  const toUtc = fromInputDate(values.to, true);
  const { data, error, reload, loading } = useAsync(
    () => telemetryApi.dataQuality({ fromUtc, toUtc, aircraftId: values.aircraftId || undefined }),
    [fromUtc, toUtc, values.aircraftId],
  );
  const exportCsv = async () => {
    try {
      await reportsApi.exportCsv('dataQuality');
      toast.success('Data quality CSV downloaded.');
    } catch (err) {
      toast.error(err);
    }
  };
  return (
    <>
      <PageHeader
        eyebrow="Data"
        title="Data Quality"
        subtitle="Completeness, validity and duplicate evidence for ingested telemetry."
        actions={
          <button type="button" className="btn btn--secondary" onClick={exportCsv}>
            <Icon name="download" size={16} /> Export CSV
          </button>
        }
      />
      <div className="card" style={{ marginBottom: 24 }}>
        <div className="filter-bar" role="search" style={{ borderBottom: 0 }}>
          <AircraftSelect value={values.aircraftId} onChange={(v) => setFilters({ aircraftId: v })} placeholder="All aircraft" />
          <TextField label="From (UTC)" type="date" value={values.from || ''} onChange={(e) => setFilters({ from: e.target.value })} />
          <TextField label="To (UTC)" type="date" value={values.to || ''} onChange={(e) => setFilters({ to: e.target.value })} />
        </div>
      </div>
      {error && !data ? (
        <div className="card">
          <ErrorState error={error} onRetry={reload} />
        </div>
      ) : !data ? (
        <>
          <SkeletonKpis count={5} />
          <div className="card">
            <SkeletonTable />
          </div>
        </>
      ) : (
        <div aria-busy={loading}>
          <DataQualityView data={data} />
        </div>
      )}
      <div className="section-gap">
        <Card title="Duplicate details">
          <DuplicateDetails />
        </Card>
      </div>
    </>
  );
}
