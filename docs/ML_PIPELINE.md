# ML Pipeline Design

This document describes the Python ML subsystem (`ml/afp_ml`). Its database contract is
[`DATABASE.md`](DATABASE.md), and its job interface is the `/api/v1/pipelines` and
`/api/v1/models` sections of [`API.md`](API.md). Setup and commands are covered in
[`ml/README.md`](../ml/README.md).

## 1. Overview

| Stage | Command / job type | Output |
|---|---|---|
| Dataset audit + readiness gate | `audit` / `DatasetAudit` | `pipeline_run.result_summary` |
| Anomaly model training | `train --target anomaly` / `TrainAnomaly` | artifact + `model_version` (`Trained`) |
| Failure model training (gated) | `train --target failure` / `TrainFailure` | `Blocked` run, **or** artifact + `model_version` (`Trained`) |
| Registration | `register` / `RegisterModel` | `model_version` `Trained → Registered` |
| Scoring + risk indicators | `score` / `Score` | `aircraft_anomaly_result`, `aircraft_risk_prediction` |
| Queue execution | `worker` | executes API-queued `pipeline_run` rows |

Code layout:

* `repositories.py` is the only module that issues SQL. Every statement is parameterised.
* The target database is `DB_NAME` (default `aircraft_predict_db`). `aircraft_app` is protected
  and always refused, together with any name in `DB_FORBIDDEN_NAMES`. DSNs come from
  `AFP_ML_READER_DSN` / `AFP_ML_WRITER_DSN` or are composed from `DB_HOST` / `DB_PORT` / `DB_NAME`
  with `ML_READER_USER` / `ML_READER_PASSWORD` and `ETL_WRITER_USER` / `ETL_WRITER_PASSWORD`
  (read from the environment, `ml/.env`, then the repository-root `.env`). The dbname in every
  DSN must equal `DB_NAME` (checked at configuration time), and `current_database()` is checked
  again right after connecting. Only `user@host:port/db` is logged.
* Business logic lives in `audit.py`, `features.py`, `anomaly.py`, `failure.py`, `scoring.py`,
  `risk.py`, `registry.py`, `training.py` and `jobs.py`. It operates on pandas DataFrames
  through a `Repository` protocol, so it is unit-tested with an in-memory fake.

## 2. Dataset audit

The audit reads `telemetry_record` (with columns `temperature_c`, `pressure_kpa`, `quality_status`, `quality_flags` and
`data_source`), `maintenance_log` and `aircraft`. `result_summary` holds the following:

* **`telemetry`**
  * total rows;
  * counts and rate by `quality_status` (`Suspect` rate);
  * overall first and last record;
  * missing-value rates for `temperature_c` and `pressure_kpa`;
  * quality-flag counts;
  * duplicate keys on (aircraft, sensor, timestamp) and duplicate values;
  * sensor coverage (number of series, rows without `sensor_id`, sensors per aircraft);
  * cadence (median interval, gaps longer than `AFP_AUDIT_GAP_MINUTES`, the largest gap, a list per series);
  * coverage per aircraft (rows, sensors, first and last record, days with data, Suspect and missing rates);
  * aircraft without telemetry;
  * data-source counts, plus a `synthetic_data_present` flag.
* **`labels`**
  * `maintenance_log` counts by `event_type`;
  * unscheduled events, verified and unverified;
  * the breakdown of `actual_failure_confirmed` (true, false, null);
  * the number of label events, the aircraft and components they cover, and their monthly distribution.
* **`label_definition`** `{event_type: Unscheduled, is_verified: true, require_confirmed_failure,
  definition_confirmed, failure_codes[], failure_codes_applied: false, failure_codes_note,
  horizon_days}`. `failure_codes` (`ML_FAILURE_CODES`) is recorded for traceability only;
  `maintenance_log` has no failure-code column, so it does not filter labels.
* **`failure_prediction_readiness`** (see §6). `{status, reasons[], horizon_days, positive_examples,
  negative_examples, label_events, usable_label_events, aircraft_with_positives,
  distinct_label_months, split, thresholds, label_definition}`.

## 3. Anomaly features (reading level)

Each series is one (`aircraft_id`, `sensor_id`) pair ordered by `recorded_at_utc` (ties are broken
by `id`). A missing `sensor_id` forms its own series. With `N` = `AFP_FEATURE_WINDOW` (default 6), the
features for each measurement `m` in {`temperature_c`, `pressure_kpa`} are:

