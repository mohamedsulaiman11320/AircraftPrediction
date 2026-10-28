# Aircraft FDE ETL — raw JSON → MinIO → PySpark → local PostgreSQL

```
Data Engineer (app)  ──POST /api/v1/telemetry/raw-uploads──▶  API  ──▶  MinIO bronze/telemetry/yyyy/MM/<importId>.json
                                                                                     │  (Spark structured streaming, 10 s trigger,
                                                                                     ▼   checkpoint on the etl_checkpoints volume)
                       aircraft_predict_db (Windows PostgreSQL 17)  ◀── PySpark job ──┤
                       telemetry_record / telemetry_import /                         └──▶ silver/telemetry_clean, gold/aircraft_features (Parquet)
                       telemetry_rejected_row / pipeline_run
```

The **local PostgreSQL database `aircraft_predict_db` is the system of record**. Docker runs only MinIO and Spark;
there is no PostgreSQL container (the former `aircraft_fde` container was backed up and removed on 2026-10-21).

## Run

```powershell
cd C:\project\AirCraft\AircraftFailurePrediction\aircraft-fde-auto-etl-postgres
Copy-Item .env.example .env       # first time only; fill MINIO_* and APP_DB_PASSWORD (= INGEST_PASSWORD in the root .env)
docker compose up -d --build
docker compose logs -f spark      # "Destination verified: afp_predict_ingest@host.docker.internal:5432/aircraft_predict_db"
```

MinIO console (administration only): http://localhost:9001/console/browser/bronze/telemetry

## Uploading data

* **Normal path:** sign in as a Data Engineer (or Flight Dispatcher) → *Imports* → *Raw JSON telemetry*. The API
  validates the file (`.json`, ≤ 50 MB, JSON array, required keys per record), stores it under a new import-specific
  key (never overwriting), creates a `telemetry_import` row (`Queued`) and returns 202. Status then moves to
  `Processing` and `Completed`/`Failed` as reported by this ETL.
* **Manual path:** objects placed directly in `bronze/telemetry/` (console, `mc`) are also processed; the ETL creates
  the `telemetry_import` row itself (requested by "ETL").

Raw format (one JSON array):

```json
[{"telemetry_id": "TEL-100001", "tail_number": "N120UA", "timestamp_epoch": 1788220800,
  "metrics": {"engine_temp_c": 476.8, "oil_pressure_psi": 25.5, "vibration_index": 0.58}, "status_flag": "NORMAL"}]
```

## Mapping into `telemetry_record`

| Raw | Column | Notes |
|---|---|---|
| `telemetry_id` | `source_record_id` | unique per `data_source` (`RAW_JSON_BRONZE`) → idempotent |
| `tail_number` | `aircraft_id` | via `aircraft.registration_number`; unknown tails auto-registered as placeholders (model/manufacturer `Unknown`, status `UnderMaintenance`, audited) |
| `timestamp_epoch` | `recorded_at_utc` | Unix seconds, UTC (event time; `ingested_at_utc` is processing time) |
| `metrics.engine_temp_c` | `temperature_value`, `temperature_c` (unit `C`) | |
| `metrics.oil_pressure_psi` | `pressure_value` (unit `psi`), `pressure_kpa` | × 6.894757 |
| `metrics.vibration_index` | `vibration_index` | |
| `status_flag` | `source_status_flag` | threshold-based operational flag — **never** used as a failure label |

The former Spark `failure_label` (= `status_flag != NORMAL`) is no longer produced: it is not a confirmed failure.

Validation (same rules as the API's CSV import, physical plausibility only): required id/tail/time, tail format,
time not in the future (> 5 min) or before 1990, numeric/finite metrics, temperature ≥ −273.15 °C, pressure ≥ 0,
at least one of temperature/pressure. One missing measurement → `Suspect` with `MISSING_*` flags. Rejected records
are stored with their raw JSON and reasons in `telemetry_rejected_row`.

## Idempotency, failures and retries

* Each file is loaded in **one database transaction**; telemetry is inserted with `ON CONFLICT DO NOTHING`
  (unique `(data_source, source_record_id)` and `(aircraft_id, sensor_id, recorded_at_utc)`), so re-processing,
  restarts or re-uploads never duplicate telemetry. An import already `Completed` is skipped.
* The streaming checkpoint (Docker volume `etl_checkpoints`) records processed files; unprocessed files are picked
  up after a restart.
* A failing file is marked `Failed` with the error; the raw object is untouched. *Retry* in the app
  (`POST /api/v1/telemetry/imports/{id}/retry`) copies it to a new key that the stream picks up.
* Silver/gold Parquet is a derived copy written after the database load; a Parquet write failure is logged and does
  not affect the database.

## Database access

The ETL connects as **`afp_predict_ingest`** (least privilege, see `tools/db/grant-privileges.sql`): insert telemetry
and rejected rows, update import status/run bookkeeping, insert placeholder aircraft and their audit entry, update
`aircraft.last_telemetry_at_utc`. It cannot read users, change aircraft details/status, or write predictions/models.
It refuses to run against `aircraft_app` and verifies `current_database()` on every connection.

From Docker Desktop, `host.docker.internal:5432` reaches the Windows PostgreSQL and is seen as `127.0.0.1`, so the
default localhost-only `pg_hba.conf` is sufficient; PostgreSQL is not exposed to other networks.

## Tests

```powershell
..\ml\.venv\Scripts\python -m pytest spark\tests -q -p no:cacheprovider
```

## Known issue: MinIO data on a Windows bind mount

`./minio/data` is a Windows folder mounted into the MinIO container. Under Docker Desktop, renames/copies on that
mount intermittently fail with *Access Denied* (`PrefixAccessDenied` in the MinIO log), typically while another
Windows process (antivirus, indexer) has a file open. Mitigations in place: the Spark checkpoint lives on a Docker
volume and Parquet writes cannot block the database load. If uploads or object copies fail, `docker restart
aircraft-fde-postgres-minio-1` clears it; consider excluding `minio\data` from real-time antivirus scanning and
Windows Search indexing, or moving MinIO to a Docker named volume (requires migrating the existing objects).

## Removed (2026-10-21)

* `postgres` service, `postgres_data` volume, `postgres/init/001_init.sql` (`etl_run_audit`, never written) and
  `spark/jobs/show_postgres_data.py`. Backup of the old `aircraft_fde` database (26,607 rows in `aircraft_features`,
  3× duplicates of 8,869 records): `%LOCALAPPDATA%\AircraftFailurePrediction\backups\aircraft_fde_docker_20261021_165819.dump`
  (SHA-256 `FD5C3F18C9B5CCE76603BD48201B98BF3353023AD5CC14A25103EC60DBA97E32`, test-restored successfully).
  Restore: `pg_restore -h localhost -U postgres -d <new empty db> --no-owner <dump>`.
