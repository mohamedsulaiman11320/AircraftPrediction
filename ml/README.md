# afp_ml - ML subsystem of the Aircraft Failure Prediction Platform

Python 3.13 package that audits the telemetry/maintenance dataset, trains an Isolation Forest
anomaly detector, trains a supervised failure-prediction model **only when the readiness gate
allows it**, scores telemetry, writes per-aircraft risk indicators, and runs the worker that
executes `pipeline_run` jobs queued by the ASP.NET Core API.

The database schema belongs to the backend (EF Core migrations). The contract is
[`docs/DATABASE.md`](../docs/DATABASE.md); the design is described in
[`docs/ML_PIPELINE.md`](../docs/ML_PIPELINE.md).

> The ML subsystem never touches users, tokens, audits, work orders or inspection flags, never
> changes aircraft operational status, and moves models only from `Trained` to `Registered`.
> Leadership approves and a Data Engineer activates models through the API/UI.

## Setup (Windows 11 / PowerShell)

```powershell
cd C:\project\AirCraft\AircraftFailurePrediction\ml
py -3.13 -m venv .venv
.\.venv\Scripts\python -m pip install --upgrade pip
.\.venv\Scripts\python -m pip install -r requirements-dev.txt   # runtime pins + pytest
.\.venv\Scripts\python -m pip install -e .
.\.venv\Scripts\python -m pytest                                 # no database needed
```

`requirements.txt` holds the pinned runtime dependencies (numpy, pandas, scikit-learn, scipy,
joblib, psycopg[binary]); `requirements-dev.txt` adds pytest.

## Configuration

