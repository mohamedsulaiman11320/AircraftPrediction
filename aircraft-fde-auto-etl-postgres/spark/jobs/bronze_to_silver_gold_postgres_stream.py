"""Raw JSON (MinIO bronze/telemetry) -> PySpark -> aircraft_predict_db (local PostgreSQL) + silver/gold Parquet.

Discovery: Spark structured streaming over BRONZE_PATH with a MinIO checkpoint (every file is processed once;
restarts resume from the checkpoint). Each source file is loaded in ONE database transaction:
  * linked to its telemetry_import row (created by the API upload, or created here for objects placed directly
    in the bucket), status Processing -> Completed / Failed, with a pipeline_run (TelemetryImport) for history;
  * records validated (telemetry_rules.py); rejects stored in telemetry_rejected_row with the raw record;
  * unknown tail numbers auto-registered as placeholder aircraft (AUTO_REGISTER_AIRCRAFT), audited;
  * telemetry inserted with ON CONFLICT DO NOTHING (unique (data_source, source_record_id) and
    (aircraft, sensor, time)) -> re-processing or re-uploading never duplicates telemetry.
A failed file is marked Failed (raw object untouched) and can be retried from the application.
status_flag is stored as an operational flag; no failure label is derived from it.
"""
import hashlib
import json
import os
import socket
import time
import uuid
from datetime import datetime, timezone

import psycopg
from minio import Minio
from psycopg.types.json import Jsonb
from pyspark.sql import SparkSession
from pyspark.sql.functions import col, dayofweek, from_unixtime, hour, input_file_name, to_timestamp
from pyspark.sql.types import DoubleType, LongType, StringType, StructField, StructType

import telemetry_rules as rules


def env(name: str, default: str | None = None, required: bool = False) -> str:
    value = os.getenv(name, default)
    if required and not value:
        raise RuntimeError(f"{name} is not configured (see .env.example).")
    return value


BRONZE_PATH = env("BRONZE_PATH", "s3a://bronze/telemetry/")
SILVER_PATH = env("SILVER_PATH", "s3a://silver/telemetry_clean")
GOLD_PATH = env("GOLD_PATH", "s3a://gold/aircraft_features")

# Transient 403/503 refusals from this MinIO build abort otherwise-valid commits; each retry is a
# full overwrite of the same per-batch path, so retrying is idempotent.
LAKE_WRITE_ATTEMPTS = int(env("LAKE_WRITE_ATTEMPTS", "4"))
LAKE_WRITE_BACKOFF_SECONDS = float(env("LAKE_WRITE_BACKOFF_SECONDS", "3"))
CHECKPOINT_PATH = env("CHECKPOINT_PATH", "file:///opt/aircraft/checkpoints/bronze_to_app_postgres")
TRIGGER_INTERVAL = env("TRIGGER_INTERVAL", "10 seconds")

APP_DB_NAME = env("APP_DB_NAME", "aircraft_predict_db")
FORBIDDEN_DATABASES = {"aircraft_app"} | {n.strip() for n in env("APP_DB_FORBIDDEN_NAMES", "").split(",") if n.strip()}
DATA_SOURCE = env("APP_DATA_SOURCE", "RAW_JSON_BRONZE")
AUTO_REGISTER = env("AUTO_REGISTER_AIRCRAFT", "true").lower() == "true"
PLACEHOLDER = {
    "model": env("PLACEHOLDER_AIRCRAFT_MODEL", "Unknown"),
    "manufacturer": env("PLACEHOLDER_AIRCRAFT_MANUFACTURER", "Unknown"),
    "fleet_group": env("PLACEHOLDER_AIRCRAFT_FLEET_GROUP", "Unassigned"),
}
MAX_STORED_REJECTS = int(env("MAX_STORED_REJECTED_ROWS", "10000"))
INSERT_CHUNK = 5000

metrics_schema = StructType([
    StructField("engine_temp_c", DoubleType(), True),
    StructField("oil_pressure_psi", DoubleType(), True),
    StructField("vibration_index", DoubleType(), True),
])
telemetry_schema = StructType([
    StructField("telemetry_id", StringType(), True),
    StructField("tail_number", StringType(), True),
    StructField("timestamp_epoch", LongType(), True),
    StructField("metrics", metrics_schema, True),
    StructField("status_flag", StringType(), True),
])


