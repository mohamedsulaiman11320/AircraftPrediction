import { describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { buildPath, matchPath, matchRoutes, parseQuery, toQueryString } from '../router/matchPath.js';
import { Router, Routes, useParams, useQuery } from '../router/Router.jsx';
import { Link } from '../router/Link.jsx';

describe('matchPath', () => {
  it('matches static paths exactly (ignoring trailing slash and case)', () => {
    expect(matchPath('/fleet', '/fleet')).toEqual({});
    expect(matchPath('/fleet', '/fleet/')).toEqual({});
    expect(matchPath('/fleet', '/FLEET')).toEqual({});
    expect(matchPath('/fleet', '/fleet/x')).toBeNull();
    expect(matchPath('/', '/')).toEqual({});
    expect(matchPath('/', '/fleet')).toBeNull();
  });

  it('extracts and decodes params', () => {
    expect(matchPath('/fleet/:id', '/fleet/abc-123')).toEqual({ id: 'abc-123' });
    expect(matchPath('/fleet/:id/edit', '/fleet/a%20b/edit')).toEqual({ id: 'a b' });
    expect(matchPath('/a/:x/b/:y', '/a/1/b/2')).toEqual({ x: '1', y: '2' });
    expect(matchPath('/fleet/:id', '/fleet')).toBeNull();
  });

  it('supports the catch-all route', () => {
    expect(matchPath('*', '/anything/here')).toEqual({});
  });

  it('matchRoutes returns the first match so static routes win when listed first', () => {
    const routes = [{ path: '/fleet/new' }, { path: '/fleet/:id' }, { path: '*' }];
    expect(matchRoutes(routes, '/fleet/new').route.path).toBe('/fleet/new');
    expect(matchRoutes(routes, '/fleet/42')).toEqual({ route: routes[1], params: { id: '42' } });
    expect(matchRoutes(routes, '/nope').route.path).toBe('*');
  });

  it('builds paths and query strings', () => {
    expect(buildPath('/fleet/:id', { id: 'a/b' })).toBe('/fleet/a%2Fb');
    expect(toQueryString({ a: 1, b: '', c: null, d: undefined, e: false, f: 'x y' })).toBe('?a=1&f=x+y');
    expect(toQueryString({})).toBe('');
    expect(parseQuery('?status=Grounded&page=2')).toEqual({ status: 'Grounded', page: '2' });
  });
});

function Detail() {
  const { id } = useParams();
  const [query, setQuery] = useQuery();
  return (
    <div>
      <p>Aircraft {id}</p>
      <p>tab={query.tab || 'none'}</p>
      <button type="button" onClick={() => setQuery({ tab: 'telemetry' })}>
        set tab
      </button>
      <Link to="/fleet">Back</Link>
    </div>
  );
}

const routes = [
  {
    path: '/fleet',
    element: (
      <div>
        <h1>Fleet list</h1>
        <Link to="/fleet/A7">Open A7</Link>
      </div>
    ),
  },
  { path: '/fleet/:id', element: <Detail /> },
  { path: '*', element: <p>Not found</p> },
];

describe('Router', () => {
  it('navigates with Link via pushState, exposes params and query, and handles popstate', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/fleet');
    render(
      <Router>
        <Routes routes={routes} />
      </Router>,
    );
    expect(screen.getByRole('heading', { name: 'Fleet list' })).toBeInTheDocument();
    const before = window.history.length;
    await user.click(screen.getByRole('link', { name: 'Open A7' }));
    expect(window.location.pathname).toBe('/fleet/A7');
    expect(window.history.length).toBe(before + 1);
    expect(screen.getByText('Aircraft A7')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'set tab' }));
    expect(window.location.search).toBe('?tab=telemetry');
    expect(screen.getByText('tab=telemetry')).toBeInTheDocument();

    // Simulate browser back
    act(() => {
      window.history.replaceState(null, '', '/fleet');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    expect(screen.getByRole('heading', { name: 'Fleet list' })).toBeInTheDocument();
  });

  it('renders the fallback route for unknown paths', () => {
    window.history.replaceState(null, '', '/does/not/exist');
    render(
      <Router>
        <Routes routes={routes} />
      </Router>,
    );
    expect(screen.getByText('Not found')).toBeInTheDocument();
  });

  it('lets modified clicks through (open in new tab)', async () => {
    window.history.replaceState(null, '', '/fleet');
    render(
      <Router>
        <Routes routes={routes} />
      </Router>,
    );
    const link = screen.getByRole('link', { name: 'Open A7' });
    expect(link).toHaveAttribute('href', '/fleet/A7');
    const evt = new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true, button: 0 });
    // Record whether the router prevented the default, then stop jsdom from attempting a real navigation.
    let preventedByRouter = null;
    const onDocClick = (e) => {
      preventedByRouter = e.defaultPrevented;
      e.preventDefault();
    };
    document.addEventListener('click', onDocClick);
    act(() => {
      link.dispatchEvent(evt);
    });
    document.removeEventListener('click', onDocClick);
    expect(preventedByRouter).toBe(false);
    expect(window.location.pathname).toBe('/fleet');
  });
});
