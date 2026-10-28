export function Skeleton({ width = '100%', height = 14, radius, style, className = '' }) {
  return (
    <span
      className={`skeleton ${className}`}
      style={{ width, height, borderRadius: radius, ...style }}
      aria-hidden="true"
    />
  );
}

export function SkeletonText({ lines = 3 }) {
  return (
    <div className="stack" style={{ gap: 8 }} aria-hidden="true">
      {Array.from({ length: lines }, (_, i) => (
        <Skeleton key={i} width={i === lines - 1 ? '60%' : '100%'} />
      ))}
    </div>
  );
}

export function SkeletonTable({ rows = 5, cols = 5 }) {
  return (
    <div className="card__body" role="status" aria-label="Loading">
      <span className="visually-hidden">Loading…</span>
      <div className="stack" style={{ gap: 12 }} aria-hidden="true">
        {Array.from({ length: rows }, (_, r) => (
          <div key={r} style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, 1fr)`, gap: 16 }}>
            {Array.from({ length: cols }, (_, c) => (
              <Skeleton key={c} height={12} />
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}

export function SkeletonKpis({ count = 4 }) {
  return (
    <div className="grid grid--kpi" role="status" aria-label="Loading">
      <span className="visually-hidden">Loading…</span>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="card kpi" aria-hidden="true">
          <Skeleton width="50%" height={10} />
          <Skeleton width="40%" height={26} style={{ marginTop: 8 }} />
        </div>
      ))}
    </div>
  );
}

export function SkeletonPage() {
  return (
    <div role="status" aria-label="Loading page">
      <span className="visually-hidden">Loading…</span>
      <Skeleton width="30%" height={28} />
      <div style={{ height: 24 }} />
      <SkeletonKpis />
      <div className="card">
        <SkeletonTable />
      </div>
    </div>
  );
}
