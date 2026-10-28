export const NOT_VALIDATED_TEXT = 'Not validated — descriptive indicator';

/**
 * Shows a failure probability ONLY when the server marks it as validated. Otherwise shows an
 * explicit "not validated" label - the UI never derives or invents a probability.
 */
export function ProbabilityValue({ prediction }) {
  const p = prediction?.probability;
  if (prediction?.probabilityValidated === true && p !== null && p !== undefined && Number.isFinite(Number(p))) {
    return (
      <span title="Validated model probability within the stated horizon">
        {(Number(p) * 100).toFixed(1)}%
      </span>
    );
  }
  return <span className="muted">{NOT_VALIDATED_TEXT}</span>;
}
