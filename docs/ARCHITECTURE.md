# Architecture

## System overview

```
 Browser (React 18 + Vite, :5173)
        │  HTTPS/JSON, Bearer JWT (60 min)
        ▼
 ASP.NET Core 10 Web API (:5080)  ──────────────►  PostgreSQL 17 (aircraft_predict_db, :5432)
   Presentation → Application → Infrastructure         ▲   role afp_predict_api (DML only)
        │ queues ML jobs as pipeline_run rows          │
        │ (never runs Python itself)                   │   roles afp_predict_ml_reader / afp_predict_etl_writer
        ▼                                              │   (ML tables only)
 Python 3.13 ML worker  (python -m afp_ml worker) ─────┘
   Isolation Forest, readiness audit, candidate failure models
   Artifacts: %LOCALAPPDATA%\AircraftFailurePrediction\models (SHA-256 verified)
```

The **API is the single business authority**: authentication, authorisation, workflow rules, status
changes and audit all live there. The **ML subsystem is a separate process** that communicates only
through documented tables (`pipeline_run` as a job queue, plus its output tables) under least-privilege
database roles. It cannot read or modify users, tokens, security audit, work orders or inspection flags.

## Backend solution (`Backend/`)

| Project | Tier | Responsibility | Depends on |
|---|---|---|---|
| `AFP.Domain` | Domain | Entities, enums, pure business rules (`WorkOrderWorkflow`, `AircraftStatusPolicy`, `DispatchAdvisoryPolicy`, `ModelLifecycle`) | — |
| `AFP.Application` | Application / business logic | DTOs, service interfaces + implementations, validation, repository & unit-of-work **interfaces**, CSV validation and import processing | Domain |
| `AFP.Infrastructure` | Data access / infrastructure | EF Core `AppDbContext`, migrations, repository + `UnitOfWork` implementations, PBKDF2 hasher, JWT issuing, email senders, import file store | Application, Domain |
| `AFP.Api` | Presentation | Controllers (thin), HTTP contracts, authorization policies, ProblemDetails, rate limiting, health checks, Swagger, background import worker, CLI commands | Application, Infrastructure |
| `AFP.UnitTests` | Tests | Domain rules, validation, CSV, services against real repositories (EF in-memory) | all src |
| `AFP.IntegrationTests` | Tests | WebApplicationFactory API tests (auth, 4-role matrix, errors, import), Testcontainers PostgreSQL tests | Api |

Dependency rule: Presentation → Application → Domain; Infrastructure implements Application
interfaces and is wired only in the composition root (`Startup.cs`). Controllers contain no business
logic: each action calls one application service method.

### SOLID in practice

* **S** — one service per use-case area (`AuthService`, `WorkOrderService`, `TelemetryImportProcessor`, …);
  pure rule classes in the domain.
* **O** — new report formats / email transports / file stores are added as new implementations of
  `IReportService.ToCsv`, `IEmailSender`, `IImportFileStore`.
* **L/I** — focused interfaces (`IAircraftRepository`, `ITelemetryRepository`, `IMaintenanceRepository`,
  `IAnomalyRepository`, `IPredictionRepository`, `IUserRepository`, `IModelVersionRepository`,
  `IPipelineRunRepository`, `IAuditRepository`, `IUnitOfWork`, `IClock`, `ICurrentUser`, …).
* **D** — services depend on abstractions; constructor injection everywhere; no inheritance hierarchies
  beyond small DTO records and the `AppException` family used for error mapping.

### Cross-cutting