Settings come from environment variables. `.env` files are also read (by a small built-in
parser, so python-dotenv is not needed). Precedence: real environment variables, then the ML file
(`--env-file`, or `AFP_ML_ENV_FILE`, or `ml\.env`, or `.\.env`), then the repository-root `.env`
(the parent of `ml\`, shared with the backend). A file never overrides a value that is already set.
Copy `.env.example` to `.env` (git-ignored) if you need ML-specific overrides. **Never commit
credentials.**

**Database target.** The ML subsystem uses `DB_NAME` (default `aircraft_predict_db`). The
database `aircraft_app` is **protected** and always refused, as is any name in
`DB_FORBIDDEN_NAMES`. The database in every DSN must equal `DB_NAME`, otherwise the command stops
with a configuration error (exit code 2) before connecting. After connecting, `SELECT
current_database()` is checked again and the connection is refused on a mismatch. Only
`user@host:port/db` is logged; DSNs and passwords are never printed or stored.

| Variable | Default | Meaning |
|---|---|---|
| `AFP_ML_READER_DSN` | - | Explicit reader DSN (highest precedence), e.g. `postgresql://<reader>:<password>@localhost:5432/aircraft_predict_db`. If no reader DSN is available, the writer DSN is used for reads. |
| `AFP_ML_WRITER_DSN` | - | Explicit writer DSN (highest precedence). Needed by every command that records a `pipeline_run`. |
| `DB_HOST`, `DB_PORT`, `DB_NAME` | localhost, 5432, `aircraft_predict_db` | Used to compose `postgresql://` DSNs when the explicit DSNs are unset. |
| `ML_READER_USER`, `ML_READER_PASSWORD` | - | Reader credentials for the composed reader DSN (URL-encoded). |
| `ETL_WRITER_USER`, `ETL_WRITER_PASSWORD` | - | Writer credentials for the composed writer DSN (URL-encoded). |
| `DB_FORBIDDEN_NAMES` | - | Extra database names that must never be used (comma list, case-insensitive). `aircraft_app` is always forbidden. |
| `ML_LABEL_DEFINITION_CONFIRMED` | false | Must be set to `true` after the domain owner signs off the failure-label definition; until then failure prediction is `Blocked`. |
| `ML_FAILURE_CODES` | - | Comma list of failure codes in scope. Recorded in the audit and model metadata only; it does not filter labels yet (`maintenance_log` has no failure-code column). |
| `AFP_MODEL_DIR` | `%LOCALAPPDATA%\AircraftFailurePrediction\models` | Artifact store, kept **outside source control**. |
| `AFP_PREDICTION_HORIZON_DAYS` | 30 | Failure-label horizon and the `horizon_days` of risk indicators. |
| `AFP_READINESS_MIN_POSITIVE_LABELS` | 30 | Minimum usable verified unscheduled events. |
| `AFP_READINESS_MIN_AIRCRAFT_WITH_POSITIVES` | 5 | Minimum distinct aircraft with usable labels. |
| `AFP_READINESS_MIN_LABEL_MONTHS` | 6 | Minimum distinct calendar months containing usable labels. |
| `AFP_READINESS_MIN_NEGATIVE_EXAMPLES` | 200 | Minimum negative aircraft-day examples. |
| `AFP_READINESS_MIN_SPLIT_POSITIVES` | 5 | Minimum positives in each of train / validation / test. |
| `AFP_LABEL_REQUIRE_CONFIRMED_FAILURE` | false | Count only events with `actual_failure_confirmed = true` as labels. |
| `AFP_CALIBRATION_MAX_ECE`, `AFP_CALIBRATION_MIN_TEST_POSITIVES` | 0.10, 10 | Criteria for `calibration_validated`. |
| `AFP_IF_CONTAMINATION` | 0.01 | Isolation Forest contamination. |
| `AFP_IF_N_ESTIMATORS` | 200 | Number of Isolation Forest trees. |
| `AFP_FEATURE_WINDOW` | 6 | Number of previous readings used for rolling features. |
| `AFP_SCORING_CONTEXT_HOURS` | 24 | Hours of earlier telemetry loaded as feature context during incremental scoring. |
| `AFP_RISK_WINDOW_DAYS`, `AFP_RISK_RECENCY_HOURS` | 7, 48 | Descriptive risk window, and the telemetry age after which the indicator is `Undetermined`. |
| `AFP_RISK_ELEVATED_RATE_MULTIPLIER`, `AFP_RISK_HIGH_RATE_MULTIPLIER` | 2, 5 | Anomaly rate compared with the training baseline. |
| `AFP_RISK_ELEVATED_ANOMALY_COUNT`, `AFP_RISK_HIGH_ANOMALY_COUNT`, `AFP_RISK_MIN_READINGS` | 3, 10, 36 | Minimum counts for each risk level. |
| `AFP_AUDIT_GAP_MINUTES` | 60 | The audit reports a gap when consecutive readings are further apart than this. |
| `AFP_WORKER_POLL_SECONDS` | 15 | How often the worker polls the queue. |
| `AFP_DB_CONNECT_TIMEOUT`, `AFP_RANDOM_SEED`, `AFP_LOG_LEVEL` | 10, 42, INFO | |

## Commands

Run every command as `.\.venv\Scripts\python -m afp_ml <command>` (or use the `afp-ml` entry
point). Each one runs independently. Every job command records a `pipeline_run` row with these
fields: `status`, `started_at_utc`, `completed_at_utc`, `result_summary`, `error_message`,
`records_processed` and `worker_host`. A command run from the CLI uses trigger `Cli`. A job run by
the worker updates the row that the API queued (trigger `Api`).

| Command | Job type | What it does |
|---|---|---|
| `audit [--json] [--no-record]` | `DatasetAudit` | Audits telemetry: counts, coverage per aircraft, missing and Suspect rates, duplicates, sensors, cadence and gaps. Audits labels from `maintenance_log`. Computes the `failure_prediction_readiness` object. Data is read with the reader DSN. The run is recorded with the writer DSN; `--no-record` skips recording. |
| `train [--target anomaly] [--include-suspect]` | `TrainAnomaly` | Trains an Isolation Forest on `Valid` telemetry, plus `Suspect` with `--include-suspect`. Saves the artifact and inserts `model_version` with status `Trained`. |
| `train --target failure` | `TrainFailure` | Runs the readiness gate first (it is `Blocked` until `ML_LABEL_DEFINITION_CONFIRMED=true`, in addition to the data criteria). If the gate is **Blocked**, the run is recorded as `Blocked`, no model is created, and the exit code is 0. Otherwise it trains the candidates, evaluates them on a chronological split against a prior-probability baseline, and inserts a `Trained` FailurePrediction model. |
| `register --model-version-id <uuid>` / `register --latest [--model-type ...]` | `RegisterModel` | Changes `Trained` to `Registered`. Before that it checks the artifact path, the SHA-256 and the metrics. |
| `score [--full]` | `Score` | Uses the **Active** AnomalyDetection model and scores telemetry above the watermark. Stores only anomalous readings, then writes `DescriptiveIndicator` risk rows. If an Active FailurePrediction model exists, it also writes `Model` risk rows. Every risk row carries `prediction_state` (`Scored`, `InsufficientTelemetry`, `MissingFeatures`) and `feature_cutoff_utc`; a row whose (aircraft, basis, model, cutoff, horizon) already exists is skipped, so re-running is idempotent. |
| `status [--json]` | - | Read-only. Shows active, registered and trained models, the last run of each job type, queued or running jobs, and readiness. |
| `worker [--once]` | executes queued jobs | Polls `pipeline_run` for `Queued` rows with `FOR UPDATE SKIP LOCKED`, oldest first. Ctrl+C stops it after the current job. |

Global options are `--log-level`, `--env-file` and `--version`. Logs go to stderr; results and
JSON go to stdout.

Exit codes:

* `0`: the job succeeded, or was `Blocked` by the readiness gate.
* `1`: the job failed or was refused, for example because there is no Active model or the artifact is tampered.
* `2`: a configuration error, for example a missing DSN. The message names the missing variable.
* `3`: the database is unreachable.

### Typical development flow

```powershell
# 1. Generate synthetic dev-only telemetry and import it via the API (Data Engineer → Telemetry import)
py -3.13 ..\tools\data\generate_synthetic_telemetry.py --out $env:TEMP\synthetic_telemetry.csv
# 2. Audit (failure prediction will be Blocked: the generator creates no maintenance labels)
.\.venv\Scripts\python -m afp_ml audit
# 3. Train + register the anomaly model
.\.venv\Scripts\python -m afp_ml train --target anomaly
.\.venv\Scripts\python -m afp_ml register --latest --model-type AnomalyDetection
# 4. Leadership approves, Data Engineer activates (API/UI)
# 5. Score
.\.venv\Scripts\python -m afp_ml score
# Or let the API queue jobs and run the worker
.\.venv\Scripts\python -m afp_ml worker
```

## Model lifecycle

```
Trained ──(afp_ml register)──▶ Registered ──(Leadership, API)──▶ Approved ──(Data Engineer, API)──▶ Active ──▶ Retired
                                     └──────────────(Leadership)──────────▶ Rejected
```

Artifacts are written to `AFP_MODEL_DIR/<model_name>/<version>/model.joblib` and
`metadata.json`. The version string is `yyyyMMddHHmmss` in UTC. Both files are made read-only,
and the artifact's SHA-256 is stored in `model_version.artifact_sha256`. `artifact_path` is
stored relative to `AFP_MODEL_DIR`. Before a joblib/pickle file is ever deserialised, two
checks run. First, the path must resolve inside `AFP_MODEL_DIR`. Second, the SHA-256 of the
bytes read must match the database. The same in-memory bytes are then loaded, so the file
cannot be swapped between the check and the load.

`metadata.json` also records `dependency_versions` (Python, numpy, pandas, scikit-learn, scipy,
joblib, psycopg), `feature_schema_version`, `training_window` (from/to) and, for failure models,
`label_definition` (event type `Unscheduled`, verified, the require-confirmed flag, the sign-off
flag, failure codes and horizon). The metadata is not part of the artifact hash.

## Tests

`.\.venv\Scripts\python -m pytest` runs 92 tests, with no database (the repository-root `.env`
and all `DB_*` variables are ignored during tests). The tests cover:

* data quality and audit;
* feature engineering, with past-only rolling features and missing-value handling;
* leakage: cutoff features, the label window, and the chronological split with a purge gap;
* the readiness gate;
* artifact save, hash and load, including a tampered artifact and a path outside the model directory;
* scoring: outliers, severity, persisting only anomalies, the watermark, and idempotency;
* the descriptive risk rules;
* worker dispatch against a fake repository;
* the CLI messages for missing DSNs, DSN composition and precedence, env-file precedence, and the
  database guard (`aircraft_app`, a mismatched name, `DB_FORBIDDEN_NAMES`);
* the label-definition sign-off gate, risk-row `prediction_state` / `feature_cutoff_utc`, and
  idempotent risk inserts;
* baseline, `beats_baseline` and false-alerts-per-aircraft metrics, and artifact metadata;
* the synthetic generator;
* failure-model training on a small synthetic labelled dataset. This dataset exists only inside the tests.

## Limitations

* The `PostgresRepository` SQL is written against `docs/DATABASE.md` but is **not exercised by
  the automated tests** (no database in CI). Run a smoke test against a real database after the
  backend migrations and `grant-privileges.sql` are applied.
* Audits and training load the needed telemetry columns into memory (pandas). This is fine for
  the expected development volumes (hundreds of thousands to a few million rows). Larger fleets
  need SQL-side aggregation or chunking.
* If a worker process is killed, its claimed run stays `Running`. There is no lease or timeout
  reaper yet, so such a run must be cancelled or failed manually.
* The anomaly model is unsupervised. Its scores are relative and are **not** failure
  probabilities. No approved engineering thresholds are used anywhere.
* Failure prediction stays `Blocked` until enough verified maintenance labels exist and the label
  definition is signed off (`ML_LABEL_DEFINITION_CONFIRMED=true`). The synthetic generator
  deliberately produces no labels.
