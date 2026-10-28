import { useMemo, useState } from 'react';
import { EmptyState } from './States.jsx';

function compare(a, b) {
  if (a === b) return 0;
  if (a === null || a === undefined || a === '') return 1;
  if (b === null || b === undefined || b === '') return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  if (typeof a === 'boolean' && typeof b === 'boolean') return a === b ? 0 : a ? -1 : 1;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

/**
 * Accessible data table with client-side sortable headers (sorts the rows it is given,
 * i.e. the current page for server-paged data).
 *
 * columns: [{ key, header, render?(row), sortable?, sortValue?(row), numeric?, className? }]
 */
export function DataTable({
  columns,
  rows,
  rowKey = 'id',
  caption,
  captionHidden = true,
  emptyTitle = 'No records',
  emptyMessage,
  initialSort = null,
  dense = false,
  fixed = false,
}) {
  const [sort, setSort] = useState(initialSort); // { key, dir: 'asc'|'desc' }

  const sorted = useMemo(() => {
    if (!sort || !rows) return rows || [];
    const col = columns.find((c) => c.key === sort.key);
    if (!col) return rows;
    const getter = col.sortValue || ((r) => r[col.key]);
    const out = [...rows].sort((a, b) => compare(getter(a), getter(b)));
    return sort.dir === 'desc' ? out.reverse() : out;
  }, [rows, sort, columns]);

  const toggleSort = (key) => {
    setSort((s) => {
      if (!s || s.key !== key) return { key, dir: 'asc' };
      if (s.dir === 'asc') return { key, dir: 'desc' };
      return null;
    });
  };

  if (!rows || rows.length === 0) {
    return <EmptyState title={emptyTitle} message={emptyMessage} />;
  }

  return (
    <div className="table-wrap">
      <table className={`data-table${fixed ? ' data-table--fixed' : ''}`} style={dense ? { fontSize: 'var(--fs-xs)' } : undefined}>
        {caption ? <caption className={captionHidden ? 'visually-hidden' : undefined}>{caption}</caption> : null}
        <thead>
          <tr>
            {columns.map((col) => {
              const active = sort?.key === col.key;
              const ariaSort = active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : col.sortable ? 'none' : undefined;
              return (
                <th key={col.key} scope="col" aria-sort={ariaSort} className={col.numeric ? 'num' : undefined} style={col.width ? { width: col.width } : undefined}>
                  {col.sortable ? (
                    <button type="button" className="data-table__sort" onClick={() => toggleSort(col.key)}>
                      {col.header}
                      <span className="data-table__sort-icon" aria-hidden="true">
                        {active ? (sort.dir === 'asc' ? '▲' : '▼') : '↕'}
                      </span>
                    </button>
                  ) : (
                    col.header
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {sorted.map((row, i) => (
            <tr key={typeof rowKey === 'function' ? rowKey(row, i) : (row[rowKey] ?? i)}>
              {columns.map((col) => (
                <td key={col.key} className={[col.numeric ? 'num' : '', col.className || ''].join(' ').trim() || undefined}>
                  {col.render ? col.render(row) : (row[col.key] ?? '—')}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
