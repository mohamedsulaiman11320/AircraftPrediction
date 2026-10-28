# Aircraft Failure Prediction Platform — Frontend

React 18 + Vite 6 single-page app (plain JavaScript/JSX, plain CSS, no UI/chart/router libraries).
It implements the contract in [`../docs/API.md`](../docs/API.md).

## Scripts

| Command | What it does |
|---|---|
| `npm install` | Install the pinned dependencies |
| `npm run dev` | Dev server on http://localhost:5173 (`--strictPort`) |
| `npm run build` | Production build to `dist/` |
| `npm run preview` | Serve the production build on port 5173 |
| `npm test` | Run the Vitest + Testing Library suite once (jsdom) |
| `npm run test:watch` | Vitest in watch mode |

## Environment

Copy `.env.example` to `.env.local` (not committed) and adjust:

| Variable | Default | Meaning |
|---|---|---|
| `VITE_API_BASE_URL` | `http://localhost:5080` | ASP.NET Core API origin; requests go to `{base}/api/v1/...` |
| `VITE_DEMO_MODE` | `false` | `true` serves fictional fixtures from `src/demo/` instead of the API and shows a persistent "DEMO DATA — not from the live API" banner. Demo sign-in: username `dispatcher`, `technician` or `dataengineer`, any password. A raw JSON upload whose file name contains `fail` ends in Failed so Retry can be tried. |

The backend must allow CORS from `http://localhost:5173` for `GET, POST, PUT, PATCH` with the
`Authorization` and `Content-Type` headers (and should expose `Content-Disposition` for CSV file names).

## Structure

```
src/
  main.jsx, App.jsx          entry, providers, route wrapping (auth/role guards + app shell)
  router/                    custom History-API router: matchPath, Router/Routes/useParams/useQuery,
                             Link/NavLink, guards (RequireAuth/RequireRole), access.js (role table + nav),
                             routes.jsx (route table)
  api/                       client.js (fetch wrapper, bearer token, ProblemDetails -> ApiError, 401 handling,
                             FormData upload, CSV download) + one module per API area
  auth/                      AuthContext (session in sessionStorage), roles helpers, password policy
  components/                Sidebar, TopNav, AppLayout, DataTable, Pagination, SearchFilterBar, badges,
                             Modal/ConfirmDialog (focus trap), form fields, Toasts, skeletons, states,
                             charts/ (LineChart, BarChart, DonutChart, Sparkline — hand-rolled SVG)
  hooks/                     useAsync/useApi, useDebounce, usePolling, useListQuery (URL-backed filters)
  pages/                     auth, dashboards (per role), fleet, telemetry/imports/data quality, anomalies,
                             predictions, work orders, pipelines, models, reports, audit, admin
  demo/                      demo fixtures + in-memory adapter (loaded only when VITE_DEMO_MODE=true)
  styles/                    tokens.css, base.css, layout.css, components.css
  __tests__/, test/          Vitest suites and helpers
```

## Roles (RBAC v2)

* **FlightDispatcher** — administrator: every screen and action (dashboards, fleet, telemetry, ML, reports, audit, user approval/roles).
* **MaintenanceTechnician** — only *My tickets*: own assigned work orders with the embedded aircraft summary and AI diagnosis; start inspection, record findings, resolve, log maintenance.
* **DataEngineer** — Data area (telemetry explorer, CSV and raw JSON imports, import history/rejected rows/retry, data quality) and Model area (pipelines, models with register/activate/retire but not approve/reject, read-only anomalies and predictions, data-quality and model-performance reports), plus the Data & models overview. Aircraft list only for filter dropdowns; no aircraft detail, work orders, users or audit.
* Raw JSON telemetry (`POST /telemetry/raw-uploads`) is queued for the Spark ETL: the Imports page polls `GET /telemetry/imports/{id}` through Queued → Processing (Spark ETL) → Completed/Failed and offers Retry for failed raw JSON imports.
* Role names appear only in `src/router/access.js`; role pickers are loaded from `GET /api/v1/admin/roles`.
* Sessions hold an access token and a refresh token (sessionStorage). A 401 triggers one single-flight `POST /auth/refresh` and a retry; if that fails the session is cleared and the user is sent to `/login?reason=expired`.

## Notes

* Role checks in the UI (hidden navigation, route guards, action buttons) are convenience only — the
  server enforces every permission.
* Probabilities are displayed only when the API returns `probabilityValidated: true`; otherwise the UI shows
  "Not validated — descriptive indicator". Anomaly scores are labelled as not being probabilities.
* Dispatch advisories always show the server disclaimer; the UI never describes an aircraft as "safe".
