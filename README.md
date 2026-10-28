# Aircraft Failure Prediction Platform

An aviation operations platform for fleet readiness, telemetry quality, anomaly detection and
maintenance workflow, with a **gated** path to predictive (failure) maintenance.

> **Safety position.** The platform supports, and never replaces, engineering judgement. Machine-learning
> output never makes an aircraft dispatchable. Isolation Forest scores are *not* probabilities. Failure
> prediction stays **BLOCKED** until an audit of real data finds enough verified maintenance labels, and
> no model accuracy or probability is shown unless it was measured and validated.

| Component | Technology | Default URL |
|---|---|---|
| Frontend | React 18.3.1, Vite 6.4.3, JavaScript/JSX, plain CSS | http://localhost:5173 |
| Backend API | ASP.NET Core (.NET 10), EF Core 10, Npgsql, JWT | http://localhost:5080 (Swagger: `/swagger`) |
| Database | PostgreSQL 17 (`aircraft_predict_db`) | localhost:5432 |
| ML | Python 3.13, pandas, scikit-learn, psycopg 3, joblib | separate process (`python -m afp_ml`) |

Documentation: [Architecture](docs/ARCHITECTURE.md) · [API](docs/API.md) · [Database](docs/DATABASE.md) · [ML pipeline](docs/ML_PIPELINE.md)

## Repository layout

```
AircraftFailurePrediction/
├─ Frontend/            React app (src/, tests alongside), .env.example
├─ Backend/
│  ├─ src/              AFP.Domain · AFP.Application · AFP.Infrastructure · AFP.Api
│  ├─ tests/            AFP.UnitTests · AFP.IntegrationTests
│  └─ .env.example      backend configuration reference
├─ ml/                  afp_ml package, tests, .env.example
├─ tools/
│  ├─ db/               provision-database.ps1, apply-migrations.ps1, apply-seed.ps1, DbCommon.ps1, *.sql, schema.sql
│  │  └─ seed/          datasets.json, csv/ (reference data), seed.sql (generated)
│  └─ data/             generate_synthetic_telemetry.py (development data only)
├─ docs/
└─ README.md
```

## Architecture in one paragraph

Three-tier backend: **Presentation** (`AFP.Api` controllers, DTO contracts, policies) → **Application**
(`AFP.Application` services, validation, business rules, repository/UoW interfaces) → **Infrastructure**
(`AFP.Infrastructure` EF Core, PostgreSQL, repositories, Unit of Work, JWT, email, file storage), plus a
dependency-free `AFP.Domain`. The Python ML subsystem runs as its own process and talks to the system only
through the `pipeline_run` job table and its own output tables, using database roles that cannot touch users,
tokens or audit records. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Prerequisites (Windows 11)

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet --list-sdks` |
| Node.js + npm | Node 20.19+ / 22+ | `node --version` |
| Python | 3.13 (via the `py` launcher) | `py -3.13 --version` |
| PostgreSQL | 17, running as the `postgresql-x64-17` Windows service | `Get-Service postgresql-x64-17` |
| Docker Desktop | optional; only for the Testcontainers PostgreSQL tests | `docker info` |

## 1. Configuration (`.env`)

All components read one git-ignored file at the repository root: **`.env`** (template: [.env.example](.env.example)).
It holds the database target (`DB_NAME=aircraft_predict_db`), the four dedicated role names and passwords, the JWT
signing key and ML settings. Real environment variables always take precedence over the file.

```powershell
cd C:\project\AirCraft\AircraftFailurePrediction
Copy-Item .env.example .env    # only if .env does not exist yet
# Replace each CHANGE_ME with a generated secret (never paste secrets into chats or logs):
$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) -replace '[+/=]','x'
```

> **`aircraft_app` is protected.** The API, `dotnet ef`, the database scripts, the seed SQL and the ML package all
> refuse it. See [docs/DATABASE.md — Isolation safeguards](docs/DATABASE.md#isolation-safeguards-aircraft_app-is-never-touched).

In Development, password-reset emails are written to `%LOCALAPPDATA%\AircraftFailurePrediction\mail\*.txt`;
set `Email__Mode=Smtp` and the SMTP settings for real delivery (see [Backend/.env.example](Backend/.env.example)).

## 2. Create the database, apply migrations and seed reference data

Run from the repository root. Only step 1 needs the `postgres` superuser password, which is prompted for and never stored.

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass   # allow the local scripts in this session only
.\tools\db\provision-database.ps1    # roles + aircraft_predict_db (needs DB_AUTO_CREATE=true; stops if the DB already exists)
.\tools\db\apply-migrations.ps1      # lists migrations, asks to confirm, applies them as DB_USER, then least-privilege grants
.\tools\db\apply-seed.ps1            # status + risk_status reference data (checks seed.sql is current first)
```

After the first successful run set `DB_AUTO_CREATE=false`. Re-run `apply-migrations.ps1` after every new migration
and `apply-seed.ps1` after editing a reference CSV. Details: [docs/DATABASE.md](docs/DATABASE.md).

