import { aircraftApi } from '../../api/aircraft.js';
import { aircraftLabel } from '../../utils/format.js';
import { useAsync } from '../../hooks/useAsync.js';
import { SelectField } from '../../components/FormFields.jsx';

let cache = null;
/**
 * Loads up to 200 aircraft for pickers (cached for the session).
 *
 * Each option also carries the aircraft row it came from (`aircraft`), so a picker that needs the
 * registration, `latestRisk` or the `health` block of the selected aircraft reads it off the option
 * instead of issuing a second request. `SelectField` only reads `value` and `label`.
 */
export function useAircraftOptions() {
  return useAsync(async () => {
    if (cache) return cache;
    const res = await aircraftApi.list({ page: 1, pageSize: 200 });
    cache = (res?.items || []).map((a) => ({ value: a.id, label: aircraftLabel(a), aircraft: a }));
    setTimeout(() => {
      cache = null;
    }, 60000);
    return cache;
  }, []);
}

export function AircraftSelect({ label = 'Aircraft', value, onChange, placeholder = 'Select aircraft…', ...rest }) {
  const { data, loading, error } = useAircraftOptions();
  return (
    <SelectField
      label={label}
      value={value || ''}
      onChange={(e) => onChange(e.target.value)}
      options={data || []}
      placeholder={loading ? 'Loading aircraft…' : error ? 'Could not load aircraft' : placeholder}
      {...rest}
    />
  );
}
