import { useCallback, useMemo } from 'react';
import { useQuery } from '../router/Router.jsx';

/**
 * URL-backed list state (filters + page + pageSize) so lists are linkable and survive reloads.
 * Changing any filter resets the page to 1.
 */
export function useListQuery(defaults = {}, { maxPageSize = 200 } = {}) {
  const [query, setQuery] = useQuery();
  const values = useMemo(() => ({ ...defaults, ...query }), [query]); // eslint-disable-line react-hooks/exhaustive-deps
  const page = Math.max(1, parseInt(values.page, 10) || 1);
  const pageSize = Math.min(maxPageSize, Math.max(1, parseInt(values.pageSize, 10) || 25));

  const setFilters = useCallback((patch) => setQuery({ ...patch, page: undefined }), [setQuery]);
  const setPage = useCallback((p) => setQuery({ page: p > 1 ? p : undefined }), [setQuery]);
  const setPageSize = useCallback((s) => setQuery({ pageSize: s, page: undefined }), [setQuery]);
  const reset = useCallback(() => setQuery({}, { merge: false }), [setQuery]);

  return { values, page, pageSize, setFilters, setPage, setPageSize, reset };
}