## 3. Reference data (CSV → SQL)

Reference lists are edited as CSV in `tools/db/seed/csv/` (one file per table, described by `datasets.json`), compiled
into `tools/db/seed/seed.sql`, and applied only by `apply-seed.ps1`:

```powershell
dotnet run --project Backend/tools/AFP.SeedSql -- validate   # file:line [column] errors
dotnet run --project Backend/tools/AFP.SeedSql -- generate   # deterministic SQL; commit it with the CSV change
dotnet run --project Backend/tools/AFP.SeedSql -- check      # stale-artifact check (also a unit test)
```

## 4. Create the first Flight Dispatcher (administrator)

Two roles exist (seeded in the `role` table, granted through `user_role`):

* **Flight Dispatcher** — administrator with all screens: fleet health and predictions, inspection flags, aircraft
  status, creating and assigning work orders to technicians, data and models, reports, audit, user approval.
* **Maintenance Technician** — sees only the work orders assigned to them (with the aircraft and AI diagnosis),
  records inspection findings, resolves with a report and logs maintenance.

```powershell
cd Backend
$env:AFP_BOOTSTRAP_ADMIN_PASSWORD = Read-Host "Password (12+ chars, upper, lower, digit, symbol)"
dotnet run --project src/AFP.Api -- bootstrap-admin --username dispatcher.admin --email admin@yourcompany.example --name "Platform Admin"
Remove-Item Env:AFP_BOOTSTRAP_ADMIN_PASSWORD
```

Sign in with that **username** and password. The command refuses to run once an active Flight Dispatcher exists.
Everyone else uses **Sign up** (username, email, password); a dispatcher approves them under *User management* and
assigns one or both roles. Sessions use a 60-minute access token that the app renews automatically with a rotating
7-day refresh token.

## 4b. Raw JSON telemetry ETL (MinIO + PySpark in Docker)

```powershell
cd C:\project\AirCraft\AircraftFailurePrediction\aircraft-fde-auto-etl-postgres
docker compose up -d --build        # MinIO :9000/:9001 + Spark; writes to the LOCAL aircraft_predict_db
docker compose logs -f spark
```

Data Engineers (and dispatchers) upload raw JSON in the app under *Imports → Raw JSON telemetry*; status moves
Queued → Processing → Completed/Failed as the ETL reports. See `aircraft-fde-auto-etl-postgres/README.md`.

Accounts (development, created on request — change the passwords):

| Role | Sign in with | Password |
|---|---|---|
| Flight Dispatcher | `dispatcher` or `Dispatcher@aircraft.com` | `Password@123` |
| Maintenance Technician | `technician` or `Technician@aircraft.com` | `Password@123` |
| Data Engineer | `data` or `data@aircraft.com` | `Password@123` |

## 5. Run the backend

```powershell
cd Backend
dotnet run --project src/AFP.Api
```

* API: http://localhost:5080 · Swagger: http://localhost:5080/swagger (Development only)
* Health: http://localhost:5080/health (includes the database) · http://localhost:5080/health/live

Optional development-only demo fleet (fictional `DEMO-A01` … `DEMO-A12`, matching the synthetic telemetry):

```powershell
dotnet run --project src/AFP.Api -- seed-demo-fleet
```

## 6. Run the frontend

```powershell
cd Frontend
Copy-Item .env.example .env          # VITE_API_BASE_URL=http://localhost:5080, VITE_DEMO_MODE=false
npm install
npm run dev                          # http://localhost:5173
```

`VITE_DEMO_MODE=true` serves clearly bannered demo fixtures from `src/demo` with no backend. The default is
the real API.

## 7. Python 3.13 ML environment

```powershell
cd ml
py -3.13 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -r requirements-dev.txt
python -m pip install -e .
```

The ML package reads the root `.env` (`DB_*`, `ML_READER_*`, `ETL_WRITER_*`); `ml/.env` is only needed for ML-specific
overrides (see `ml/.env.example`).

Model artifacts are stored outside source control in `%LOCALAPPDATA%\AircraftFailurePrediction\models`
(override with `AFP_MODEL_DIR`). Each artifact's SHA-256 is recorded and checked before it is loaded.

## 8. Import historical telemetry