| Feature | Definition |
|---|---|
| `m` | raw normalised value |
| `m_diff1` | `m - previous m` |
| `m_roll_mean_N`, `m_roll_std_N` | mean and std of the **previous** N readings (`shift(1).rolling(N, min_periods=max(2, N//2))`) |
| `m_dev_roll_mean_N` | `m - m_roll_mean_N` |
| `minutes_since_prev` | minutes since the previous reading in the series |

**Past only.** No feature can be influenced by a later reading, and the current reading is never
part of its own baseline. A test checks that features are identical with and without the future
rows.

**Warm-up rows.** Rows with fewer than `min_periods` earlier readings in their series have no
defined baseline. They are excluded from training and are not scored; they still advance the
scoring watermark. Incremental scoring loads `AFP_SCORING_CONTEXT_HOURS` (24 h) of earlier
telemetry as history, so warm-up only happens at the very start of a series or after a gap
longer than the context window.

### Missing-data policy

* Rows with `quality_status = Valid` are used for training by default; `--include-suspect` also
  uses `Suspect` rows (a Suspect row has one measurement missing). Scoring uses the same statuses
  the model was trained on (stored in the artifact).
* NaNs are kept in the feature frame. Imputation happens inside the persisted sklearn
  `Pipeline` as `SimpleImputer(strategy="median", add_indicator=True, keep_empty_features=True)`.
  The medians and the indicator columns are learned **on the training data only**, stored in the
  artifact, and reused unchanged at scoring time. The medians are also recorded in
  `metrics.imputer_medians`.

## 4. Isolation Forest: score semantics and severity

> Since 2026-10-28 (user decision: anomalies from the 30-day forecast only) `score` still scores every reading and writes
> the descriptive risk indicators, but does **not** persist the flagged rows to `aircraft_anomaly_result` unless
> `AFP_PERSIST_TELEMETRY_ANOMALIES=true`. The descriptive indicator counts persisted flags, so with persistence off it
> reflects no new anomaly activity.
>
> Forecast override (`descriptive_forecast_condition_v1`, 2026-10-28): an aircraft with recent telemetry and an OPEN
> forecast incident gets `HighCritical` (engine-temperature and oil-pressure limits crossed) or `Critical` (one limit);
> the rate rule applies otherwise. Both categories are seeded in `risk_status`. Rows are idempotent per data cutoff, so a
> changed forecast condition shows from the next cutoff (the next simulated day).

* `IsolationForest(n_estimators=200, contamination=0.01, max_samples="auto", random_state=42)`.
* **`anomaly_score = -score_samples(x)`.** A higher score means the reading is more anomalous
  relative to the training data. **It is not a probability**; it has no calibrated meaning and
  does not by itself indicate a fault.
* `is_anomaly` is the Isolation Forest decision: `anomaly_score > -offset_`. The cut is set by
  `contamination`, so about 1 % of normal training readings are flagged by construction.
* The severity thresholds come from the training score distribution and are stored in the
  artifact and in `metrics.severity_thresholds`:
  * `low` is the decision cut;
  * `medium` is the training quantile `1 - contamination/2` (99.5 %);
  * `high` is the training quantile `1 - contamination/10` (99.9 %);
  * each level is forced to be ≥ the previous one.
  * A flagged reading is `High` if its score ≥ `high`, `Medium` if ≥ `medium`, otherwise `Low`.
* `model_version.metrics` holds training score percentiles, mean/std/min/max, the flagged
  fraction, `baseline_rates` (the training anomaly rate and High rate), row counts, the
  missing-value rate per feature and the imputer medians. `metric_type` is
  `unsupervised_training_distribution`: these values describe the score distribution, **not accuracy**.

## 5. Scoring

1. Load the **Active** `AnomalyDetection` model. If none exists, the run fails with instructions:
   register the model, then Leadership approves it and a Data Engineer activates it through the API/UI.
2. Verify the artifact (§9) **before** deserialising it.
3. Find the watermark: the largest `result_summary.max_telemetry_record_id` among `Succeeded`
   `Score` runs of that model. `--full` (job parameter `full: true`) starts from 0.
4. Fetch telemetry with `id >` the watermark, add the context rows, build the features, and score
   the eligible non-warm-up rows. The new watermark is the largest id fetched, including rows
   skipped because of their quality status.
5. Insert **only** readings flagged anomalous into `aircraft_anomaly_result` with:
   * `severity`;
   * `feature_values` (the raw features as jsonb; null where missing);
   * `investigation_status = New`;
   * `scored_at_utc` and `pipeline_run_id`.

   The insert uses `ON CONFLICT (telemetry_record_id, model_version_id) DO NOTHING`, which makes scoring idempotent.
6. Write the risk indicators (§7).

## 6. Failure-prediction readiness gate and BLOCKED behaviour

