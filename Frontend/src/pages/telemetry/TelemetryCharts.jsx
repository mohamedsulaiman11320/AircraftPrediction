import { telemetryApi } from '../../api/telemetry.js';
import { useAsync } from '../../hooks/useAsync.js';
import { LineChart } from '../../components/charts/LineChart.jsx';
import { ErrorState } from '../../components/States.jsx';
import { Skeleton } from '../../components/Skeleton.jsx';

/** Temperature (°C) and pressure (kPa) charts for one aircraft, from /telemetry/series. */
export function TelemetryCharts({ aircraftId, fromUtc, toUtc, sensorId, bucketMinutes = 60 }) {
  const { data, error, loading, reload } = useAsync(
    () => telemetryApi.series({ aircraftId, fromUtc, toUtc, sensorId: sensorId || undefined, bucketMinutes }),
    [aircraftId, fromUtc, toUtc, sensorId, bucketMinutes],
    { enabled: !!aircraftId },
  );
  if (!aircraftId) return null;
  if (error && !data) return <ErrorState error={error} onRetry={reload} title="Could not load telemetry series" />;
  if (!data)
    return (
      <div className="grid grid--2" role="status" aria-label="Loading charts">
        <Skeleton height={240} />
        <Skeleton height={240} />
      </div>
    );
  const points = data.points || [];
  const totalSamples = points.reduce((s, p) => s + (p.count || 0), 0);
  return (
    <div aria-busy={loading}>
      <p className="subtle" style={{ marginBottom: 12 }}>
        {points.length} buckets of {data.bucketMinutes} min · {totalSamples.toLocaleString()} samples (bucket averages)
      </p>
      <div className="grid grid--2">
        <div>
          <h3 style={{ fontSize: 'var(--fs-base)' }}>Temperature (°C)</h3>
          <LineChart
            ariaLabel="Average temperature"
            unit="°C"
            series={[{ name: 'Temperature', color: 'var(--chart-2)', data: points.map((p) => ({ x: p.timestampUtc, y: p.temperatureC })) }]}
            emptyText="No temperature readings in this range."
          />
        </div>
        <div>
          <h3 style={{ fontSize: 'var(--fs-base)' }}>Pressure (kPa)</h3>
          <LineChart
            ariaLabel="Average pressure"
            unit="kPa"
            series={[{ name: 'Pressure', color: 'var(--chart-1)', data: points.map((p) => ({ x: p.timestampUtc, y: p.pressureKpa })) }]}
            emptyText="No pressure readings in this range."
          />
        </div>
      </div>
    </div>
  );
}
