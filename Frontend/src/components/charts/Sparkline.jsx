/** Minimal inline trend line. */
export function Sparkline({ values = [], width = 120, height = 28, color = 'var(--chart-1)', label = 'Trend' }) {
  const nums = values.map((v) => Number(v) || 0);
  if (nums.length < 2) return null;
  const min = Math.min(...nums);
  const max = Math.max(...nums);
  const span = max - min || 1;
  const step = width / (nums.length - 1);
  const pts = nums.map((v, i) => `${(i * step).toFixed(1)},${(height - 3 - ((v - min) / span) * (height - 6)).toFixed(1)}`);
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${label}: ${nums.join(', ')}`}>
      <polyline points={pts.join(' ')} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

export const RISK_COLORS = {
  Low: 'var(--risk-low)',
  Elevated: 'var(--risk-elevated)',
  High: 'var(--risk-high)',
  Undetermined: 'var(--risk-undetermined)',
};