**Labels.** A label is a verified `Unscheduled` `maintenance_log` event (`is_verified = true`) whose
`actual_failure_confirmed` is not `false`. With `AFP_LABEL_REQUIRE_CONFIRMED_FAILURE=true`, it must be `true`.

**Examples.** One aircraft-day row per cutoff `c` (00:00 UTC). Features (schema version 2) use
telemetry with `recorded_at_utc < c` only, for temperature, pressure **and vibration**:

* mean over 1, 7 and 30 days; min and max over 1 day (the 24 h window) and 7 days;
* std over 7 and 30 days; the 1-day mean minus the 30-day mean;
* reading counts; the Suspect fraction and the temperature/pressure/vibration missing fractions;
* days since the last telemetry;
* BR-01 breach counts over 1 and 7 days (`pressure_low_breaches_*`, `temperature_high_breaches_*`).
  The limits are read from the seeded `health_threshold` rows `OilPressureLowPsi` and
  `EngineTemperatureHighC`, never from code. Pressure is compared in PSI after rounding to 4 dp, so an
  exact 35 PSI reading is not a breach. If a limit is not configured, its counts are NaN (unknown), not 0.
  No vibration threshold exists or is invented;
* `days_since_last_maintenance`: days from the latest **verified** `maintenance_log` event strictly
  before `c`; NaN when there is none.

Missing measurements stay NaN; the model's median imputer fills them. They are never replaced with 0.

An aircraft's first 7 days and any row without telemetry in the previous 30 days are excluded.

**Label.** `label = 1` when a label event falls in the window `(c, c + horizon]`. Examples whose
window ends after the **observation end** are **censored** and removed, never kept as negatives. The
observation end is the aircraft's last telemetry reading (and never later than the run time). It was the
run time alone until 2026-10-27: rerunning training days later, with no new data, turned unobserved
days into "no failure" negatives (the operational test split drifted 103/5 → 105/9). A regression test
now checks that labels are identical when only the clock advances.

**Readiness = `Ready` only if ALL of the following hold** (defaults; set them with `AFP_READINESS_*`):

| Criterion | Default |
|---|---|
| the label definition is signed off by the domain owner (`ML_LABEL_DEFINITION_CONFIRMED=true`); otherwise the reason is "Label definition not confirmed by the domain owner (set ML_LABEL_DEFINITION_CONFIRMED=true after sign-off)." | false |
| usable label events (covered by an observable example window) | ≥ 30 |
| distinct aircraft with usable labels | ≥ 5 |
| distinct calendar months with usable labels | ≥ 6 |
| negative aircraft-day examples | ≥ 200 |
| a chronological split with purge gaps of `horizon` days is possible, and each of train / validation / test has positives | ≥ 5 each |

Otherwise the status is `Blocked`, and `reasons` lists every failed criterion in plain language.
`train --target failure`, and a `TrainFailure` job, then records `pipeline_run.status = Blocked`
with the readiness object in `result_summary` and the reasons in `error_message`. It creates **no**
model and **no** artifact, and the CLI exits with code 0 and a clear message. The synthetic
generator creates no maintenance labels, so on synthetic data failure prediction correctly stays
Blocked.

## 7. Risk indicators

Every successful `score` run writes one row per aircraft to `aircraft_risk_prediction`, with these fixed values:

* `approval_status = PendingReview` and `actual_outcome = Unknown`;
* `horizon_days = AFP_PREDICTION_HORIZON_DAYS`;
* `component_id = NULL`;
* `status_id` is omitted (database default 1 = Active).

Every row also sets `prediction_state` and `feature_cutoff_utc` (the values live in one place,
`risk.PredictionState`):

| Basis | Case | `prediction_state` | `feature_cutoff_utc` |
|---|---|---|---|
| `DescriptiveIndicator` | category determined | `Scored` | the aircraft's latest telemetry `recorded_at_utc` |
| `DescriptiveIndicator` | `Undetermined` (no recent / too little telemetry) | `InsufficientTelemetry` | latest telemetry time, or the scoring time truncated to the hour if the aircraft has none |
| `Model` | a feature row exists | `Scored` | the feature row's cutoff (00:00 UTC) |
| `Model` | no feature row | `MissingFeatures` (probability NULL, category `Undetermined`) | the scoring time truncated to the hour |

The insert uses `ON CONFLICT (aircraft_id, basis, model_version_id, feature_cutoff_utc,
horizon_days) DO NOTHING` (unique index, `NULLS NOT DISTINCT`), so re-scoring the same data does
not duplicate rows. `result_summary.predictions_inserted` is the number actually inserted and
`predictions_already_present` the number skipped.

### Descriptive indicator (`basis = DescriptiveIndicator`)

