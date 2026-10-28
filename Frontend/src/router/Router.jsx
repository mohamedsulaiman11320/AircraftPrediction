import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useState } from 'react';
import { getLocation, navigate as historyNavigate, subscribe } from './history.js';
import { matchRoutes, parseQuery, toQueryString } from './matchPath.js';

const RouterContext = createContext(null);
const RouteContext = createContext({ params: {}, route: null });

export function Router({ children }) {
  const [location, setLocation] = useState(getLocation);

  // Layout effect so the listener exists before any child's passive effect (e.g. <Redirect>) navigates.
  useLayoutEffect(() => {
    const sync = () => setLocation(getLocation());
    const unsubscribe = subscribe(sync);
    sync();
    return unsubscribe;
  }, []);

  const navigate = useCallback((to, opts) => {
    historyNavigate(to, opts);
    if (!opts?.keepScroll) {
      try {
        window.scrollTo(0, 0);
      } catch {
        /* not implemented in jsdom */
      }
    }
  }, []);

  const value = useMemo(() => ({ ...location, navigate }), [location, navigate]);
  return <RouterContext.Provider value={value}>{children}</RouterContext.Provider>;
}

export function useRouter() {
  const ctx = useContext(RouterContext);
  if (!ctx) throw new Error('useRouter must be used inside <Router>');
  return ctx;
}

export function useLocation() {
  const { pathname, search } = useRouter();
  return { pathname, search };
}

export function useNavigate() {
  return useRouter().navigate;
}

/** Params of the currently rendered route, e.g. { id } for /fleet/:id. */
export function useParams() {
  return useContext(RouteContext).params;
}

/** { route, params } of the currently rendered route. */
export function useRoute() {
  return useContext(RouteContext);
}

/**
 * Query string state. Returns [queryObject, setQuery]. setQuery merges by default
 * and replaces the history entry so filter changes do not spam the back button.
 */
export function useQuery() {
  const { pathname, search, navigate } = useRouter();
  const query = useMemo(() => parseQuery(search), [search]);
  const setQuery = useCallback(
    (patch, { merge = true, replace = true } = {}) => {
      const next = merge ? { ...parseQuery(window.location.search), ...patch } : patch;
      navigate(pathname + toQueryString(next), { replace, keepScroll: true });
    },
    [pathname, navigate],
  );
  return [query, setQuery];
}

/** Renders the first matching route. Each route: { path, element | render(params) }. */
export function Routes({ routes, fallback = null, wrap }) {
  const { pathname } = useRouter();
  const match = matchRoutes(routes, pathname);
  if (!match) return fallback;
  const { route, params } = match;
  let content = route.render ? route.render(params) : route.element;
  if (wrap) content = wrap(route, content, params);
  return <RouteContext.Provider value={{ params, route }}>{content}</RouteContext.Provider>;
}

export function Redirect({ to }) {
  const navigate = useNavigate();
  useEffect(() => {
    navigate(to, { replace: true });
  }, [to, navigate]);
  return null;
}