Real data: upload a CSV under *Telemetry → Imports*, signed in as a Data Engineer, or call
`POST /api/v1/telemetry/imports`. The required columns, units and validation rules are in
[docs/API.md](docs/API.md#csv-format). Register the aircraft first; rows for unknown registrations are
rejected and listed in the import summary.

Development only: generate about 2 months of **synthetic** readings for the demo fleet:

```powershell
cd C:\project\AirCraft\AircraftFailurePrediction
ml\.venv\Scripts\python tools\data\generate_synthetic_telemetry.py --out tools\data\synthetic_telemetry.csv --days 60
```

The output includes a few deliberately invalid rows to exercise validation. Its values are synthetic and are
**not** real sensor ranges or thresholds.

## 9. ML commands

Run from `ml\` with the virtual environment active:

```powershell
python -m afp_ml audit            # dataset + label audit; failure-prediction readiness (Blocked/Ready)
python -m afp_ml train            # train Isolation Forest (status: Trained)
python -m afp_ml train --target failure   # refuses (records Blocked) unless the audit found enough verified labels
python -m afp_ml register --latest --model-type AnomalyDetection   # Trained -> Registered
#   then in the UI: Leadership approves, Data Engineer activates
python -m afp_ml score            # score new telemetry with the ACTIVE model; writes anomalies + descriptive risk
python -m afp_ml status           # models, recent runs, queue, readiness
python -m afp_ml worker           # executes jobs queued from the UI/API (Ctrl+C to stop); --once for a single job
```

Each command records a `pipeline_run`. The full lifecycle, readiness criteria and limitations are in
[docs/ML_PIPELINE.md](docs/ML_PIPELINE.md).

## 10. Tests

```powershell
# Backend (xUnit): unit + API integration tests; no PostgreSQL needed (EF in-memory)
cd Backend; dotnet test

# Frontend (Vitest + jsdom + React Testing Library)
cd Frontend; npm test

# ML (pytest)
cd ml; .\.venv\Scripts\python -m pytest
```

The PostgreSQL tests in `AFP.IntegrationTests/PostgresRepositoryTests.cs` (migrations, seeds, status FKs, indexes)
run against a **disposable** database `aircraft_predict_db_test_<random>` that they create and drop. Without Docker,
point them at the local server with a role that may create databases:

```powershell
$env:AFP_TEST_PG_ADMIN = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<prompted, not stored>"
cd Backend; dotnet test --filter PostgresRepositoryTests
Remove-Item Env:AFP_TEST_PG_ADMIN
```

Otherwise they use Testcontainers (Docker); with neither available they print `SKIPPED (requires PostgreSQL)` and
return — treat that as **not run**. `AFP_SKIP_DOCKER_TESTS=1` skips the Docker attempt.

## Security improvements over a basic login design

* Registration never grants roles or reveals whether an email exists; accounts stay pending until approved.
* JWTs are revoked server-side: logout, account disable, role change and password reset all invalidate
  existing tokens immediately.
* Reset tokens are stored only as SHA-256 hashes, expire in 30 minutes, are single-use, and requesting a new
  one invalidates older ones.
* Account lockout (5 attempts, 15 minutes), per-IP rate limiting on authentication endpoints, and timing
  equalisation for unknown emails.
* Least-privilege database roles, plus a database trigger that prevents the ML role from approving or
  activating models.
* CSV exports neutralise spreadsheet-formula injection. Uploaded files are stored read-only outside the
  web root.

## Troubleshooting (Windows / PowerShell)

| Symptom | Fix |
|---|---|
| `...ps1 cannot be loaded because running scripts is disabled` | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` (current window only). |
| `python` is not recognised | Use `py -3.13`, or activate the venv (`.\.venv\Scripts\Activate.ps1`). |
| `Activate.ps1` blocked | Same execution-policy fix as above, or run `.\.venv\Scripts\python.exe` directly. |
| `psql` is not recognised | Use `& "C:\Program Files\PostgreSQL\17\bin\psql.exe"`, or add that folder to PATH. |
| `fe_sendauth: no password supplied` / `password authentication failed` | Check the role password, and the `pg_hba.conf` entry for host `127.0.0.1/32` and `::1/128` (`scram-sha-256`). |
| API exits: `Jwt:SigningKey must be configured…` | Set the signing key (step 3). It must be at least 32 bytes. |
| API exits: `Database connection is not configured` | Fill `DB_*`/`DB_APP_USER`/`API_USER_PASSWORD` in the root `.env`. |
| API exits: `database 'aircraft_app' is protected` | `DB_NAME` or a connection string points at the protected database. Use `aircraft_predict_db`. |
| `/health` returns 503 | Database unreachable, wrong credentials, roles not provisioned yet, or connected database ≠ `DB_NAME`. Check `Get-Service postgresql-x64-17` and run `tools\db\provision-database.ps1`. |
| `seed.sql is stale` | Run `dotnet run --project Backend/tools/AFP.SeedSql -- generate`, review the diff, retry. |
| `Failed to bind to address http://127.0.0.1:5080: address already in use` | Find the owner: `Get-NetTCPConnection -LocalPort 5080 -State Listen \| % { Get-Process -Id $_.OwningProcess }`. Stop that process, or run on another port with `dotnet run --project src/AFP.Api --urls http://localhost:5081` and set `VITE_API_BASE_URL` to match. |
| `permission denied for table …` (ML) | Re-run `tools\db\apply-migrations.ps1` (it re-applies `grant-privileges.sql`), and check that you are using the writer DSN for writes. |
| `npm` fails with `EACCES` / proxy errors | Run `npm config get proxy`; behind a corporate proxy set `npm config set proxy …`. |
| CORS error in the browser | The frontend must run on http://localhost:5173, or add the origin to `Cors:AllowedOrigins`. |
| `dotnet ef` not found | Run `dotnet tool restore` in `Backend/` (the tool is pinned in `dotnet-tools.json`). |