These rows always have `probability = NULL` and `probability_validated = false`, and
`model_version_id` is the anomaly model. The Isolation Forest flags about `contamination` of
normal readings, so raw counts grow with cadence. The rule therefore compares **rates** over the
last `AFP_RISK_WINDOW_DAYS` (7) with the model's training baseline. Anomalies marked `Dismissed`
are not counted.

```
rate       = anomalies_in_window / readings_in_window
ratio      = max(rate / baseline_anomaly_rate, high_severity_rate / baseline_high_rate)
Undetermined  if no telemetry within AFP_RISK_RECENCY_HOURS (48 h) or readings < AFP_RISK_MIN_READINGS (36)
High          if ratio >= 5 (AFP_RISK_HIGH_RATE_MULTIPLIER)     and anomalies >= 10 (AFP_RISK_HIGH_ANOMALY_COUNT)
Elevated      if ratio >= 2 (AFP_RISK_ELEVATED_RATE_MULTIPLIER) and anomalies >= 3  (AFP_RISK_ELEVATED_ANOMALY_COUNT)
Low           otherwise
```

`feature_snapshot` records every count, rate, ratio and threshold used, and the reason.

### Model-based indicator (`basis = Model`)

These rows are written only when an **Active** `FailurePrediction` model exists. Features are
built at today's 00:00 UTC cutoff. The category is:

* `High` if the score ≥ the decision threshold;
* `Elevated` if the score ≥ half the threshold;
* `Low` otherwise;
* `Undetermined` if there is no recent telemetry or no features.

`probability` is filled in, and `probability_validated = true`, **only if** the model's
`metrics.calibration_validated` is true. Otherwise `probability` is NULL. This also satisfies the
`CHECK (probability IS NULL OR probability_validated)` constraint.

## 8. Failure-model training, leakage prevention and evaluation

**Temporal leakage.** The split is chronological by cutoff: 60 % train, 20 % validation and 20 %
test of the usable span. A **purge gap of `horizon` days** separates train from validation and
validation from test, and rows inside the gaps are dropped. Every training label window
`(c, c + h]` therefore ends before the first validation cutoff, and the same holds between
validation and test. Tests check this.

**Target leakage.** Features come only from data before the cutoff. The single maintenance-derived
feature is `days_since_last_maintenance`, built from events strictly before the cutoff; no work-order or
outcome field is ever a feature. Tests check the feature names, that an event at or after the cutoff
(including the failure being predicted) does not move that feature, and that features at a cutoff do
not change when later data are changed.

**Model (approved specification).**
`Pipeline([SimpleImputer(strategy="median"), RandomForestClassifier(n_estimators=300, max_depth=10,
class_weight="balanced", random_state=AFP_RANDOM_SEED)])`, with `AFP_RANDOM_SEED` defaulting to 42.
Two comparison candidates are also fitted and reported in `metrics.candidates`, but are **never
selected**: `StandardScaler` + `LogisticRegression(class_weight="balanced")` and
`HistGradientBoostingClassifier(class_weight="balanced", learning_rate=0.05, max_iter=200)` (both with
median imputation plus missing indicators). `metrics.feature_importance` lists the forest's importances.

**Selection and calibration.**

1. All candidates are trained on the train split.
2. The specified RandomForest is always selected (`metrics.selection_metric = "approved_specification"`).
   Every candidate's validation metrics are recorded in `metrics.candidates`.
3. The selected model is wrapped in a sigmoid (Platt) calibrator fitted on the validation split
   (`CalibratedClassifierCV(FrozenEstimator(model))`).
4. The decision threshold is set to maximise F1 on the calibrated validation predictions.
5. The model is evaluated **once** on the untouched test split.

**Metrics.** These are computed on validation and test and are never assumed:

* precision, recall and F1 at the decision threshold;
* PR-AUC (average precision);
* ROC-AUC, **null** when the split does not contain both classes (never a meaningless value);
* **specificity** = TN / (TN + FP) and **false-alert rate** = FP / (FP + TN), both null without negatives;
* **Brier score**, the baseline Brier score at prevalence, and the Brier skill score;
* **reliability bins**: 10 equal-width bins, each with its count, mean prediction and observed frequency;
* expected calibration error (ECE);
* the confusion matrix and prevalence;
* `pr_auc_baseline` (= the prevalence of the evaluated split, the PR-AUC of a no-skill model) and
  `pr_auc_lift` = `pr_auc / prevalence` (null when not computable).

**Baseline.** A `DummyClassifier(strategy="prior")` is fitted on train and evaluated on validation
and test at the same decision threshold; its PR-AUC, precision and recall are stored in
`metrics.baseline`. `metrics.beats_baseline` is true when the selected model's test PR-AUC is
greater than the baseline's (null when either is undefined).

