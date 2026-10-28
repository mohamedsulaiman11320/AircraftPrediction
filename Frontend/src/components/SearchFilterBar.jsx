import { useEffect, useState } from 'react';
import { useDebounce } from '../hooks/useDebounce.js';

/**
 * Row of filters above a table.
 * filters: [{ name, label, type: 'search'|'select'|'date'|'datetime'|'checkbox', options?, placeholder? }]
 * values: object; onChange(patch) receives partial updates (search is debounced).
 */
export function SearchFilterBar({ filters, values, onChange, onReset, children }) {
  return (
    <div className="filter-bar" role="search">
      {filters.map((f) => (
        <FilterControl key={f.name} filter={f} value={values[f.name] ?? ''} onChange={onChange} />
      ))}
      {children}
      {onReset ? (
        <div className="filter-bar__actions">
          <button type="button" className="btn btn--ghost btn--sm" onClick={onReset}>
            Reset filters
          </button>
        </div>
      ) : null}
    </div>
  );
}

function FilterControl({ filter, value, onChange }) {
  const id = `filter-${filter.name}`;
  if (filter.type === 'search') return <SearchInput filter={filter} value={value} onChange={onChange} id={id} />;
  if (filter.type === 'checkbox') {
    return (
      <div className="field" style={{ flex: '0 0 auto', minWidth: 0 }}>
        <span className="field__label" aria-hidden="true">
          &nbsp;
        </span>
        <label className="row" htmlFor={id} style={{ minHeight: 40 }}>
          <input
            id={id}
            type="checkbox"
            checked={value === true || value === 'true'}
            onChange={(e) => onChange({ [filter.name]: e.target.checked ? 'true' : '' })}
            style={{ width: 18, height: 18, accentColor: 'var(--color-accent)' }}
          />
          <span>{filter.label}</span>
        </label>
      </div>
    );
  }
  if (filter.type === 'select') {
    return (
      <div className="field">
        <label className="field__label" htmlFor={id}>
          {filter.label}
        </label>
        <select id={id} className="select" value={value} onChange={(e) => onChange({ [filter.name]: e.target.value })}>
          <option value="">{filter.placeholder || 'All'}</option>
          {(filter.options || []).map((o) => {
            const opt = typeof o === 'string' ? { value: o, label: o } : o;
            return (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            );
          })}
        </select>
      </div>
    );
  }
  return (
    <div className="field">
      <label className="field__label" htmlFor={id}>
        {filter.label}
      </label>
      <input
        id={id}
        className="input"
        type={filter.type === 'datetime' ? 'datetime-local' : filter.type || 'text'}
        value={value}
        onChange={(e) => onChange({ [filter.name]: e.target.value })}
      />
    </div>
  );
}

function SearchInput({ filter, value, onChange, id }) {
  const [text, setText] = useState(value);
  const debounced = useDebounce(text, 350);
  useEffect(() => {
    setText(value);
  }, [value]);
  useEffect(() => {
    if (debounced !== value) onChange({ [filter.name]: debounced });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debounced]);
  return (
    <div className="field field--search">
      <label className="field__label" htmlFor={id}>
        {filter.label}
      </label>
      <input
        id={id}
        className="input"
        type="search"
        value={text}
        placeholder={filter.placeholder}
        onChange={(e) => setText(e.target.value)}
      />
    </div>
  );
}
