# REST API Reference (v1)

Base URL (development): `http://localhost:5080`. All business endpoints are under `/api/v1`.
Swagger UI: `http://localhost:5080/swagger` (Development environment only).

## Conventions

* JSON, camelCase property names, enums serialised as strings (PascalCase names).
* Timestamps are ISO-8601 UTC strings (`2026-08-14T06:30:00Z`).
* Authentication: `Authorization: Bearer <jwt>`. Tokens expire after 60 minutes and are
  revoked server-side on logout, account disable or role change.
* Paged responses:

```json
{ "items": [], "page": 1, "pageSize": 25, "totalCount": 0, "totalPages": 0 }
```

  Query parameters `page` (default 1) and `pageSize` (default 25, max 200 unless stated).
* Errors are RFC 9457 `application/problem+json`:

```json
{ "type": "https://httpstatuses.io/400", "title": "Validation failed", "status": 400,
  "detail": "...", "code": "validation_failed", "traceId": "...",
  "errors": { "email": ["Email is required."] } }
```

| Status | Meaning |
|---|---|
| 400 | Validation failure (`errors` populated) or invalid business-rule transition (`code` = `invalid_transition`, `business_rule_violation`) |
| 401 | Missing/expired/revoked token, or invalid credentials |
| 403 | Authenticated but role not permitted; `code` `account_pending` / `account_disabled` on login |
| 404 | Not found |
| 409 | Conflict (duplicate registration number, file already imported, concurrency) |
| 423 | Account locked (`code` = `account_locked`) |
| 429 | Rate limit exceeded |

## Roles

**RBAC v2 (2026-10-21) — authoritative; supersedes the role columns in the tables further below.**
Two roles, seeded in the `role` table (`tools/db/seed/csv/role.csv`) and assigned through `user_role`:

