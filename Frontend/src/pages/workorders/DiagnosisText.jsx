import { Icon } from '../../components/Icon.jsx';

const ACTION_HEADING = /^recommended action/i;
const BASIS_HEADING = /^basis/i;

/**
 * Splits the API's plain-text diagnosis into sections: blocks separated by blank lines, each an optional heading line
 * followed by "- " items. Rendered as text only (never HTML).
 */
export function parseDiagnosis(text) {
  return String(text || '')
    .split(/\r?\n\s*\r?\n/)
    .map((block) => block.split(/\r?\n/).map((l) => l.trim()).filter(Boolean))
    .filter((lines) => lines.length)
    .map((lines) => {
      const heading = lines[0].startsWith('- ') ? null : lines[0];
      return { heading, items: (heading ? lines.slice(1) : lines).map((l) => l.replace(/^-\s+/, '')) };
    });
}

/**
 * The stored diagnosis as readable sections: the concern as a callout, recommended actions as a list, and the basis as
 * a footnote. High-contrast text throughout (slate-900 body, slate-600 secondary - both above 4.5:1 on white/slate-50).
 */
export function DiagnosisText({ text, testId = 'diagnosis-text' }) {
  const sections = parseDiagnosis(text);
  const actions = sections.filter((s) => ACTION_HEADING.test(s.heading || ''));
  const basis = sections.filter((s) => BASIS_HEADING.test(s.heading || ''));
  const concerns = sections.filter((s) => !actions.includes(s) && !basis.includes(s));
  return (
    <div className="flex flex-col gap-3 text-[14px] leading-relaxed text-slate-900" data-testid={testId}>
      {concerns.map((s, i) => (
        <section key={`c${i}`} className="rounded-lg border border-slate-200 bg-slate-50 p-4" data-testid="diagnosis-concern">
          {s.heading ? <h4 className="m-0 mb-1 text-[14px] font-semibold text-slate-900">{s.heading}</h4> : null}
          <ul className="m-0 flex list-disc flex-col gap-1 pl-5">
            {s.items.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </section>
      ))}
      {actions.map((s, i) => (
        <section key={`a${i}`} className="rounded-lg border border-slate-200 bg-white p-4" data-testid="diagnosis-actions">
          <h4 className="m-0 mb-1 flex items-center gap-1.5 text-[14px] font-semibold text-slate-900">
            <Icon name="wrench" size={15} className="text-slate-600" />
            {s.heading}
          </h4>
          <ul className="m-0 flex list-disc flex-col gap-1 pl-5">
            {s.items.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </section>
      ))}
      {basis.map((s, i) => (
        <p key={`b${i}`} className="m-0 text-[12px] text-slate-600" data-testid="diagnosis-basis">
          <span className="font-semibold">{s.heading}:</span> {s.items.join(' ')}
        </p>
      ))}
    </div>
  );
}
