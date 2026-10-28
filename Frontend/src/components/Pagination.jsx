export function Pagination({ page = 1, pageSize = 25, totalCount = 0, totalPages, onPageChange, onPageSizeChange, pageSizes = [10, 25, 50, 100] }) {
  const pages = totalPages ?? Math.max(1, Math.ceil(totalCount / pageSize));
  const from = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, totalCount);
  return (
    <nav className="pagination" aria-label="Pagination">
      <span aria-live="polite">
        {totalCount === 0 ? 'No results' : `Showing ${from}–${to} of ${totalCount.toLocaleString()}`}
      </span>
      <div className="pagination__controls">
        {onPageSizeChange ? (
          <label className="row" style={{ gap: 6 }}>
            <span>Rows</span>
            <select className="select" style={{ minHeight: 30, width: 'auto', padding: '2px 8px' }} value={pageSize} onChange={(e) => onPageSizeChange(Number(e.target.value))}>
              {pageSizes.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        <button type="button" className="btn btn--secondary btn--sm" onClick={() => onPageChange(page - 1)} disabled={page <= 1}>
          Previous
        </button>
        <span>
          Page {Math.min(page, Math.max(pages, 1))} of {Math.max(pages, 1)}
        </span>
        <button type="button" className="btn btn--secondary btn--sm" onClick={() => onPageChange(page + 1)} disabled={page >= pages}>
          Next
        </button>
      </div>
    </nav>
  );
}
