import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ForecastValidation } from '../pages/models/ForecastValidation.jsx';

const metrics = {
  validation_criteria: { max_mae_ratio_vs_best_naive: 0.95, coverage_band: [0.7, 0.9], min_eval_origins: 5 },
  forecast_validation: {
    OneDay: {
      temperatureC: { supported: true, checks: { beats_both_naive_baselines: true, interval_coverage_in_band: true }, mae_model: 122.06, mae_persistence: 129.36, mae_seasonal_naive: 137.05, mae_ratio_vs_best_naive: 0.944, interval_coverage: 0.78, eval_origins: 27 },
      pressurePsi: { supported: false, checks: { beats_both_naive_baselines: false, interval_coverage_in_band: true }, mae_model: 8.24, mae_persistence: 8.22, mae_seasonal_naive: 9.3, mae_ratio_vs_best_naive: 1.002, interval_coverage: 0.78, eval_origins: 27 },
    },
    ThirtyDays: { vibrationIndex: { supported: false, reason: 'not enough history for this horizon' } },
  },
};

describe('forecast validation evidence', () => {
  it('shows each horizon and parameter with its verdict and the numbers behind it', () => {
    render(<ForecastValidation metrics={metrics} />);
    expect(screen.getByTestId('forecast-validation')).toHaveTextContent('at most 0.95 × the better naive baseline');
    expect(screen.getByTestId('fv-OneDay-temperatureC')).toHaveTextContent(/1 DayEngine temperatureSupported122\.06129\.36137\.050\.94478%27/);
    expect(screen.getByTestId('fv-OneDay-pressurePsi')).toHaveTextContent('Not supported');
    expect(screen.getByTestId('fv-ThirtyDays-vibrationIndex')).toHaveTextContent('Not supported - not enough history for this horizon');
    expect(screen.queryByTestId('fv-SevenDays-temperatureC')).not.toBeInTheDocument();
  });

  it('renders nothing for a model without forecast evidence', () => {
    const { container } = render(<ForecastValidation metrics={{ precision: 0.5 }} />);
    expect(container).toBeEmptyDOMElement();
  });
});