**Alert burden.** `metrics.false_alerts_per_aircraft_per_30_days` = test false positives /
(distinct test aircraft × test-period days / 30), where the period is the inclusive span of test
cutoffs. It is null when aircraft or cutoffs are unavailable. Values are never fabricated.

The top-level `metrics.precision`, `recall`, `pr_auc`, `false_alert_rate`, `brier`,
`pr_auc_baseline` and `pr_auc_lift` are the **test** values. `metrics.split` records the boundaries, the purge gap and the counts per split.

**`metrics.calibration_validated`** is true only if all of these hold on the test split:

* at least `AFP_CALIBRATION_MIN_TEST_POSITIVES` positives (10);
* ECE ≤ `AFP_CALIBRATION_MAX_ECE` (0.10);
* Brier skill score > 0, meaning the model beats the prevalence baseline.

Each check is recorded in `metrics.calibration_checks`.

The model is saved as `FailurePrediction` with status `Trained`. It carries
`prediction_horizon_days` and a `limitations` text, `metrics.readiness` holds the readiness
snapshot and `metrics.label_definition` the label definition in force.

**Artifact metadata.** `metadata.json` (next to every artifact) records `dependency_versions`
(Python, numpy, pandas, scikit-learn, scipy, joblib, psycopg), `feature_schema_version`,
`training_window` `{from_utc, to_utc}` and, for failure models, `label_definition`. It is not part
of the artifact hash; hashing and verification are unchanged (§9).

## 9. Artifact protection

* Path: `AFP_MODEL_DIR/<model_name>/<version>/model.joblib` + `metadata.json`.
  `model_name` and `version` must match `[A-Za-z0-9][A-Za-z0-9._-]*`, and an existing version
  directory is never overwritten. `AFP_MODEL_DIR` is outside source control
  (`%LOCALAPPDATA%\AircraftFailurePrediction\models` by default).
* The artifact's SHA-256 is computed when it is written, checked again on disk, and stored in
  `model_version.artifact_sha256`. Both files are made read-only.
* joblib files are pickles, so loading one runs code. Before any load:
  1. The stored path, relative or absolute, is resolved with symlinks followed and must lie
     **inside** `AFP_MODEL_DIR`.
  2. The file's bytes are read and their SHA-256 compared with the database value in
     constant time.
  3. Only those same in-memory bytes are deserialised, so the file cannot be swapped between
     the check and the load.

  A mismatch, or a path outside the directory, refuses the load and fails the run.
* `register` performs the same verification without deserialising the file.
* Protect `AFP_MODEL_DIR` with file-system ACLs: only the ML service account should be able to write to it.

## 10. Worker / job interface

* The API inserts `pipeline_run` rows with `status = Queued` and `trigger = Api`, setting
  `job_type` and `parameters` (jsonb).
* `python -m afp_ml worker` runs this transaction:

  ```sql
  SELECT ... FROM pipeline_run
   WHERE status = 'Queued' AND job_type = ANY('{DatasetAudit,TrainAnomaly,TrainFailure,Score,RegisterModel}')
   ORDER BY requested_at_utc, id LIMIT 1 FOR UPDATE SKIP LOCKED;
  UPDATE pipeline_run SET status='Running', started_at_utc=now, worker_host=<host> WHERE id=...;
  ```

  `TelemetryImport` rows are left for the API.
* The worker dispatches by job type:

  | `job_type` | Runs |
  |---|---|
  | `DatasetAudit` | audit |
  | `TrainAnomaly` | the anomaly target |
  | `TrainFailure` | the failure target |
  | `Score` | score |
  | `RegisterModel` | register |

  Parameters may use camelCase or snake_case:

  | Job | Parameters |
  |---|---|
  | `TrainAnomaly` | `includeSuspect` |
  | `Score` | `full` |
  | `RegisterModel` | `modelVersionId`, `latest`, `modelType` |

* On completion the worker sets `status` (`Succeeded` / `Blocked` / `Failed`) together with
  `completed_at_utc`, `result_summary`, `error_message`, `records_processed` and
  `model_version_id`.
* A failure records `error_message` (`Type: message`, truncated to 4000 characters) and keeps the worker alive.
* `--once` processes at most one job. Without it the worker polls every
  `AFP_WORKER_POLL_SECONDS`. The first Ctrl+C or Ctrl+Break finishes the current job and exits;
  a second one aborts.
* Runs started from the CLI insert their own row with `trigger = Cli`.

## 11. Database privileges used

The ML subsystem uses only these privileges:

| Role | Used for |
|---|---|
| `afp_ml_reader` (the session is set `READ ONLY`) | SELECT on `aircraft`, `telemetry_record`, `maintenance_log`, `model_version`, `pipeline_run` and `aircraft_anomaly_result` |
| `afp_ml_writer` | INSERT into `pipeline_run`, `model_version`, `aircraft_anomaly_result` and `aircraft_risk_prediction` |
| `afp_ml_writer` | UPDATE `pipeline_run` columns: `status`, `started_at_utc`, `completed_at_utc`, `result_summary`, `error_message`, `records_processed`, `worker_host`, `model_version_id` |
| `afp_ml_writer` | UPDATE `model_version` columns: `status` (`Trained → Registered` only, with `WHERE status='Trained'`) and `registered_at_utc` |
| `afp_ml_writer` | `SELECT ... FOR UPDATE` on `pipeline_run` |

The ML subsystem never accesses users, tokens, the auth or action audits, work orders or
inspection flags. It never updates `aircraft` (including operational status), and it never
approves, activates or retires models.

## 12. Limitations

* The anomaly detector is unsupervised. It finds statistically unusual readings relative to the
  training period, not faults, and its scores are not probabilities. A training period that
  contains abnormal behaviour shifts the baseline. Severity is relative, not an engineering limit;
  no approved `engineering_threshold` is used.
* Only `temperature_c` and `pressure_kpa` are available. Operating context (flight phase, power
  setting, ambient conditions) is missing, so normal operational changes can look anomalous.
* A gap in telemetry raises `minutes_since_prev` and can flag the first reading after the gap.
* Failure prediction depends entirely on complete and verified `maintenance_log` labels. Missing
  records turn into false negatives. One chronological test period may not represent future
  performance.
* Probabilities are exposed only when calibration is validated. Even then they are advisory and
  need human review (`PendingReview`).
* Data is processed in memory with pandas, which suits development-scale volumes.
* The PostgreSQL repository is not covered by the automated tests; they use an in-memory fake.
* A worker that is killed mid-job leaves its run in `Running`, because no lease reaper exists yet.
* Synthetic demo data (`data_source = SYNTHETIC_DEMO`) is flagged in audits and in model limitations. It is
  for development only.

## Development environment (isolated synthetic data)

`AFP_ENVIRONMENT` is `operational` (default) or `development`. `development` is **refused** when `DB_NAME` is the
operational `aircraft_predict_db`. The development config lives in the git-ignored `.env.synthetic`
(`DB_NAME=aircraft_predict_db_synth`, `DB_FORBIDDEN_NAMES=aircraft_predict_db,aircraft_app`,
`AFP_MODEL_DIR=%LOCALAPPDATA%\AircraftFailurePrediction\models-synthetic`, `AFP_ENVIRONMENT=development`) and is
selected with `AFP_ML_ENV_FILE`.

**`ScoreCandidate`** (job type; CLI `python -m afp_ml score-candidate --model-version-id <id>`) scores every aircraft
with an explicitly chosen FailurePrediction model of **any** status, as of the day after the latest processed
telemetry (recency is judged against that cutoff, not the wall clock). It reuses the operational scoring code
(`_failure_predictions`), so features, probability gating and idempotency (`ON CONFLICT`) are identical; rows carry
`feature_snapshot.environment = "development"`. It **refuses** unless `AFP_ENVIRONMENT=development`, so an unapproved
candidate can never write predictions into operational tables. Operational scoring (`Score`) still uses only the
approved, Active model.


### Horizon experiment (`horizon-experiment`, development only)

`AFP_ML_ENV_FILE=../.env.synthetic python -m afp_ml horizon-experiment [--json]` compares the 30-day horizon with
7 days on the development data. It reuses `compute_readiness` (labels, features, censoring) and
`train_failure_model_rolling` (same model, folds, Platt fit on earlier folds, the unchanged gates); only
`prediction_horizon_days` differs. Both horizons are evaluated on the **same cutoff span** (the 7-day set is trimmed
to the 30-day eligible span), plus the 7-day full eligible span. It is refused unless `AFP_ENVIRONMENT=development`
(before connecting), connects **without a writer DSN**, records no `pipeline_run`, registers nothing, and writes
`report.json`, `manifest.json` and the unregistered `candidate_7d_bundle.joblib` (read-only) to
`<AFP_MODEL_DIR>/experiments/7-day-experiment/<stamp>/` and the same key in the environment's bucket (bounded retry:
MinIO on this host intermittently reports a storage lock as `AccessDenied`). The outcome is one of *Promising
candidate*, *Informative but uncalibrated*, *Insufficient evidence* (held-out positives or negatives < the gate
minimum, or < 3 usable folds) or *No improvement*. Rolling training now also records `metrics.held_out_by_fold`
(each held-out fold scored alone with the same mapping and threshold) to expose drift.