| Concern | Implementation |
|---|---|
| Errors | `GlobalExceptionHandler` → RFC 9457 ProblemDetails with `code` and `traceId`; model-binding errors use the same shape; empty 401/403/404 bodies become ProblemDetails via `UseStatusCodePages`. |
| Validation | `Validator` (application layer) — every request DTO is validated in its service. |
| Transactions | `IUnitOfWork.SaveChangesAsync` (one SaveChanges per use case) and `ExecuteInTransactionAsync` for multi-step operations (telemetry import, model activation). Unique-violation and concurrency failures become 409. |
| Concurrency | Work orders carry a `concurrency_stamp` checked on every write. |
| Logging | Console (Development) / JSON console (other environments), with request correlation via `TraceIdentifier`. |
| Rate limiting | Per-IP fixed window: auth endpoints (default 10/min) + global limit. |
| Health | `/health` (database check), `/health/live` (process only). |
| Background work | Telemetry CSV imports run in a hosted `TelemetryImportWorker` (Channel queue); ML jobs are queued for the Python worker. Both return job ids + status URLs (HTTP 202). |
| Paging | `PageRequest`/`PagedResult<T>` on every list endpoint (max 200, telemetry 1000). |

## Security model

* **Passwords**: ASP.NET Core Identity `PasswordHasher` (PBKDF2-HMAC-SHA512, v3 format), policy 12–128 chars
  with upper/lower/digit/symbol. Rehash on login if the format is upgraded.
* **Tokens (OAuth 2.0 style)**: login returns a 60-minute HMAC-SHA256 JWT access token (`jti`, security stamp, one
  `role` claim per assignment) plus an opaque 7-day refresh token stored only as a SHA-256 hash in `refresh_token`.
  `POST /auth/refresh` rotates it; presenting a consumed token revokes its whole family (theft detection). Every
  request is validated server-side (`SessionValidator`: user Active, has a role, stamp matches, `jti` not revoked).
  Logout revokes the `jti` and the refresh family; disable/role change/password reset rotate the stamp and revoke
  all refresh tokens.
* **Lockout**: 5 failed attempts -> 15-minute lockout (HTTP 423). Unknown usernames run a dummy hash to keep timing similar.
* **Registration**: username + email; always creates `Pending` accounts with no roles; identical response whether
  or not the username/email exists. A Flight Dispatcher approves and grants roles (rows in `user_role`).
* **Password reset**: by email; 256-bit random token, only its SHA-256 stored, 30-minute expiry, single use, previous
  tokens invalidated, identical response for unknown emails, delivery errors never surfaced.
* **First accounts**: `bootstrap-admin` CLI command (`--username --email --name [--role FlightDispatcher|DataEngineer]`,
  default FlightDispatcher); password from `AFP_BOOTSTRAP_ADMIN_PASSWORD`; refuses once an active account of that role
  exists. A fresh installation needs the DataEngineer bootstrap: only that role approves other accounts (2026-10-27).
* **Secrets**: none in source. DB credentials and the signing key come from the git-ignored root `.env` (or
  environment variables, which take precedence); the API refuses to start with a missing/short signing key.
* **Database isolation**: every entry point validates the target is `DB_NAME` (`aircraft_predict_db`) and never
  `aircraft_app`; `/health` verifies `current_database()` at runtime (see `docs/DATABASE.md`).
* **Lifecycle status**: every table carries `status_id` → seeded `status` table (0/1/2), separate from operational,
  workflow, ML prediction and anomaly-investigation states. Reference lists are seeded from CSV, never hard-coded.
* **Authorisation**: fallback policy requires authentication; every controller/action names a role policy
  (`Security/Authorization.cs`). Frontend guards are cosmetic only.

### RBAC (server-enforced, two roles)

Users -> `user_role` -> `role` (seeded). Policies: `Dispatcher`, `Technician`, `AnyRole` (`Security/Authorization.cs`);
row-level scoping of tickets is enforced in `WorkOrderService` / `MaintenanceLogService`.

| Capability | Flight Dispatcher (administrator) | Maintenance Technician |
|---|:-:|:-:|
| Dashboards (operations, fleet health, data & models), fleet, telemetry, anomalies, predictions, reports | ✔ | |
| Raise/clear inspection flags, change aircraft status (incl. release to Available) | ✔ | |
| Create, edit, assign, reopen, close, cancel work orders | ✔ | |
| Telemetry import, ML pipelines, register/approve/activate/retire models | ✔ | |
| Users (approve, roles, disable), audit | ✔ | |
| "My tickets": list/detail of **own assigned** work orders incl. aircraft summary and AI diagnosis | (all tickets) | ✔ (own only; others -> 404) |
| Start inspection, record findings, resolve with report | | ✔ (assigned technician only) |
| Log maintenance | ✔ | ✔ (only aircraft on own open ticket) |