def connect() -> psycopg.Connection:
    conn = psycopg.connect(
        host=env("APP_DB_HOST", required=True), port=int(env("APP_DB_PORT", "5432")), dbname=APP_DB_NAME,
        user=env("APP_DB_USER", required=True), password=env("APP_DB_PASSWORD", required=True), connect_timeout=10,
    )
    actual = conn.execute("SELECT current_database()").fetchone()[0]
    rules.assert_safe_database(actual, APP_DB_NAME, FORBIDDEN_DATABASES)
    return conn


def minio_client() -> Minio:
    endpoint = env("MINIO_ENDPOINT", "http://minio:9000")
    return Minio(endpoint.split("://", 1)[-1], access_key=env("MINIO_ROOT_USER"), secret_key=env("MINIO_ROOT_PASSWORD"),
                 secure=endpoint.startswith("https"))


def object_sha256(location: str) -> str:
    bucket, key = location[len("s3://"):].split("/", 1)
    response = minio_client().get_object(bucket, key)
    try:
        digest = hashlib.sha256()
        for chunk in response.stream(1024 * 1024):
            digest.update(chunk)
        return digest.hexdigest()
    finally:
        response.close()
        response.release_conn()


def claim_import(cur, location: str, now: datetime) -> tuple[str, bool, str]:
    """Returns (import id, api_uploaded, current status) for the object, creating a tracking row if needed."""
    row = cur.execute("SELECT id, requested_by_user_id, status FROM telemetry_import WHERE stored_path = %s FOR UPDATE",
                      (location,)).fetchone()
    if row:
        return str(row[0]), row[1] is not None, row[2]
    sha = object_sha256(location)
    existing = cur.execute("SELECT id FROM telemetry_import WHERE file_sha256 = %s", (sha,)).fetchone()
    if existing:
        # Identical bytes were already imported under another object name: record nothing new.
        return str(existing[0]), False, "DuplicateFile"
    import_id = str(uuid.uuid4())
    cur.execute(
        """INSERT INTO telemetry_import (id, file_name, file_sha256, stored_path, status, source, total_rows, accepted_rows,
                                         rejected_rows, duplicate_rows, warning_count, warnings, requested_by_user_id,
                                         requested_at_utc)
           VALUES (%s, %s, %s, %s, 'Queued', 'RawJson', 0, 0, 0, 0, 0, '[]'::jsonb, NULL, %s)""",
        (import_id, location.rsplit("/", 1)[-1][:255], sha, location, now))
    return import_id, False, "Queued"


def resolve_aircraft(cur, registrations: set[str], now: datetime, import_id: str) -> tuple[dict[str, str], list[str]]:
    known = dict(cur.execute("SELECT upper(registration_number), id::text FROM aircraft WHERE upper(registration_number) = ANY(%s)",
                             (list(registrations),)).fetchall())
    created = []
    for registration in sorted(registrations - known.keys()) if AUTO_REGISTER else []:
        aircraft_id = str(uuid.uuid4())
        cur.execute(
            """INSERT INTO aircraft (id, registration_number, model, manufacturer, fleet_group, operational_status, status_reason,
                                     status_changed_at_utc, created_at_utc, updated_at_utc)
               VALUES (%s, %s, %s, %s, %s, 'UnderMaintenance', %s, %s, %s, %s) ON CONFLICT (registration_number) DO NOTHING""",
            (aircraft_id, registration, PLACEHOLDER["model"], PLACEHOLDER["manufacturer"], PLACEHOLDER["fleet_group"],
             "Auto-registered by the telemetry ETL; complete model/manufacturer and release via the application.", now, now, now))
        if cur.rowcount == 1:
            known[registration] = aircraft_id
            created.append(registration)
            cur.execute(
                """INSERT INTO action_audit (user_id, action, entity_type, entity_id, details, occurred_at_utc)
                   VALUES (NULL, 'AircraftAutoRegistered', 'Aircraft', %s, %s, %s)""",
                (aircraft_id, Jsonb({"registration": registration, "importId": import_id, "placeholder": PLACEHOLDER}), now))
    return known, created