## Model artifact storage (MinIO)

The local `AFP_MODEL_DIR` stays the integrity-checked working cache; MinIO holds the durable copy.

| Environment | Bucket | MinIO user | Config |
|---|---|---|---|
| operational | `models` | `afp-ml-models` | root `.env` |
| development (synthetic) | `synthetic-dev` | `afp-ml-synthetic` | `.env.synthetic` |

Keys: `AFP_ARTIFACT_ENDPOINT`, `AFP_ARTIFACT_BUCKET`, `AFP_ARTIFACT_PREFIX` (default `models`),
`AFP_ARTIFACT_ACCESS_KEY`, `AFP_ARTIFACT_SECRET_KEY`, `AFP_ARTIFACT_FORBIDDEN_BUCKETS` (each environment forbids the
other's bucket, so a missing key can never fall back to it — that is a `ConfigError`). No bucket configured = local
directory only. The secret is never printed (`Settings.public_dict` reports `artifact_credentials_configured`).

* **Save**: the artifact and `metadata.json` are uploaded to `<prefix>/<model>/<version>/` and read back; the run
  fails if the durable copy cannot be written. `model_version.artifact_uri` records `s3://bucket/key`.
* **Load**: a missing local file is fetched from the bucket and cached **only after** its SHA-256 matches
  `model_version.artifact_sha256`.
* **Immutable**: an existing object with different content is never overwritten; the users have no delete right.
* **Backfill**: `python -m afp_ml sync-artifacts` (per environment) uploads every model without `artifact_uri`
  after verifying its local hash. Idempotent.
* **Provisioning**: `tools/minio/provision_model_storage.py` (needs the MinIO root credentials in
  `aircraft-fde-auto-etl-postgres/.env`) creates the buckets, the no-delete policies and the users, writes the keys
  into the env files without printing them, and proves with each user's own credentials that it can write its own
  bucket, cannot delete, and cannot write the other bucket. `init_minio.py` also creates both buckets on a fresh stack.
* The development bucket also archives the supplied synthetic source files under `bronze/`
  (`tools/data/load_synthetic_dev.py --archive-env-file .env.synthetic [--archive-only]`).

## Sensor forecasting (`SensorForecast`)

`python -m afp_ml train --target forecast` (job `TrainSensorForecast`) and `python -m afp_ml forecast` (job
`ForecastSensors`, daily stage 3). Code: `afp_ml/forecast.py`, `afp_ml/forecast_jobs.py`.

* **Method** (transparent): forecast = exponentially weighted level (half-life 72 h) of the aircraft's last 28 days of
  hourly Valid readings + its hour-of-day profile. Anchored at the aircraft's last recorded reading; points strictly after it.
* **Validation** (rolling origin, one origin every 72 h): per horizon (24/168/720 h) and metric, MAE against two naive
  baselines - persistence and last-24-hours repeat. 80 % interval quantiles are fitted on the EARLIER origins and their
  coverage judged on the LATER origins. Supported only if MAE <= `AFP_FORECAST_MAX_MAE_RATIO` (0.95) x the better
  baseline, coverage in [`AFP_FORECAST_COVERAGE_LOW` 0.70, `AFP_FORECAST_COVERAGE_HIGH` 0.90] and >=
  `AFP_FORECAST_MIN_EVAL_ORIGINS` (5) evaluation origins. Evidence: `metrics.forecast_validation`, `validation_criteria`,
  `supported_horizons`; shown as a table on the model page.
* **Governance**: training records a `Trained` candidate (even with nothing supported, so the evidence is visible);
  registration needs >= 1 supported horizon; approval is refused without one; forecasts are written and served only for
  the **Active** model, with its artifact hash verified. Unsupported metrics are written as null.
* **Storage**: `sensor_forecast` (migration `SensorForecasts`), points jsonb, unique (aircraft, model, horizon,
  data_as_of) - re-runs insert nothing. ML writer has INSERT only.
* **Results 2026-10-28** (operational, 22-27 evaluation origins): supported OneDay temperature (ratio 0.944), SevenDays
  pressure (0.909), ThirtyDays pressure (0.914) + vibration (0.897); coverage 0.75-0.80. Margins are small and the
  temperature MAE is ~120 C (hour-to-hour swings): the forecast gives the typical level, not individual spikes.

## Daily telemetry simulator

`python -m afp_ml simulate` (job type `SimulateTelemetry`; `--dry-run` writes nothing and records no run).
Configuration — rules as data, with provenance — in `tools/simulator/telemetry_simulator.json`.

* **When**: Windows Task Scheduler at **00:00 IST** (`tools/simulator/register-daily-task.ps1`; the start boundary
  carries an explicit `+05:30`). Missed runs start when the PC is available; one instance at a time (task policy +
  an OS file lock). Registering does not run it. `tools/simulator/run-daily.ps1` runs `simulate`, then — only if it
  succeeded — `score`, `forecast` and `AFP.Api process-anomalies` (each even if an earlier one failed), as four
  separately recorded pipeline runs. Task result = the simulate exit code if it failed, else max(score, forecast, anomalies); a wrapper
  error is logged and exits 1, never 0. Log writes retry a transiently locked file (antivirus/indexer scans) and a
  logging failure never aborts a stage.
* **What**: every hourly reading from the file's last timestamp up to (excluding) today's IST midnight, i.e. only
  fully past hours — never future-dated; missed days are caught up. Records older than the rolling six-month
  window are removed **from the raw file only** (window convention: six months ending on the last date, e.g.
  04-23 → 10-22); PostgreSQL history is never deleted.
* **How values are made**: per aircraft and metric, an AR(1) continuation of its own last 30 days (mean, std,
  lag-1 autocorrelation), clamped to that aircraft's observed range, missing at its observed rate, seeded by
  aircraft + timestamp (deterministic). `status_flag` uses the source system's rule inferred from the file —
  it reproduces **all 26,707** flags in it (a test asserts 0 mismatches against the real file). No failure,
  maintenance or degradation is ever injected; an aircraft without history is refused, never invented.
* **Safety**: the original file is copied once (with its SHA-256) to
  `%LOCALAPPDATA%\AircraftFailurePrediction\simulator\original\` before the first change; every change keeps the
  previous version (`versions\`, last 14). The delta goes to `outbox\` first, then the source is replaced atomically
  (temp file, fsync, re-parse + re-hash, `os.replace`, retried on transient Windows locks). Recovery on start removes a
  half-written temp file and discards an outbox delta whose records never reached the source (deletes retry
  transient locks too). Holding the OS lock proves no simulation is alive on this host, so an earlier `Running`
  `SimulateTelemetry` row from this host (process killed, PC switched off) is closed as `Failed` ("Interrupted: …");
  rows from other hosts and other job types are left alone, and a dry run closes nothing.
* **ETL**: the delta goes through the **existing** ingestion path — the API's raw-upload service in CLI mode
  (`AFP.Api ingest-raw-telemetry --file … --run-as <DataEngineer> --wait …`), so validation, duplicate detection,
  Bronze storage, the import row and the audit entry are the endpoint's own. The CLI reuses an import with the same
  file hash instead of re-uploading and waits for the Spark ETL. The run **succeeds only when the import is
  Completed**; a Failed or still-pending import fails the run with the real reason and the delta stays in the
  outbox, to be confirmed by the next run. The ML roles never read `telemetry_import` (they are explicitly denied it).

### Operating scenario (rotating forecast breaches) - 2026-10-28

**Generated data, labelled.** With `scenario.enabled` in `telemetry_simulator.json`, `simulate` replaces the AR(1)
continuation with `afp_ml/scenario.py`: every `rotation_days` (14) from `anchor_date`, two aircraft take the roles in
`roles_per_period` - **Critical** (engine temperature ~785 C, above the 700 C limit) and **HighCritical** (~790 C and oil
~27 PSI, below 35 PSI) - and the other four stay Healthy (~560 C, ~46 PSI). Over 3 periods every aircraft takes a turn;
consecutive periods never repeat a pair. Values are deterministic (sha256 of tail + epoch) with a daily cycle; vibration
is never a breach condition. Scenario records get the `TEL-SCN-` ID prefix (the ETL `data_source` is global, so the ID
is the label); the shared ID space covers both prefixes.

`tools/data/apply_scenario_history.py` rewrites the last `rewrite_history_days` (28) of history so the forecaster sees the
current pattern: `UPDATE` of values with `data_source = 'SCENARIO'` (schema owner; refuses `aircraft_app`), optional
`--raw-file` (under the simulator lock, version backup `versions/...pre-scenario...json`), `--extend-to-now` (gap
readings via `load_synthetic_dev`, `data_source = SCENARIO`), `--dry-run`. Take a `pg_dump` first.

The daily wrapper (`run-daily.ps1`) runs four stages: simulate, score, forecast, process-anomalies; exit = max of the
last three. Forecasts with the Active `SensorForecast` model then drive the anomalies and work orders (see API.md).
