/**
 * Minimal path matching for the custom router.
 * Supports static segments, `:param` segments and a trailing `*` wildcard.
 */
const cache = new Map();

function escapeRegex(s) {
  return s.replace(/[.+?^${}()|[\]\\]/g, '\\$&');
}

export function compilePattern(pattern) {
  if (cache.has(pattern)) return cache.get(pattern);
  const keys = [];
  const normalized = pattern === '/' ? '/' : pattern.replace(/\/+$/, '');
  let source;
  if (normalized === '*') {
    source = '.*';
  } else {
    source = normalized
      .split('/')
      .filter((seg, i) => !(i === 0 && seg === ''))
      .map((seg) => {
        if (seg === '*') {
          keys.push('*');
          return '/(.*)';
        }
        if (seg.startsWith(':')) {
          keys.push(seg.slice(1));
          return '/([^/]+)';
        }
        return '/' + escapeRegex(seg);
      })
      .join('');
    if (source === '') source = '/';
  }
  const compiled = { regex: new RegExp('^' + source + '/?$', 'i'), keys };
  cache.set(pattern, compiled);
  return compiled;
}

/** Returns a params object when `pathname` matches `pattern`, otherwise null. */
export function matchPath(pattern, pathname) {
  const { regex, keys } = compilePattern(pattern);
  const m = regex.exec(pathname || '/');
  if (!m) return null;
  const params = {};
  keys.forEach((k, i) => {
    try {
      params[k] = decodeURIComponent(m[i + 1]);
    } catch {
      params[k] = m[i + 1];
    }
  });
  return params;
}

/** First route in `routes` whose `path` matches. Returns { route, params } or null. */
export function matchRoutes(routes, pathname) {
  for (const route of routes) {
    const params = matchPath(route.path, pathname);
    if (params) return { route, params };
  }
  return null;
}

/** Fill a pattern with params: buildPath('/fleet/:id', { id: 7 }) -> '/fleet/7'. */
export function buildPath(pattern, params = {}) {
  return pattern.replace(/:([A-Za-z0-9_]+)/g, (_, k) => encodeURIComponent(params[k] ?? ''));
}

/** Parse a search string into a plain object (last value wins). */
export function parseQuery(search) {
  const out = {};
  new URLSearchParams(search || '').forEach((v, k) => {
    out[k] = v;
  });
  return out;
}

/** Serialise an object to a query string, skipping empty values. Includes leading '?' when non-empty. */
export function toQueryString(params = {}) {
  const usp = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v === undefined || v === null || v === '' || v === false) return;
    usp.set(k, String(v));
  });
  const s = usp.toString();
  return s ? `?${s}` : '';
}