def insert_rejects(cur, import_id: str, rejects: list[tuple[int, str, str]]) -> None:
    for start in range(0, len(rejects), INSERT_CHUNK):
        chunk = rejects[start:start + INSERT_CHUNK]
        cur.execute(
            """INSERT INTO telemetry_rejected_row (import_id, row_number, raw_line, reasons)
               SELECT %s, n, r, reason FROM unnest(%s::int[], %s::text[], %s::text[]) AS t(n, r, reason)""",
            (import_id, [c[0] for c in chunk], [c[1][:2000] for c in chunk], [c[2][:1000] for c in chunk]))


def insert_telemetry(cur, import_id: str, records: list[rules.ValidRecord], aircraft: dict[str, str], now: datetime) -> int:
    inserted = 0
    for start in range(0, len(records), INSERT_CHUNK):
        chunk = records[start:start + INSERT_CHUNK]
        cur.execute(
            """INSERT INTO telemetry_record (aircraft_id, sensor_id, recorded_at_utc, temperature_value, temperature_unit, temperature_c,
                                             pressure_value, pressure_unit, pressure_kpa, data_source, quality_status, quality_flags,
                                             ingested_at_utc, import_id, source_row_number, source_record_id, vibration_index,
                                             source_status_flag)
               SELECT a::uuid, NULL, ts, t, CASE WHEN t IS NULL THEN NULL ELSE 'C' END, t, p, CASE WHEN p IS NULL THEN NULL ELSE 'psi' END,
                      pk, %s, q, f, %s, %s, n, rid, v, sf
               FROM unnest(%s::text[], %s::timestamptz[], %s::float8[], %s::float8[], %s::float8[], %s::text[], %s::text[],
                           %s::int[], %s::text[], %s::float8[], %s::text[]) AS x(a, ts, t, p, pk, q, f, n, rid, v, sf)
               ON CONFLICT DO NOTHING""",
            (DATA_SOURCE, now, import_id,
             [aircraft[r.registration] for r in chunk], [r.recorded_at_utc for r in chunk], [r.temperature_c for r in chunk],
             [r.pressure_psi for r in chunk], [r.pressure_kpa for r in chunk], [r.quality_status for r in chunk],
             [",".join(r.quality_flags) or None for r in chunk], [r.row_number for r in chunk],
             [r.telemetry_id for r in chunk], [r.vibration_index for r in chunk], [r.status_flag for r in chunk]))
        inserted += cur.rowcount
    return inserted


