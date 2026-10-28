# Plan — Real Failure Data Import and Supervised 30-Day Prediction

Status: **PLAN ONLY (not implemented).** Supervised failure prediction remains **BLOCKED** until the steps
below are completed with real, domain-confirmed data. Nothing in this plan fabricates labels, failures or metrics.

## 1. What we need from you (inputs and decisions)

| # | Item | Why |
|---|---|---|
| D1 | The failure file(s) — CSV or Excel (`.xlsx`) — plus a short data dictionary | Columns, meanings and units must be known, not guessed |
| D2 | **Failure-code list**: which codes represent a *genuine unscheduled failure* (vs. scheduled tasks, deferrals, cosmetic or no-fault-found) | Label definition; stored in `ML_FAILURE_CODES` |
| D3 | **Event date semantics**: is the date the failure *occurrence*, *detection* or *work completion*? Time zone of the file? | Horizon labels must use the occurrence/detection time, in UTC |
| D4 | **Identifier mapping**: how file aircraft IDs map to `aircraft.registration_number` (and component IDs to sensors, if applicable) | Labels must relate to the same aircraft/component as the telemetry |
| D5 | **Sign-off owner** (maintenance/reliability engineering) who approves D2–D4 | Sets `ML_LABEL_DEFINITION_CONFIRMED=true` only after sign-off |
| D6 | Whether "no-fault-found" and repeat write-ups of the same defect count as one event | Duplicate-event leakage and double counting |
| D7 | Approved temperature/pressure limits per component type and the mapping from telemetry sensors to component types (for the rule-based Normal/Critical/HighCritical classification), and whether equality counts as a breach | Currently no approved limits exist; the rule returns Unclassified |

## 2. Data audit (read-only, first step)

A new `python -m afp_ml failure-audit --file <path>` command (planned) will, without writing to the database:

* Preserve the original file (SHA-256, read-only copy in the import storage folder).
* Report columns, inferred types, row count, date range, time-zone evidence, per-column missing values,
  exact and near-duplicate rows, unknown aircraft identifiers, code frequency table, events per aircraft/component
  and per month.
* Compare event dates with telemetry coverage per aircraft (events outside telemetry coverage cannot be used).
* Produce a JSON + Markdown report for the sign-off owner.

## 3. Import and validation design (after sign-off)

* **Format**: CSV (UTF-8) or Excel (`.xlsx`, first sheet or named sheet). Required columns (final names from D1):
  `aircraft_registration`, `event_date` (with offset or a declared file time zone), `failure_code`,
  `description`; optional `component_id`, `work_order_reference`, `confirmed` (Y/N), `source_reference`.
* **Schema change (additive migration)**: add `failure_code varchar(32)` and `source_reference varchar(128)` to
  `maintenance_log`, plus a unique key on (`aircraft_id`, `component_id`, `occurred_at_utc`, `failure_code`) for
  idempotent re-imports. A `failure_code` reference table seeded from a reviewed CSV (`tools/db/seed/csv/failure_code.csv`,
  columns: code, description, is_genuine_failure) replaces the free-text list in `ML_FAILURE_CODES`.
* **Path**: through the existing API import pattern (upload → 202 → background validation → accepted/rejected rows
  with row-level reasons → summary), stored as `maintenance_log` rows with `source = Import`, `event_type = Unscheduled`
  only for codes marked genuine, `is_verified = true` only when the file says confirmed.
* **Rejections**: unknown aircraft, unparseable/ambiguous date, unknown code, future date, duplicate key, missing
  required value. Nothing is inferred from telemetry thresholds or `quality_status`.

## 4. Labels and leakage controls

* Example = (aircraft, cutoff day). Label = 1 if a **confirmed genuine** failure occurs in (cutoff, cutoff + 30 days].
* Features use only telemetry strictly before the cutoff (already implemented and tested).
* Never use as features: maintenance outcome fields, work-order fields, failure codes, identifiers, anything after the cutoff.
* Repeat write-ups of one defect (D6) are collapsed into one event before labelling.
* Examples whose horizon window is not fully observed (end of data) are dropped.

## 5. Evaluation protocol (already implemented in `failure.py`, gated)

* Chronological train / validation / test split with a purge gap ≥ horizon; aircraft-aware reporting when ≥ 5 aircraft
  have events; test set touched once.
* Candidates: prior-baseline, Logistic Regression, Random Forest, HistGradientBoosting; selection on validation PR-AUC only.
* Report per split: positives/negatives, precision, recall, PR-AUC vs. prevalence baseline, confusion matrix at the
  selected threshold, false alerts per aircraft per 30 days, Brier score and reliability bins. Probabilities are shown
  as calibrated only if the calibration criteria pass.

## 6. Gates (all must pass; otherwise status stays BLOCKED with the exact reason)

1. `ML_LABEL_DEFINITION_CONFIRMED=true` (domain sign-off recorded).
2. ≥ 30 confirmed events, ≥ 5 aircraft with events, ≥ 6 distinct months, ≥ 200 negatives, ≥ 5 positives in each split.
3. Selected model beats the baseline on validation and test PR-AUC.
4. Leadership approval in the API; Data Engineer activation. Nothing activates automatically.

## 7. Expected outcome with ~2 months of telemetry

With two months of telemetry, a 30-day horizon and a purge gap, at most ~1 month of usable cutoffs exist; it is
**unlikely** that the sample-size and split gates pass. The expected honest result is: failure data imported and
audited, labels defined, supervised training **BLOCKED** with quantified reasons, while anomaly detection and
descriptive risk indicators continue. Longer telemetry history is the main lever.

## 8. Next-step deliverables (when you approve this plan)

1. `failure-audit` command + report (read-only).
2. Additive migration (`failure_code`, `source_reference`, idempotency key) + `failure_code.csv` seed.
3. Failure import endpoint/worker with row-level results, tests (dates, time zones, duplicates, unknown codes, leakage).
4. Readiness audit and evaluation run on the real data; documented result (BLOCKED or EVALUATED).