**Data Engineer** (role 3, policy `DataAccess` = Dispatcher or Data Engineer): telemetry query/series, CSV and raw JSON imports with status/retry, data quality, pipelines, models (register/activate/retire — approval remains Dispatcher-only, restoring separation of duties for model approval), predictions and anomalies read-only, aircraft list and master data. No maintenance, dispatch, user administration or audit.

### Raw telemetry ETL

`Raw JSON → API upload → MinIO bronze/telemetry → PySpark (Docker) → aircraft_predict_db (local PostgreSQL) → API → Data/Model UI`. The Spark job connects as `afp_predict_ingest`, tracks every object in `telemetry_import`, loads each file in one transaction with idempotent inserts, and never derives failure labels. Details: `aircraft-fde-auto-etl-postgres/README.md`.

Separation of duties note: with two roles, the same dispatcher can approve and activate an ML model; every step is
recorded in `action_audit`.

## Business rules (documented decisions)

* **Operational status** (`Available`, `UnderMaintenance`, `Grounded`) is changed only by Maintenance
  Technicians with a reason. `Available` is refused while a `Grounding` inspection flag is active or a
  `Critical` work order is open. Raising a `Grounding` flag grounds the aircraft immediately; clearing it
  does **not** restore availability — a technician must release the aircraft explicitly. New aircraft
  start as `UnderMaintenance` pending an entry-into-service check.
* **ML never makes an aircraft dispatchable.** The dispatch advisory lists known restrictions
  (`NoKnownRestrictions` / `Restricted` / `NotAvailable`) and always carries a disclaimer; it never says "safe".
* **Anomaly scores are not probabilities.** Every anomaly DTO carries a note to that effect.
* **Probabilities** are returned only when `probability_validated` is true (enforced in the database by a
  check constraint and again in the mapper).
* **Failure prediction is BLOCKED** unless the latest dataset audit reports sufficient verified labels or an
  approved model is active. Leadership cannot approve a failure-prediction model while blocked, or one
  missing precision / recall / PR-AUC / false-alert-rate metrics.
* **Model lifecycle**: Trained → Registered (DE or ML CLI) → Approved (LD) → Active (DE; previous active retired)
  → Retired; Registered/Approved → Rejected (LD).
* **Work-order workflow** — see `docs/API.md`; closure writes a verified `maintenance_log` row (Unscheduled if a
  failure was confirmed, otherwise Inspection) that becomes a training label, and records the outcome on
  the linked prediction and anomaly.
* **Thresholds**: no sensor thresholds are invented. `engineering_threshold` exists for approved,
  specification-referenced limits only and ships empty; import validation applies physical plausibility only.

## Frontend (`Frontend/`)

React 18.3.1 + Vite 6.4.3, JavaScript/JSX, plain CSS (design tokens in `src/styles`), custom History-API
router, Fetch-based API client mapping ProblemDetails to `ApiError`. Role-aware navigation and route guards
mirror (but do not replace) server policies. Demo fixtures live in `src/demo` and are used only when
`VITE_DEMO_MODE=true`, with a permanent banner. See `Frontend/README.md`.

## ML subsystem (`ml/`)

See `docs/ML_PIPELINE.md`. Interface summary:

| Direction | Mechanism |
|---|---|
| API → ML | Insert `pipeline_run` (`status = Queued`, `trigger = Api`); worker claims with `FOR UPDATE SKIP LOCKED`. |
| ML → API | Writes `model_version` (Trained/Registered), `aircraft_anomaly_result`, `aircraft_risk_prediction`, and updates its `pipeline_run`. |
| Human gate | Approval (Leadership) and activation (Data Engineer) happen only through the API. |