def process_file(location: str, rows: list, batch_id: int | None = None) -> None:
    now = datetime.now(timezone.utc)
    with connect() as conn:
        with conn.cursor() as cur:
            import_id, api_uploaded, status = claim_import(cur, location, now)
            if status in ("Completed", "DuplicateFile"):
                print(f"Skipping {location}: import {import_id} already {status}.")
                conn.commit()
                return
            run_id = str(uuid.uuid4())
            cur.execute("UPDATE telemetry_import SET status = 'Processing', started_at_utc = %s, error_message = NULL WHERE id = %s",
                        (now, import_id))
            cur.execute(
                """INSERT INTO pipeline_run (id, job_type, status, trigger, requested_at_utc, started_at_utc, parameters, worker_host)
                   VALUES (%s, 'TelemetryImport', 'Running', %s, %s, %s, %s, %s)""",
                (run_id, "Api" if api_uploaded else "Cli", now, now, Jsonb({"importId": import_id, "location": location}), socket.gethostname()))
        conn.commit()

    try:
        with connect() as conn, conn.transaction(), conn.cursor() as cur:
            cur.execute("DELETE FROM telemetry_rejected_row WHERE import_id = %s", (import_id,))
            valid, rejects = [], []
            for number, row in enumerate(rows, start=1):
                record = row.asDict(recursive=True)
                raw = json.dumps({k: v for k, v in record.items() if k != "source_file"}, default=str)
                ok, reasons = rules.validate(record, now)
                if ok:
                    ok.row_number = number
                    valid.append(ok)
                else:
                    rejects.append((number, raw, "; ".join(reasons)))
            unique, in_file_dups = rules.deduplicate(valid)
            rejects += [(r.row_number, json.dumps({"telemetry_id": r.telemetry_id}), "duplicate telemetry_id in this file")
                        for r in in_file_dups]

            aircraft, created = resolve_aircraft(cur, {r.registration for r in unique}, now, import_id)
            loadable = [r for r in unique if r.registration in aircraft]
            unknown = sorted({r.registration for r in unique} - aircraft.keys())
            rejects += [(r.row_number, json.dumps({"telemetry_id": r.telemetry_id, "tail_number": r.registration}),
                         f"unknown aircraft registration '{r.registration}'") for r in unique if r.registration not in aircraft]

            inserted = insert_telemetry(cur, import_id, loadable, aircraft, now)
            already_stored = len(loadable) - inserted
            insert_rejects(cur, import_id, sorted(rejects)[:MAX_STORED_REJECTS])

            latest = {}
            for r in loadable:
                latest[r.registration] = max(latest.get(r.registration, r.recorded_at_utc), r.recorded_at_utc)
            for registration, ts in latest.items():
                cur.execute("""UPDATE aircraft SET last_telemetry_at_utc = GREATEST(COALESCE(last_telemetry_at_utc, %s), %s), updated_at_utc = %s
                               WHERE id = %s""", (ts, ts, now, aircraft[registration]))

            warnings = []
            suspect = sum(1 for r in loadable if r.quality_flags)
            if suspect:
                warnings.append(f"{suspect} accepted record(s) have a missing measurement (quality: Suspect).")
            if created:
                warnings.append(f"Auto-registered placeholder aircraft: {', '.join(created)}. Complete their details in the application.")
            if unknown:
                warnings.append(f"Records for unregistered aircraft were rejected: {', '.join(unknown[:20])}.")
            if in_file_dups:
                warnings.append(f"{len(in_file_dups)} record(s) repeated within the file were rejected as duplicates.")
            if already_stored:
                warnings.append(f"{already_stored} record(s) were already stored and were skipped.")
            if len(rejects) > MAX_STORED_REJECTS:
                warnings.append(f"Only the first {MAX_STORED_REJECTS} rejected records are stored for review.")
            # The counters must reconcile: total = accepted + rejected + duplicate.
            # `rejects` already contains the records repeated within the file (rejection reason
            # "duplicate telemetry_id in this file"), so they must not be added again here.
            # `already_stored` is the only bucket that is skipped rather than rejected.
            summary = {"total_rows": len(rows), "accepted_rows": inserted, "rejected_rows": len(rejects),
                       "duplicate_rows": already_stored, "in_file_duplicate_rows": len(in_file_dups),
                       "auto_registered_aircraft": created,
                       "data_source": DATA_SOURCE, "warnings": warnings}
            cur.execute(
                # lake_batch_id records which micro-batch wrote this import's Silver/Gold partition, so a later
            # per-import deletion can tell an exclusively owned partition from one shared with another import.
            """UPDATE telemetry_import SET status = 'Completed', total_rows = %s, accepted_rows = %s, rejected_rows = %s,
                          duplicate_rows = %s, warning_count = %s, warnings = %s, completed_at_utc = %s,
                          lake_batch_id = %s WHERE id = %s""",
                (len(rows), inserted, summary["rejected_rows"], summary["duplicate_rows"], len(warnings), Jsonb(warnings),
                 datetime.now(timezone.utc), batch_id, import_id))
            cur.execute("""UPDATE pipeline_run SET status = 'Succeeded', completed_at_utc = %s, records_processed = %s, result_summary = %s
                           WHERE id = %s""", (datetime.now(timezone.utc), len(rows), Jsonb(summary), run_id))
        print(f"Loaded {location}: {inserted} inserted, {len(rejects)} rejected, {already_stored} already stored (import {import_id}).")
    except Exception as exc:  # the transaction above is rolled back; record the failure separately
        message = f"{type(exc).__name__}: {exc}"[:1000]
        print(f"FAILED {location}: {message}")
        with connect() as conn, conn.transaction(), conn.cursor() as cur:
            cur.execute("UPDATE telemetry_import SET status = 'Failed', error_message = %s, completed_at_utc = %s WHERE id = %s",
                        (message, datetime.now(timezone.utc), import_id))
            cur.execute("UPDATE pipeline_run SET status = 'Failed', error_message = %s, completed_at_utc = %s WHERE id = %s",
                        (message, datetime.now(timezone.utc), run_id))


