import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { ForecastDiagnosis } from '../pages/anomalies/ForecastDiagnosis.jsx';
import { aircraftLabel } from '../utils/format.js';
import { DiagnosisText, parseDiagnosis } from '../pages/workorders/DiagnosisText.jsx';

/** The evidence a forecast anomaly stores (shape written by AnomalyProcessingService). */
const EVIDENCE = {
  horizon: 'ThirtyDays',
  dataAsOfUtc: '2026-10-27T18:00:00Z',
  forecastModelVersion: '20261028053845',
  forecastModelVersionId: '686701f3-5a60-43e1-8137-ada8cf1648c7',
  breaches: [
    { code: 'OilPressureLowPsi', unit: 'psi', limit: 35, points: 720, extreme: 29.3, firstUtc: '2026-10-27T19:00:00Z', lastUtc: '2026-11-26T18:00:00Z', comparison: 'LessThan', measurement: 'OilPressure', recommendedInspection: 'Inspect the oil pressure regulator.' },
    { code: 'EngineTemperatureHighC', unit: 'C', limit: 700, points: 720, extreme: 761.9, firstUtc: '2026-10-27T19:00:00Z', lastUtc: '2026-11-26T18:00:00Z', comparison: 'GreaterThan', measurement: 'EngineTemperature', recommendedInspection: 'Immediate thermal shroud and turbine inspection.' },
  ],
};

describe('forecast diagnosis', () => {
  it('reads as plain English rows, with model identifiers in a closed technical section and no raw JSON', () => {
    render(<ForecastDiagnosis evidence={EVIDENCE} />);
    const [oil, temp] = screen.getAllByTestId('forecast-breach-row');
    expect(oil).toHaveTextContent('Oil pressure low (29.3 PSI vs 35.0 PSI limit)');
    expect(oil).toHaveTextContent('Inspect the oil pressure regulator.');
    expect(temp).toHaveTextContent('Engine temperature high (761.9 °C vs 700.0 °C limit)');
    expect(temp).toHaveTextContent('Immediate thermal shroud and turbine inspection.');
    expect(oil).not.toHaveTextContent('2026-10-27T19:00:00Z');                       // formatted, never raw
    const tech = screen.getByTestId('technical-details');
    expect(tech).not.toHaveAttribute('open');
    expect(within(tech).getByText('20261028053845')).toBeInTheDocument();
    expect(screen.getByTestId('forecast-diagnosis')).not.toHaveTextContent('{');
    expect(screen.getByTestId('forecast-diagnosis')).not.toHaveTextContent('OilPressureLowPsi');
  });

  it('labels an aircraft by its tail number alone when the model is unknown', () => {
    expect(aircraftLabel({ registrationNumber: 'N120UA', model: 'Unknown' })).toBe('N120UA');
    expect(aircraftLabel({ registrationNumber: 'N120UA', model: 'A320neo' })).toBe('N120UA · A320neo');
  });
});

/** Diagnosis text exactly as WorkOrderDiagnosisService writes it for a forecast incident. */
const DIAGNOSIS = `Forecast concern for N120UA
- Engine exhaust temperature is forecast to rise above 700 °C from 2026-10-27 19:00 UTC (highest forecast 755.9 °C).

Recommended action
- Immediate thermal shroud and turbine inspection.
- Plan the inspection before the forecast date. The forecast follows this aircraft's recent readings; it is not a confirmed fault.

Basis
- 30-day sensor forecast (model 20261028053845) from readings up to 2026-10-27 18:00 UTC.`;

describe('diagnosis text', () => {
  it('splits the plain text into headed sections of items', () => {
    const sections = parseDiagnosis(DIAGNOSIS);
    expect(sections.map((s) => s.heading)).toEqual(['Forecast concern for N120UA', 'Recommended action', 'Basis']);
    expect(sections[1].items).toHaveLength(2);
    expect(sections[0].items[0]).toMatch(/^Engine exhaust temperature/);       // the "- " marker is removed
  });

  it('renders a concern callout, an action list and a basis footnote in dark text - no <pre>, no HTML', () => {
    const { container } = render(<DiagnosisText text={`${DIAGNOSIS}
- <b>not html</b>`} />);
    expect(container.querySelector('pre')).toBeNull();
    expect(container.querySelector('b')).toBeNull();
    expect(screen.getByTestId('diagnosis-concern')).toHaveTextContent('Forecast concern for N120UA');
    expect(within(screen.getByTestId('diagnosis-actions')).getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByTestId('diagnosis-basis')).toHaveTextContent('Basis: 30-day sensor forecast (model 20261028053845)');
    expect(screen.getByTestId('diagnosis-text').className).toContain('text-slate-900');
  });
});