| role_id | roleName | Purpose |
|---|---|---|
| 1 | `FlightDispatcher` | **Scoped to five approved tabs (2026-10-22)**: Health Dashboard (`/dashboards/dispatcher`), Fleet Health (`/aircraft` incl. the `health` block, aircraft status, inspection flags, telemetry reads for traceability), Anomalies / Work Orders (`/anomalies` + acknowledge/investigate, full work-order planning), Reports / Maintenance Log (`/reports/fleet-health`, `/reports/maintenance-history`, `/maintenance-logs` read), `/technicians` (to assign work). **User Management (`/admin/users`, `/admin/roles`) moved to the DataEngineer on 2026-10-26 — the dispatcher now gets 403.** **No longer holds**: telemetry imports/data-quality, pipelines, model registry, predictions list/metadata, aircraft master-data create/update, leadership and data-engineer dashboards, audit. |
| 2 | `MaintenanceTechnician` | Sees **only work orders assigned to them** (with the linked aircraft summary and AI diagnosis embedded), starts inspection, records findings, resolves with a report, logs maintenance for those aircraft. |
| 3 | `DataEngineer` | **Data and Model areas** (added 2026-10-21): telemetry query/series, CSV and raw JSON imports + status/retry, data quality, pipelines (run jobs), models (list/evaluate/**register/activate/retire** — approval stays with the Dispatcher), predictions and anomalies (read only), aircraft **list** (for filters) and master-data create/update, `GET /dashboards/data-engineer`, data-quality and model-performance reports. No work orders, maintenance logs, aircraft detail/status/flags, users, audit or dispatcher/technician dashboards. |

Technician-accessible endpoints (everything else returns **403** for technicians):
`GET/POST /auth/*`, `GET /dashboards/technician`, `GET /work-orders` (always filtered to own), `GET /work-orders/{id}` (own only, else 404),
`POST /work-orders/{id}/start-inspection|inspection|resolve` (own only), `POST /maintenance-logs` (only for an aircraft with an open work order assigned to them).
Dispatcher-only work-order actions: create, PATCH, assign, reopen, close, cancel. `allowedTransitions` in `WorkOrderDto` is filtered by the caller's role.

**Model governance (policy `ModelApproval`, 2026-10-23).** `POST /models/{id}/approve` and `POST /models/{id}/reject`
require the **DataEngineer** role — the project owner's decision; the BRD itself names no model approver. The permission is
deliberately narrow: it grants candidate review only, not training, activation, prediction review or any dispatcher
capability. Approval records the approving user id and timestamp on `model_version` and writes a `ModelApproved`
`action_audit` entry.

Approval is refused (**400**) unless all of the following hold:
* failure-prediction readiness is not `Blocked` (`prediction_blocked`),
* the required evaluation metrics are present — precision, recall, pr_auc, false_alert_rate (`missing_metrics`),
* the test split records its class counts (`evaluation_not_assessable`) and has **≥30 positives and ≥30 negatives**
  (`insufficient_test_examples`). Precision and false-alert rate are estimated from negatives, so a handful of them
  cannot establish either however high PR-AUC looks.

Only an `Approved` candidate can be activated (`ModelLifecycle.Activate`), so training never implies production use.

**Capabilities with no role holder (policy `Unassigned`, 2026-10-22).** These lost their only holder when the Dispatcher
was scoped to five tabs and still return **403 for every role** until the business assigns one:
`POST /predictions/{id}/review`, `POST /predictions/{id}/outcome`, `GET /audit/actions`, `GET /audit/auth`,
`GET /dashboards/leadership`. Nothing was deleted — naming a role restores them.
The frontend must read roles from `GET /admin/roles` (never hard-code role lists). Abbreviations in older tables below: read **FD** as Dispatcher and everything else (MT/DE/LD/ADM/ANY) as "see the list above".

## Shared DTOs

```ts
// User ids are 64-bit integers (JSON numbers; numeric strings are also accepted in requests).
UserDto { id: number, username, email, displayName, roles: ("FlightDispatcher"|"MaintenanceTechnician")[],
          status: "Pending"|"Active"|"Disabled", createdAtUtc, lastLoginAtUtc|null }
AdminUserDto extends UserDto { approvedAtUtc|null, failedLoginCount, lockoutEndUtc|null, disabledReason|null }
RoleDto { roleId: number, roleName }
TokenResponse { accessToken, tokenType: "Bearer", expiresAtUtc, refreshToken, refreshTokenExpiresAtUtc, user: UserDto }

LatestRiskDto { riskCategory: "Low"|"Elevated"|"High"|"Undetermined", basis: "Model"|"DescriptiveIndicator",
                predictionTimestampUtc, horizonDays }

AircraftSummaryDto { id, registrationNumber, model, manufacturer, fleetGroup,
  operationalStatus: "Available"|"UnderMaintenance"|"Grounded", statusReason|null,
  lastTelemetryAtUtc|null, lastInspectionAtUtc|null,
  openAnomalyCount, activeInspectionFlagCount, openWorkOrderCount, latestRisk: LatestRiskDto|null }

DispatchAdvisoryDto { readiness: "NoKnownRestrictions"|"Restricted"|"NotAvailable",
  reasons: string[], disclaimer: string }   // never states an aircraft is "safe"

AircraftDetailDto extends AircraftSummaryDto { serialNumber|null, statusChangedAtUtc, createdAtUtc,
  dispatchAdvisory: DispatchAdvisoryDto, activeInspectionFlags: InspectionFlagDto[],
  recentMaintenance: MaintenanceLogDto[], recentAnomalies: AnomalyDto[],
  latestPredictions: PredictionDto[], openWorkOrders: WorkOrderDto[] }

InspectionFlagDto { id, aircraftId, registrationNumber, severity: "Advisory"|"Restrictive"|"Grounding",
  reason, raisedBy, raisedAtUtc, isActive, clearedBy|null, clearedAtUtc|null, clearanceNotes|null,
  anomalyId|null, predictionId|null, workOrderId|null }

MaintenanceLogDto { id, aircraftId, registrationNumber, componentId|null,
  eventType: "Scheduled"|"Unscheduled"|"Inspection", occurredAtUtc, description,
  actualFailureConfirmed: bool|null, isVerified, source: "Manual"|"WorkOrder"|"Import", workOrderId|null }

TelemetryRecordDto { id, aircraftId, sensorId|null, recordedAtUtc, temperatureValue|null, temperatureUnit|null,
  temperatureC|null, pressureValue|null, pressureUnit|null, pressureKpa|null, dataSource,
  qualityStatus: "Valid"|"Suspect", qualityFlags: string[], ingestedAtUtc, importId|null,
  sourceRecordId|null, vibrationIndex|null,   // added 2026-10-21 (raw JSON telemetry_id, metrics.vibration_index)
  sourceStatusFlag|null }                      // source NORMAL/WARNING/CRITICAL/HIGH CRITICAL/UNKNOWN — threshold flag, not a failure label

TelemetryImportDto { id, fileName, fileSha256, status: "Queued"|"Processing"|"Completed"|"Failed",
  totalRows, acceptedRows, rejectedRows, duplicateRows, warningCount, warnings: string[],
  errorMessage|null, requestedBy, requestedAtUtc, startedAtUtc|null, completedAtUtc|null,
  source: "Csv"|"RawJson" }   // source added 2026-10-21; requestedBy "ETL (placed directly in object storage)" when discovered in MinIO

AnomalyDto { id, aircraftId, registrationNumber, sensorId|null, telemetryRecordId|null, observedAtUtc,
  anomalyScore: number, severity: "Low"|"Medium"|"High", modelName, modelVersion, modelVersionId,
  scoredAtUtc, investigationStatus: "New"|"Acknowledged"|"Investigating"|"Dismissed"|"ConfirmedIssue",
  acknowledgedBy|null, acknowledgedAtUtc|null, investigationNotes|null,
  featureValues: object, scoreNote: string }   // scoreNote explains the score is not a probability

PredictionDto { id, aircraftId, registrationNumber, componentId|null, predictionTimestampUtc, horizonDays,
  riskCategory, basis: "Model"|"DescriptiveIndicator", probability: number|null, probabilityValidated: bool,
  featureSnapshot: object, modelName|null, modelVersion|null,
  approvalStatus: "PendingReview"|"Approved"|"Rejected", reviewedBy|null, reviewedAtUtc|null, reviewNotes|null,
  actualOutcome: "Unknown"|"UnscheduledMaintenance"|"NoUnscheduledMaintenance", outcomeRecordedAtUtc|null,
  disclaimer: string,
  predictionState: "Scored"|"InsufficientTelemetry"|"MissingFeatures",   // added 2026-10-21 (additive)
  featureCutoffUtc }                                                       // latest telemetry used; added 2026-10-21
// No prediction row for an aircraft = no prediction available. No eligible model = /predictions/metadata activeFailureModel null.

WorkOrderDto { id, workOrderNumber, aircraftId, registrationNumber, componentId|null, anomalyId|null,
  predictionId|null, priority: "Low"|"Medium"|"High"|"Critical", title, description,
  status: "Open"|"Assigned"|"InInspection"|"Resolved"|"Closed"|"Cancelled",
  assignedTechnicianId|null, assignedTechnicianName|null, createdBy, createdAtUtc, dueAtUtc|null,
  isOverdue: bool, inspectionFindings|null, correctiveAction|null, actualFailureConfirmed: bool|null,
  resolutionNotes|null, resolvedAtUtc|null, closedAtUtc|null, updatedAtUtc,
  allowedTransitions: string[], concurrencyStamp }
// assignedTechnicianId is a user id (number). The detail embeds everything a technician needs (no other endpoints required):
WorkOrderDetailDto extends WorkOrderDto { history: ActionAuditDto[], aircraft: AircraftSummaryDto,
  anomaly: AnomalyDto|null,        // AI diagnosis: anomaly score (not a probability) + feature values
  prediction: PredictionDto|null } // risk indicator / model output with its disclaimer

PipelineRunDto { id, jobType: "DatasetAudit"|"TrainAnomaly"|"RegisterModel"|"Score"|"TrainFailure"|"TelemetryImport",
  status: "Queued"|"Running"|"Succeeded"|"Failed"|"Blocked"|"Cancelled", trigger: "Api"|"Cli",
  requestedBy|null, requestedAtUtc, startedAtUtc|null, completedAtUtc|null,
  parameters: object, resultSummary: object|null, errorMessage|null, modelVersionId|null, recordsProcessed|null }

ModelVersionDto { id, modelName, version, modelType: "AnomalyDetection"|"FailurePrediction",
  algorithm, status: "Trained"|"Registered"|"Approved"|"Active"|"Retired"|"Rejected",
  artifactSha256, featureNames: string[], hyperparameters: object, metrics: object,
  trainingDataFromUtc|null, trainingDataToUtc|null, trainingRowCount, predictionHorizonDays|null,
  limitations|null, notes|null, createdAtUtc, registeredAtUtc|null, approvedBy|null, approvedAtUtc|null,
  activatedAtUtc|null, retiredAtUtc|null }

ActionAuditDto { id, userId|null, userName|null, action, entityType, entityId, details: object|null,
  occurredAtUtc, correlationId|null }
AuthAuditDto { id, userId|null, email|null, eventType, succeeded, ipAddress|null, userAgent|null,
  detail|null, occurredAtUtc }
JobAcceptedDto { id, status, statusUrl }
```

## Authentication — `/api/v1/auth`

| Method | Path | Auth | Body → Response |
|---|---|---|---|
| POST | `/register` | anonymous, rate-limited | `{ username, email, displayName, password, confirmPassword }` → **202** `{ message }`. Username: 3–64 chars `[a-z0-9._-]` (stored lower-case). Identical response whether or not the username/email exists (no enumeration). New accounts are `Pending` with no roles. |
| POST | `/login` | anonymous, rate-limited | `{ username, password }` → **200** `TokenResponse`. 401 `invalid_credentials`; 403 `account_pending` / `account_disabled`; 423 `account_locked` (5 failures → 15 min lockout). |
| POST | `/refresh` | anonymous, rate-limited | `{ refreshToken }` → **200** `TokenResponse` (OAuth 2.0 refresh-token grant with **rotation**: the old refresh token is consumed). 401 `invalid_refresh_token`. Re-using a consumed token revokes the whole token family (theft detection). Access token: 60 min; refresh token: 7 days (configurable). |
| POST | `/logout` | bearer | `{ refreshToken? }` → **204**. Revokes the presented access token (jti) and the refresh-token family. |
| POST | `/forgot-password` | anonymous, rate-limited | `{ email }` → **202** `{ message }` — always identical. |
| POST | `/reset-password` | anonymous, rate-limited | `{ token, newPassword, confirmPassword }` → **204**; 400 `invalid_or_expired_token`. Tokens are single-use, expire in 30 minutes, and resetting revokes all existing sessions. |
| GET | `/me` | bearer (any status) | `UserDto` |

Password policy: 12–128 characters, at least one upper-case letter, one lower-case letter, one digit and one symbol.

## Administration — `/api/v1/admin/users` (DE)

Policy `DataEngineer` since 2026-10-26 (was the dispatcher); `/admin/roles` likewise. The dispatcher keeps `GET /technicians` for assignment.

| Method | Path | Body → Response |
|---|---|---|
| GET | `/` `?status&role&search&page&pageSize` | `Paged<AdminUserDto>` (search matches username, email, display name) |
| POST | `/{id}/approve` | `{ roles: string[] }` (≥1) → `AdminUserDto` (Pending → Active; creates `user_role` rows) |
| PUT | `/{id}/roles` | `{ roles: string[] }` (≥1) → `AdminUserDto` (removed assignments become Inactive history rows; sessions and refresh tokens revoked) |
| GET | `/api/v1/admin/roles` | → `RoleDto[]` (active roles from the seeded `role` table) |
| POST | `/{id}/disable` | `{ reason }` → `AdminUserDto` (sessions revoked; cannot disable yourself) |
| POST | `/{id}/enable` | → `AdminUserDto` |

## Dashboards — `/api/v1/dashboards`

| GET | Roles | Response |
|---|---|---|
| `/dispatcher` | FD, LD | `{ fleetCounts: { total, available, underMaintenance, grounded }, readiness: [{ aircraftId, registrationNumber, model, fleetGroup, operationalStatus, dispatchReadiness, restrictions: string[], latestRiskCategory|null, openAnomalyCount, lastTelemetryAtUtc|null }], riskOverview: [{ riskCategory, count }], recentAlerts: AnomalyDto[], activeRestrictions: InspectionFlagDto[], aircraftWithoutPrediction }` |

Phase 7 additions to `/dispatcher` (2026-10-27, additive): `thresholds: [{ code, measurement, comparison: "LessThan"|"GreaterThan",
value, unit, isValidated, description }]` - the measured limits from `health_threshold` (rows with a value whose measurement
is not `None`/`FailureRiskScore`), and per `aircraftRisk` row `breachCodes: string[]` (limits breached in the latest
24 h window; empty = none). A prediction counts as a model prediction only when its model is **Approved or Active**
(`ModelGovernance`); a candidate's score (development scoring) is `predictionStatus: "NoModelPrediction"` with no
probability and no provisional score.

2026-10-28 additions (additive): per `aircraftRisk` row `forecastCondition` (`"Critical"` = a forecast temperature breach;
`"HighCritical"` = forecast temperature **and** oil-pressure breaches; `null` = no open forecast incident) and
`forecastAnomalyId` (the open `ForecastModel` anomaly behind it). Vibration is never a breach condition. The same
`forecastCondition` is on each `GET /aircraft` list item (`AnomalyRules.ForecastCondition`), feeding Fleet Health's
Aircraft health card. Since 2026-10-28 each list/detail `latestRisk` is the latest DESCRIPTIVE indicator (falling
back to the latest prediction when none exists); the model's score stays in `health.failureRisk*`. Risk categories add
`Critical` and `HighCritical` (forecast condition).

| `/technician` | MT | `{ counts: { assignedOpen, inInspection, overdue, awaitingClosure }, assignedWorkOrders: WorkOrderDto[], recentlyResolved: WorkOrderDto[] }` — only the caller's own tickets |
| `/data-engineer` | DE | `{ dataQuality: { totalRecords, validRecords, suspectRecords, validPercent }, latestImports: TelemetryImportDto[], recentRuns: PipelineRunDto[], latestAudit: PipelineRunDto|null, latestScoring: PipelineRunDto|null, featurePreparation: { status, lastPreparedAtUtc|null, featureNames: string[] }, models: ModelVersionDto[], failurePredictionStatus }` |
| `/leadership` | LD | `{ fleetCounts, riskDistribution: [{ riskCategory, count }], maintenanceTrend: [{ month, scheduled, unscheduled, inspections }], unscheduled: { last30Days, previous30Days, last90Days }, anomalyTrend: [{ date, count }], openWorkOrders, modelSummaries: ModelVersionDto[], failurePredictionStatus, aircraftWithoutPrediction }` |

**`/dispatcher?fleetGroup=<name>`** (2026-10-27, additive): every section — including the original fields — is for the
selected fleet group (omitted = all fleets; an unknown name is **400**). New fields:
`selectedFleetGroup`, `fleets: [{ fleetGroup, aircraftCount }]` (the real groups, e.g. `Unassigned`),
`health: { aircraft, assessable, highRisk, withinLimits, notAssessable, stale, latestReadingAtUtc, thresholdsValidated }`,
`prediction: { horizonDays, validated, low, medium, critical, notValidated, noModelPrediction, latestPredictionAtUtc }`
(only **validated** probabilities are tiered; the rest are counted separately, never as Low),
`workOrders: { open, assigned, inInspection, resolved, closed, cancelled }` (same rows as `/work-orders`),
`aircraftRisk: [{ aircraftId, registrationNumber, fleetGroup, isHighRisk|null, isStale|null, latestReadingAtUtc, recommendedInspection,
failureProbability|null, riskTier|null, predictionStatus: "Validated"|"NotValidated"|"NoModelPrediction", predictionCategory,
predictionAtUtc, featureCutoffUtc, openWorkOrders }]`, `lastScoringRunAtUtc`. Health uses the same evaluator as `/aircraft`.

Risk categories in `riskOverview`/`riskDistribution` come from the seeded `risk_status` table (ordered by `display_order`);
aircraft with no prediction are counted in `aircraftWithoutPrediction` (added 2026-10-21, additive) instead of being
reported as a risk category. The fleet-health report carries the same field.

`failurePredictionStatus` = `{ status: "Blocked"|"Ready"|"Active", reasons: string[], horizonDays }`.

## Fleet — `/api/v1/aircraft`

| Method | Path | Roles | Body → Response |
|---|---|---|---|
| GET | `/` `?search&status&fleetGroup&model&page&pageSize` | FD, DE | `Paged<AircraftSummaryDto>` (each item carries the `health` block below) |
| GET | `/fleet-groups` | ANY | `string[]` |
| GET | `/{id}` | ANY | `AircraftDetailDto` |
| POST | `/` | ADM, DE | `{ registrationNumber, model, manufacturer, fleetGroup, serialNumber? }` → 201 `AircraftDetailDto` |
| PUT | `/{id}` | ADM, DE | same body → `AircraftDetailDto` |
| POST | `/{id}/status` | MT | `{ status, reason }` → `AircraftDetailDto`. Rule: `Available` is refused while a `Grounding` flag is active or a `Critical` work order is open. ML output never changes status. |
| GET | `/{id}/maintenance-history` `?page&pageSize` | ANY | `Paged<MaintenanceLogDto>` |
| GET | `/{id}/inspection-flags` `?activeOnly=true` | ANY | `InspectionFlagDto[]` |
| POST | `/{id}/inspection-flags` | MT | `{ severity, reason, anomalyId?, predictionId?, workOrderId? }` → 201 `InspectionFlagDto` (`Grounding` sets the aircraft to `Grounded`) |
| POST | `/api/v1/inspection-flags/{flagId}/clear` | MT | `{ clearanceNotes }` → `InspectionFlagDto` |

### `derivedState` (added 2026-10-23, additive) — on `AircraftSummaryDto` and `AircraftDetailDto`

Presentation state derived from `operationalStatus` plus the current maintenance workflow. `operationalStatus`
remains the stored, authoritative value and is **not** replaced; `derivedState` only stops two very different
situations being displayed identically.

| Value | Meaning |
|---|---|
| `Available` | Stored status is `Available`. |
| `AwaitingRelease` | Stored status is `UnderMaintenance`, **and** no work order is IN_PROGRESS or REPORTED (a NEW or ASSIGNED order means maintenance is due, not started - 2026-10-27), **and** 0 active inspection flags, **and** the status reason is a known auto-registration sentinel. Registered but never released — nobody is working on it. |
| `UnderMaintenance` | Genuinely in maintenance: a work order is IN_PROGRESS or REPORTED (awaiting dispatcher acceptance), or an active inspection flag exists, or a human set the status with their own reason. |
| `Grounded` | Stored status is `Grounded`. Never softened by the absence of work orders. |

Why: the Spark ETL auto-registers an unknown tail number as `UnderMaintenance`, and `OperationalStatus` has no
value for "registered, not yet released", so every auto-registered aircraft displayed as if it were being worked
on. The rule (`AFP.Domain.Rules.FleetStatusRules`) is **presentation only** — it never changes a stored status,
never promotes an aircraft to `Available`, and never infers release eligibility from telemetry.
`POST /aircraft/{id}/status` remains the only, audited way to release an aircraft.

Data determinability (missing or stale telemetry) is reported by `health.isAssessable`, not by this field —
operational status is always known because it is stored.

### `health` block (added 2026-10-22, additive) — on `AircraftSummaryDto` and `AircraftDetailDto`

```jsonc
"health": {
  "latestReadingAtUtc": "2026-10-21T11:00:00Z", // most recent reading; the window ENDS here (since 2026-10-27)
  "engineTemperatureC": 791.8, "oilPressurePsi": 53.9, "vibrationIndex": 1.73,
  "windowFromUtc": "2026-10-21T11:23:00Z",      // start of the evaluation window
  "readingsInWindow": 6,
  "isAssessable": true,                          // false = no reading in the window
  "isHighRisk": true,                            // a recorded threshold was breached in the window
  "breaches": [{ "code": "EngineTemperatureHighC", "ruleReference": "BR-01",
                 "description": "...", "recommendedInspection": "..." }],
  "recommendedInspection": "Immediate thermal shroud and turbine inspection.",
  "thresholdsValidated": false,                  // thresholds await stakeholder approval
  "thresholdSource": "The Hub BRD v0.4 (21 October 2026) section 8",
  "failureRiskScore": null, "failureRiskTier": null,
  "isStale": false, "staleAfterHours": 48        // added 2026-10-27; null = unknown (no reading / no rule)
}
```

Every value is derived from stored telemetry and the seeded `health_threshold` reference data — never hard-coded.
Reading rules the UI must honour:

* `isHighRisk` is **threshold-based and descriptive**, not a model prediction, and never makes an aircraft dispatchable.
* `isAssessable: false` means *unknown*, not healthy. Render it as "not assessable", never as within limits.
* **The 24 h window ends at the aircraft's latest reading, not at "now"** (2026-10-27). Telemetry arrives as a daily
  batch at 00:00 IST, so a clock-anchored window shrank through the day and dropped breaches. How old the data is, is
  reported separately: `isStale` = latest reading older than the seeded `TelemetryStaleAfterHours` (48 h, unvalidated).
  Show stale data as stale; its last known breach stays visible.
* `failureRiskScore` is `null` unless a prediction exists **and** `probability_validated` is set; render "no valid
  assessment" rather than `0` or a tier. `failureRiskTier` follows BR-03 and is `null` whenever the score is.
* `thresholdsValidated: false` must be shown as a disclaimer: client-defined values recorded in the BRD, not
  manufacturer limits or regulatory guidance.

## Telemetry — `/api/v1/telemetry`

| Method | Path | Roles | Body → Response |
|---|---|---|---|
| GET | `/` `?aircraftId (required)&sensorId&fromUtc&toUtc&qualityStatus&page&pageSize(≤1000)` | ANY | `Paged<TelemetryRecordDto>` |
| GET | `/series` `?aircraftId&fromUtc&toUtc&sensorId&bucketMinutes(default 60)` | FD, DE | `{ aircraftId, fromUtc, toUtc, bucketMinutes, points: [{ timestampUtc, temperatureC\|null, pressureKpa\|null, count, pressurePsi\|null, vibrationIndex\|null }] }` (range ≤ 120 days). `pressurePsi` and `vibrationIndex` added 2026-10-23 (additive) for the Fleet Health graph. **`pressurePsi` is converted once here**, at the API boundary, via `TelemetryRowValidator.KpaToPsi` (`kPa × 0.1450377377`, the exact inverse of the ingest factor) — clients must not re-derive it. kPa remains the stored canonical unit; PSI is the unit the engine-health business rules are written in. A `null` means **no reading in that bucket** and must never be plotted as zero or interpolated across. |
| POST | `/imports` | DE | `multipart/form-data` field `file` (.csv, ≤ 200 MB) → **202** `JobAcceptedDto`. **Retained but no longer surfaced in the Data Engineer UI (2026-10-23)**: the upload screen is JSON-only. This endpoint, `TelemetryImportProcessor`, the documented CSV format below and their tests (`TelemetryImportProcessorTests`, `ValidationAndCsvTests`, `SyntheticImportCompatibilityTests`) are all still live, so the CSV path was **not** removed — it was inspected and found to be supported functionality, not dead code. Backend validation still rejects anything that is not `.csv` here, and `/raw-uploads` still rejects anything that is not `.json`. |
| POST | `/raw-uploads` | Dispatcher, DE | `multipart/form-data` field `file` (.json, ≤ 50 MB; JSON array; every record needs `telemetry_id`, `tail_number`, `timestamp_epoch`, `metrics`) → **202** `JobAcceptedDto` (`status: "Queued"`). The file is stored byte-for-byte in MinIO `bronze/telemetry/yyyy/MM/<importId>.json` for the Spark ETL; the import moves `Queued → Processing → Completed/Failed` as the ETL reports. 400 structural errors (`errors.file[]`), 409 identical file already imported, 503 `storage_unavailable` (MinIO refused/unreachable). |
| GET | `/imports/{id}/deletion-plan` | DE | `ImportDeletionPlanDto` — what a deletion would remove and what it keeps. Read-only. |
| DELETE | `/imports/{id}` | DE | `ImportDeletionResultDto`. Removes **only** resources provably owned by the import: its Bronze object (`stored_path`), its `telemetry_record` rows (`import_id`; the FK is `ON DELETE SET NULL`, so they are deleted explicitly rather than orphaned), its `telemetry_rejected_row` rows, its ETL `pipeline_run` rows (`parameters->>'importId'`), and its Silver/Gold partition **only when `lake_batch_id` proves no other import shares that Spark batch**. A null `lake_batch_id` means ownership is unknown and the lake data is **kept**. Aircraft, anomalies, predictions, maintenance records, users and audit history are never removed. 400 `import_in_progress` while Queued/Processing; 404 for an unknown id; 403 for any role but DataEngineer. **PostgreSQL and MinIO are not atomic**: the database half runs in one transaction, object removal is idempotent and follows, and `completed` is `false` with `remainingWork` populated if storage could not be fully cleaned — re-running resumes safely. Writes a `TelemetryImportDeleted` audit entry. |
| POST | `/imports/{id}/retry` | Dispatcher, DE | Failed **RawJson** imports only → `TelemetryImportDto` (`Queued`; object copied to a new key so the ETL re-reads it; inserts are idempotent) |
| GET | `/imports` `?page&pageSize` | DE | `Paged<TelemetryImportDto>`. Added 2026-10-22 (additive): `storedPath` — the MinIO Bronze object key the original payload was written to (null for older rows) — and `fileSizeBytes` — the uploaded size as received (`0` when unknown, e.g. imports created before this field existed). A **202 on upload is not a completed ETL run**: the import stays `Queued`/`Processing` until the ETL reports. |
| GET | `/imports/{id}` | DE, LD | `TelemetryImportDto` |
| GET | `/imports/{id}/rejected-rows` `?page&pageSize` | DE | `Paged<{ rowNumber, rawLine, reasons }>` |
| GET | `/data-quality` `?fromUtc&toUtc&aircraftId` | DE, LD | `{ totalRecords, validRecords, suspectRecords, missingTemperature, missingPressure, firstRecordUtc|null, lastRecordUtc|null, byAircraft: [{ aircraftId, registrationNumber, records, suspectRecords, firstRecordUtc, lastRecordUtc }], imports: { count, acceptedRows, rejectedRows, duplicateRows } }` |

### CSV format

Header row required (case-insensitive). Required columns: `aircraft_registration`, `timestamp_utc`,
`temperature`, `temperature_unit`, `pressure`, `pressure_unit`. Optional: `sensor_id`, `data_source`.

* `timestamp_utc` — ISO-8601 with `Z` or an explicit offset; naive timestamps and timestamps more than 5 minutes in the future are rejected.
* Units — temperature `C`, `F`, `K`; pressure `kPa`, `hPa`, `psi`, `bar`. A value without a valid unit is rejected.
* A row missing both measurements is rejected; a row missing one is accepted as `Suspect` (`MISSING_TEMPERATURE` / `MISSING_PRESSURE`).
* Physically impossible values (below absolute zero, negative absolute pressure, NaN/∞) are rejected.
* Unknown aircraft registrations are rejected. Duplicates (same aircraft + sensor + timestamp, in-file or already stored) are counted and rejected.
* The original file is preserved byte-for-byte in the import storage directory and its SHA-256 recorded.

## Sensor forecast — `GET /api/v1/aircraft/{id}/sensor-forecast?horizon=OneDay|SevenDays|ThirtyDays` (FD, DE)

Added 2026-10-28. Future hourly engine temperature / oil pressure / vibration for one aircraft and horizon:
`{ aircraftId, registrationNumber, horizon, status: "Available"|"NotAvailable", reason|null, supportedHorizons: [],
supportedMetrics: []|null, modelName|null, modelVersion|null, generatedAtUtc|null, dataAsOfUtc|null, points: [{ timestampUtc,
temperatureC|null, temperatureCLow|null, temperatureCHigh|null, pressurePsi..., vibrationIndex... }] }`.
Served ONLY from stored forecasts (`sensor_forecast`) of the **Active** `SensorForecast` model, for the horizons and
metrics its backtest validated (`metrics.supported_horizons`); a non-validated metric is always null, and points at or
before `dataAsOfUtc` are dropped. Otherwise `NotAvailable` with the reason: no Active forecaster / horizon not validated
/ nothing generated yet. Missing/unknown `horizon` -> 400; unknown aircraft -> 404; technician -> 403. Recorded
telemetry is never returned here (`GET /telemetry/series`). Approval of a SensorForecast model is refused
(`no_supported_horizon`) when no horizon passed validation.

Since 2026-10-28 (user decision, additive): a shorter horizon the model did not validate on its own is served as the
opening hours of the shortest validated LONGER forecast (e.g. 1 Day = the first 24 h of the 30-day forecast), with
`slicedFromHorizon: "ThirtyDays"` (null when the horizon is served from its own forecast). `supportedHorizons` lists
every servable horizon. Points are always limited to `dataAsOfUtc` + the horizon length (`AnomalyRules.HorizonHours`).

Latest prediction per aircraft (dashboards, fleet health, anomaly processing) since 2026-10-28: the aircraft's latest
**model** prediction is preferred over a newer descriptive indicator (model rows are de-duplicated per feature cutoff;
a second scoring run on the same UTC day used to hide the model assessment).

## Anomalies — `/api/v1/anomalies`

`AnomalyDto` gains (2026-10-27, additive) `source` (`IsolationForest` | `ThresholdRule` | `FailureModel` | `ForecastModel` (2026-10-28)), `ruleCodes`,
`evidence` (immutable detection evidence) and `lastObservedAtUtc`; `anomalyScore` and `modelVersionId` are **null** for a
`ThresholdRule` anomaly (a rule has no score). The dry run's `counts` adds `tooOld` (outside the age window).

| Method | Path | Roles | Body → Response |
|---|---|---|---|
| GET | `/` `?aircraftId&status&severity&fromUtc&toUtc&source&page&pageSize` | ANY | `Paged<AnomalyDto>`; `source` = `IsolationForest`\|`ThresholdRule`\|`FailureModel`\|`ForecastModel` (added 2026-10-27; `ForecastModel` 2026-10-28) |
| GET | `/summary` `?days=30` | ANY | `{ total, open, bySeverity: { Low, Medium, High }, byDay: [{ date, count }] }` |
| GET | `/{id}` | ANY | `AnomalyDto & { telemetry: TelemetryRecordDto|null }` |
| POST | `/{id}/acknowledge` | MT | `{ notes? }` → `AnomalyDto` (New → Acknowledged) |
| POST | `/{id}/investigation` | MT | `{ status: "Investigating"|"Dismissed"|"ConfirmedIssue", notes }` → `AnomalyDto` |

## Predictions — `/api/v1/predictions`

| Method | Path | Roles | Body → Response |
|---|---|---|---|
| GET | `/` `?aircraftId&riskCategory&basis&approvalStatus&page&pageSize` | ANY | `Paged<PredictionDto>` |
| GET | `/aircraft/{aircraftId}` | ANY | `PredictionDto[]` (latest first, max 20) |
| GET | `/metadata` | ANY | `{ configuredHorizonDays, failurePredictionStatus, activeAnomalyModel: ModelVersionDto|null, activeFailureModel: ModelVersionDto|null, riskCategories: [{ code, description, displayOrder }] }` |
| POST | `/{id}/review` | MT | `{ approvalStatus: "Approved"|"Rejected", notes }` → `PredictionDto` |
| POST | `/{id}/outcome` | MT | `{ actualOutcome, workOrderId? }` → `PredictionDto` |

## Maintenance — `/api/v1/work-orders`, `/api/v1/maintenance-logs`, `/api/v1/technicians`

**Phase 5 additions (2026-10-27, additive).** `WorkOrderDto` gains `source` (`Manual` | `Automatic`), the diagnosis
snapshot stored at creation and never regenerated — `diagnosisStatus` (`NotGenerated` | `Generated` | `Failed`),
`diagnosisText`, `diagnosisMethod` (`deterministic-rules`), `diagnosisRuleVersion`, `diagnosisGeneratedAtUtc`,
`diagnosisError` — and `dispatcherReviewNotes`. The detail adds `reports` (every append-only technician report, oldest
first: `sequence, technicianUserId, technicianName, submittedAtUtc, actionCode, inspectionFindings, correctiveAction,
resolutionNotes, actualFailureConfirmed`) and `diagnosisEvidence`. A diagnosis failure never loses the ticket
(`diagnosisStatus: Failed` + `diagnosisError`). `GET /maintenance-action-codes` (FD, MT) → `[{ code, description }]` from
the seeded `maintenance_action_code` table (ENG_INSP, OIL_REPL). A maintenance-log row is written **only** when the
dispatcher closes (accepts) the work order; it names the accepted report's action code.

**Automatic work orders** come from `AFP.Api process-anomalies --run-as <DataEngineer>` (nightly, after scoring;
recorded as a `pipeline_run` of job type `AnomalyProcessing` with its counts and failures). Sources: approved BR-01
breaches (one incident per aircraft per breach episode in its latest 24 h window; continuing breaches update the open
incident) and **validated** failure-model probabilities in the BR-03 Critical tier — provisional scores are never
eligible. Since 2026-10-27 a model incident also requires the model to be **Approved or Active** and the BR-03 tier rows
to be **approved** (`health_threshold.is_validated`), exactly as BR-01 incidents need approved limits; skipped scores are
counted (`modelAssessmentsSkippedProvisional`, `modelAssessmentsSkippedUnapproved`) and the reason is in `notes`. Isolation Forest anomalies qualify at High severity within `AnomalyWorkOrders:MaxAgeDays` (7). Idempotent:
unique `aircraft_anomaly_result.dedup_key` and one work order per anomaly (unique `maintenance_work_order.anomaly_id`).

**Since 2026-10-28 incidents are forecast-driven.** `process-anomalies` reads the **Active** `SensorForecast` model's
stored forecast (longest validated horizon: 30 days, then 7, then 1) and raises one `ForecastModel` anomaly per aircraft
whose forecast crosses an approved BR-01 limit after its data cut-off (dedup `ForecastModel:{aircraft}:{dataAsOf}`; a
newer forecast of a still-open incident updates it). `observedAtUtc`/`lastObservedAtUtc` = first/last forecast breach.
Anomalies and work orders come from the forecast ONLY (`AnomalyRules.WorkOrderSources = [ForecastModel]`): failure-model
incidents are retired (the `modelIncidentsCreated` / `modelAssessmentsSkipped*` counts are always 0), and the ML `score` job
no longer persists Isolation Forest flags unless `AFP_PERSIST_TELEMETRY_ANOMALIES=true`. Older non-forecast anomalies and
their work orders were deleted on 2026-10-28 with `tools/data/purge_telemetry_anomalies.py` (backup first). The work order starts **New** with a
plain-English diagnosis (no rule codes). Maintenance sync: an open forecast work order puts an Available aircraft
**UnderMaintenance** (reason `AnomalyRules.ForecastMaintenanceReason`, via `ChangeStatusAsync` - policy + audit); the
aircraft is released to Available only when all its forecast work orders are **Closed** (dispatcher-accepted). A status
set by a person is never changed. The run's counts add `forecastIncidentsCreated/Continuing/AlreadyProcessed`,
`placedInMaintenance`, `releasedToService`; the `threshold*` counts are retired (always 0).

**Since 2026-10-28 (user decision) anomalies are breach EPISODES and work orders are DAILY.** Per aircraft and limit,
consecutive forecast hours beyond the limit form one anomaly (`ruleCodes` = that one limit, `observedAtUtc` = first
breaching hour, `lastObservedAtUtc` = last); a return within the limit ends it and the next breach is a new anomaly.
Limits whose episodes START in the same hour are ONE anomaly (codes and breaches merged); the unique `dedup_key` is
`{tail}_{firstBreach:yyyyMMddHHmmss}_{forecasterId}` (`AnomalyProcessingService.DedupKey`). A newer forecast in which the same breach is still running extends the
open anomaly. Work orders: ONE New order per aircraft per projected UTC day (`maintenance_work_order.anomaly_id` = its
first anomaly; every anomaly of the day carries `aircraft_anomaly_result.work_order_id`, migration
`AnomalyWorkOrderLink`); later anomalies of a day with an open order are linked to it and its diagnosis is refreshed
while it is still New. `GET /work-orders?anomalyId=` also finds orders an anomaly is linked to. Work-order numbers
continue from the highest number ever issued that year (existing orders or creation audit), so deleted numbers are not
reused.

| Method | Path | Roles | Body → Response |
|---|---|---|---|
| GET | `/work-orders` `?aircraftId&status&priority&assignedToMe&source&anomalyId&page&pageSize` | MT, FD, LD | `Paged<WorkOrderDto>`; `anomalyId` returns the order raised for that anomaly (added 2026-10-27) |
| GET | `/work-orders/{id}` | MT, FD, LD | `WorkOrderDetailDto` |
| POST | `/work-orders` | MT, FD | `{ aircraftId, componentId?, anomalyId?, predictionId?, priority, title, description, dueAtUtc?, assignedTechnicianId? }` → 201 `WorkOrderDto` |
| PATCH | `/work-orders/{id}` | MT | `{ concurrencyStamp, title?, description?, priority?, dueAtUtc?, componentId? }` → `WorkOrderDto` (not allowed when Closed/Cancelled) |
| POST | `/work-orders/{id}/assign` | MT | `{ technicianId }` (Open/Assigned → Assigned) |
| POST | `/work-orders/{id}/start-inspection` | MT | `{}` (Assigned → InInspection) |
| POST | `/work-orders/{id}/inspection` | MT | `{ inspectionFindings }` (InInspection; records findings, updates aircraft `last_inspection_at_utc`) |
| POST | `/work-orders/{id}/resolve` | MT | `{ correctiveAction, actualFailureConfirmed: bool, resolutionNotes, actionCode }` (InInspection → Resolved; findings required; `resolutionNotes` ≥ 10 characters; **`actionCode` required since 2026-10-27** — an active code from `GET /maintenance-action-codes`, else 400). Every submission **appends** an immutable `work_order_report` row (sequence 1, 2, …; technician = the authenticated user) — a re-report never overwrites an earlier one. |
| POST | `/work-orders/{id}/reopen` | MT | `{ reason }` (Resolved → **Assigned**, since 2026-10-26: the dispatcher sends reported work back; the technician starts again and earlier reports are kept) |
| POST | `/work-orders/{id}/close` | MT | `{ resolutionNotes? }` (Resolved → Closed; the dispatcher's notes are stored in `dispatcher_review_notes` and **never** alter the technician's report; writes a `maintenance_log` outcome row and records the outcome on the linked prediction/anomaly) |
| POST | `/work-orders/{id}/cancel` | MT | `{ reason }` (Open/Assigned → Cancelled) |
| GET | `/maintenance-logs` `?aircraftId&eventType&fromUtc&toUtc&page&pageSize` | ANY | `Paged<MaintenanceLogDto>` |
| POST | `/maintenance-logs` | MT | `{ aircraftId, componentId?, eventType, occurredAtUtc, description, actualFailureConfirmed?, isVerified }` → 201 `MaintenanceLogDto` |
| GET | `/technicians` | FD | `[{ id: number, displayName, username }]` (active users holding MaintenanceTechnician) |

All state-changing transition endpoints return `WorkOrderDto`. Work-order status machine:

```
Open ──assign──▶ Assigned ──start-inspection──▶ InInspection ──resolve──▶ Resolved ──close──▶ Closed
  │                 │  ▲                                                   │
  └──cancel──▶ Cancelled ◀──cancel──┘  └────────────────reopen (reason)─────────────┘

UI labels: Open=NEW, Assigned=ASSIGNED, InInspection=IN_PROGRESS, Resolved=REPORTED, Closed=COMPLETED.
```

## Data engineering — `/api/v1/pipelines` (DE)

| Method | Path | Body → Response |
|---|---|---|
| GET | `/runs` `?jobType&status&page&pageSize` | `Paged<PipelineRunDto>` |
| GET | `/runs/{id}` | `PipelineRunDto` |
| POST | `/jobs` | `{ jobType: "DatasetAudit"|"TrainAnomaly"|"Score"|"TrainFailure", parameters?: { include_suspect?: bool (TrainAnomaly), full?: bool (Score) } }` — any other parameter is rejected with 400; the prediction horizon comes from configuration (`Prediction:HorizonDays` / `AFP_PREDICTION_HORIZON_DAYS`). → **202** `JobAcceptedDto`. Queued runs are executed by the Python worker (`python -m afp_ml worker`). 409 if a run of that type is already queued or running. |
| POST | `/runs/{id}/cancel` | Queued → Cancelled |
| GET | `/dataset-audit` | Latest successful `DatasetAudit` run (`PipelineRunDto`) or 404 |

## Models — `/api/v1/models`

| Method | Path | Roles | Body → Response |
|---|---|---|---|
| GET | `/` `?modelType&status` | DE, LD | `ModelVersionDto[]` |
| GET | `/{id}` | DE, LD | `ModelVersionDto` |
| GET | `/{id}/evaluation` | DE, LD | `{ modelVersionId, modelName, version, modelType, metrics, limitations|null, evaluationAvailable }` |
| POST | `/{id}/register` | DE | Trained → Registered |
| POST | `/{id}/approve` | LD | `{ notes }` Registered → Approved. Failure-prediction models are refused while readiness is Blocked or required metrics are missing. |
| POST | `/{id}/reject` | LD | `{ notes }` Registered/Approved → Rejected |
| POST | `/{id}/activate` | DE | Approved → Active (the previously active model of the same type becomes Retired) |
| POST | `/{id}/retire` | DE | Active → Retired |

## Development (synthetic) — `/api/v1/development` (DE)

The **isolated** development environment: the database named by `DEV_DB_NAME` (root `.env`), reached on its own
connection — never the operational one; `DEV_DB_NAME` equal to `DB_NAME`/`aircraft_predict_db` or a forbidden name
(`aircraft_app`) is refused at startup. Without `DEV_DB_NAME`, `GET /` returns `configured: false` and every other
endpoint returns **400 `development_not_configured`**. There is **no** register/approve/activate here: nothing can be
promoted to the operational registry. Jobs are executed by an ML worker started with the development config
(`AFP_ML_ENV_FILE=.env.synthetic python -m afp_ml worker`).

| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | `{ configured, database, notice, queueableJobs }` |
| GET | `/models` | `ModelVersionDto[]` from the development database |
| GET | `/models/{id}/evaluation` | as `/models/{id}/evaluation` |
| GET | `/runs`, `/runs/{id}` | `PipelineRunDto[]` (latest 50) / `PipelineRunDto` |
| POST | `/jobs` | `{ jobType: "DatasetAudit"|"TrainFailure"|"ScoreCandidate", modelVersionId? }` → **202** `JobAcceptedDto`. `ScoreCandidate` needs a failure-prediction `modelVersionId` (400/404 otherwise); any other job type 400; 409 `job_already_pending`. `requested_by_user_id` is NULL (the development DB has no users); the requester is in `parameters.requested_by_user_id` and a `DevelopmentJobQueued` entry is written to the **operational** audit log. |
| GET | `/predictions` | `PredictionDto[]`, latest model-basis prediction per aircraft in the development database (`probability` only when validated, as everywhere) |

## Reports — `/api/v1/reports`

All accept `?format=json` (default) or `?format=csv` (download, `text/csv`).

| GET | Roles | Content |
|---|---|---|
| `/fleet-health` | LD, FD, MT | per-aircraft status, risk, open anomalies/work orders + totals |
| `/maintenance-history` `?fromUtc&toUtc` | LD, MT | monthly scheduled/unscheduled/inspection counts, work orders opened/closed, mean resolution hours |
| `/data-quality` | LD, DE | data-quality report (same shape as telemetry data-quality) |
| `/model-performance` | LD, DE | model versions + stored evaluation metrics, anomaly investigation outcomes, prediction outcome counts |

## Audit — `/api/v1/audit`

| GET | Roles | Response |
|---|---|---|
| `/actions` `?entityType&entityId&userId&fromUtc&toUtc&page&pageSize` | ADM, LD | `Paged<ActionAuditDto>` |
| `/auth` `?email&eventType&fromUtc&toUtc&page&pageSize` | ADM | `Paged<AuthAuditDto>` |

## Health

* `GET /health` — anonymous; JSON with database check (`Healthy`/`Unhealthy`). The check also verifies that the connected
  database (`current_database()`) is the configured `DB_NAME` (`aircraft_predict_db`).
* `GET /health/live` — liveness only (no dependencies).