def write_lake_layers(batch_df, batch_id: int) -> None:
    """Silver (parsed) and gold (features) Parquet per micro-batch; no failure label is derived."""
    silver = (batch_df.select("telemetry_id", "tail_number", "timestamp_epoch",
                              col("metrics.engine_temp_c").alias("engine_temp_c"),
                              col("metrics.oil_pressure_psi").alias("oil_pressure_psi"),
                              col("metrics.vibration_index").alias("vibration_index"), "status_flag", "source_file")
              .filter(col("telemetry_id").isNotNull() & col("tail_number").isNotNull() & col("timestamp_epoch").isNotNull())
              .dropDuplicates(["telemetry_id"])
              .withColumn("event_time", to_timestamp(from_unixtime(col("timestamp_epoch")))))
    gold = (silver.withColumn("event_hour", hour(col("event_time")))
                  .withColumn("day_of_week", dayofweek(col("event_time"))))
    # `overwrite` on a per-batch path keeps the write idempotent: a retry replaces that batch's own
    # output and never appends a second copy or touches another batch's partition.
    _write_with_retry(silver, f"{SILVER_PATH}/batch_id={batch_id}", "silver")
    _write_with_retry(gold, f"{GOLD_PATH}/batch_id={batch_id}", "gold")


def _write_with_retry(df, path: str, layer: str, attempts: int = LAKE_WRITE_ATTEMPTS) -> None:
    """
    Commit one lake layer, retrying transient storage refusals.

    This MinIO build intermittently answers 403/503 to individual object writes under load, which
    aborts an otherwise valid commit. Each attempt is a full overwrite of this batch's own path, so
    retrying cannot duplicate or partially merge data. The final failure is raised to the caller.
    """
    for attempt in range(1, attempts + 1):
        try:
            df.write.mode("overwrite").parquet(path)
            if attempt > 1:
                print(f"{layer} commit succeeded on attempt {attempt}: {path}")
            return
        except Exception as exc:  # noqa: BLE001 - retried below; re-raised on the last attempt
            if attempt == attempts:
                raise
            print(f"WARNING: {layer} commit attempt {attempt}/{attempts} failed "
                  f"({type(exc).__name__}: {str(exc)[:200]}); retrying in {LAKE_WRITE_BACKOFF_SECONDS * attempt}s")
            time.sleep(LAKE_WRITE_BACKOFF_SECONDS * attempt)


def process_batch(batch_df, batch_id: int) -> None:
    print(f"Processing micro-batch {batch_id}")
    files = [r.source_file for r in batch_df.select("source_file").distinct().collect()]
    if not files:
        print("No new files.")
        return
    for source_file in files:
        rows = batch_df.filter(col("source_file") == source_file).collect()
        process_file(rules.s3a_to_location(source_file), rows, batch_id)
    # The database load above is the system of record; silver/gold Parquet is a derived copy and must not block it.
    try:
        write_lake_layers(batch_df, batch_id)
    except Exception as exc:  # noqa: BLE001 - logged, retried on the next batch's overwrite
        print(f"WARNING: silver/gold Parquet write failed for micro-batch {batch_id}: {type(exc).__name__}: {str(exc)[:300]}")


def main() -> None:
    with connect() as conn:  # fail fast on wrong target database or credentials
        print(f"Destination verified: {conn.info.user}@{conn.info.host}:{conn.info.port}/{conn.info.dbname}")
    spark = SparkSession.builder.appName("Aircraft-Bronze-To-App-Postgres-ETL").getOrCreate()
    spark.sparkContext.setLogLevel("WARN")
    stream = (spark.readStream.schema(telemetry_schema)
              .option("multiLine", "true").option("recursiveFileLookup", "true").option("maxFilesPerTrigger", 50)
              .json(BRONZE_PATH)
              .withColumn("source_file", input_file_name()))
    query = (stream.writeStream.foreachBatch(process_batch).option("checkpointLocation", CHECKPOINT_PATH)
             .trigger(processingTime=TRIGGER_INTERVAL).start())
    print(f"Watching {BRONZE_PATH} (checkpoint {CHECKPOINT_PATH})")
    query.awaitTermination()


if __name__ == "__main__":
    main()
