"""
Maintenance-log ETL: MinIO Bronze CSV -> Silver (cleansed) -> Gold (business-ready) -> PostgreSQL.

Reads maintenance CSVs from `s3a://bronze/maintenance/`, validates and de-duplicates them, writes the
cleansed and business-ready layers as Parquet, and upserts the result into `maintenance_log`.

Labelling contract (this is what makes an event usable as a 30-day failure label):
  * action codes listed in ML_FAILURE_CODES  -> event_type 'Unscheduled', is_verified = true,
                                                actual_failure_confirmed = true
  * codes in MAINT_INSPECTION_CODES          -> event_type 'Inspection'
  * codes in MAINT_EXCLUDED_CODES            -> not loaded (e.g. cancelled work was never performed)
  * everything else                          -> event_type 'Scheduled'
Only the first group can ever become a positive label; nothing else is inferred as a failure.

Idempotent: a row's identity is (aircraft, occurred_at, source log id), so re-running the job over the
same Bronze object updates rather than duplicates.

Run: run-spark-job /opt/aircraft/jobs/bronze_maintenance_to_postgres.py
"""
from __future__ import annotations

import datetime as dt
import os
import sys
import uuid

import psycopg
from psycopg.types.json import Jsonb
from pyspark.sql import SparkSession
from pyspark.sql.functions import col, input_file_name, lit, lower, trim, upper

import telemetry_rules as rules


def env(name: str, default: str | None = None, required: bool = False) -> str:
    value = os.environ.get(name, default)
    if required and not value:
        raise SystemExit(f"{name} is required")
    return value or ""


BRONZE_PATH = env("MAINTENANCE_BRONZE_PATH", "s3a://bronze/maintenance/")
SILVER_PATH = env("MAINTENANCE_SILVER_PATH", "s3a://silver/maintenance_clean")
GOLD_PATH = env("MAINTENANCE_GOLD_PATH", "s3a://gold/maintenance_events")

APP_DB_NAME = env("APP_DB_NAME", "aircraft_predict_db")
FORBIDDEN_DATABASES = {"aircraft_app"} | {n.strip() for n in env("APP_DB_FORBIDDEN_NAMES", "").split(",") if n.strip()}


def _codes(name: str, default: str) -> set[str]:
    raw = env(name, default)
    return {c.strip().upper() for c in raw.split(",") if c.strip()}


FAILURE_CODES = _codes("ML_FAILURE_CODES", "ENG_UNSCHED_REMOVAL")
INSPECTION_CODES = _codes("MAINT_INSPECTION_CODES", "ENG_INSP")
EXCLUDED_CODES = _codes("MAINT_EXCLUDED_CODES", "CANCELLED")

LAKE_WRITE_ATTEMPTS = int(env("LAKE_WRITE_ATTEMPTS", "4"))
LAKE_WRITE_BACKOFF_SECONDS = float(env("LAKE_WRITE_BACKOFF_SECONDS", "3"))


def connect() -> psycopg.Connection:
    conn = psycopg.connect(
        host=env("APP_DB_HOST", required=True), port=int(env("APP_DB_PORT", "5432")), dbname=APP_DB_NAME,
        user=env("APP_DB_USER", required=True), password=env("APP_DB_PASSWORD", required=True), connect_timeout=10,
    )
    with conn.cursor() as cur:
        cur.execute("select current_database()")
        actual = cur.fetchone()[0]
    rules.assert_safe_database(actual, APP_DB_NAME, FORBIDDEN_DATABASES)
    return conn


def _write_with_retry(df, path: str, layer: str) -> None:
    """Commit one lake layer; retries ride out this MinIO build's intermittent 403/503 refusals."""
    import time
    for attempt in range(1, LAKE_WRITE_ATTEMPTS + 1):
        try:
            df.write.mode("overwrite").parquet(path)
            return
        except Exception as exc:  # noqa: BLE001
            if attempt == LAKE_WRITE_ATTEMPTS:
                raise
            print(f"WARNING: {layer} commit attempt {attempt} failed ({type(exc).__name__}); retrying")
            time.sleep(LAKE_WRITE_BACKOFF_SECONDS * attempt)


def classify(action_code: str) -> tuple[str, bool, bool] | None:
    """(event_type, is_verified, actual_failure_confirmed) for an action code, or None to exclude."""
    code = (action_code or "").strip().upper()
    if not code or code in EXCLUDED_CODES:
        return None
    if code in FAILURE_CODES:
        return ("Unscheduled", True, True)
    if code in INSPECTION_CODES:
        return ("Inspection", True, False)
    return ("Scheduled", True, False)


def main() -> int:
    spark = SparkSession.builder.appName("Aircraft-Bronze-Maintenance-To-Postgres").getOrCreate()
    run_id = str(uuid.uuid4())
    started = dt.datetime.now(dt.timezone.utc)

    raw = (spark.read.option("header", True).option("mode", "PERMISSIVE").csv(BRONZE_PATH)
           .withColumn("source_file", input_file_name()))
    total = raw.count()
    if total == 0:
        print(f"No maintenance CSV objects under {BRONZE_PATH}.")
        spark.stop()
        return 0

    # ---- Silver: cleanse, normalise, de-duplicate -------------------------------------------------
    silver = (raw
              .select(trim(col("log_id")).alias("log_id"),
                      upper(trim(col("aircraft_sn"))).alias("registration_number"),
                      trim(col("event_date")).alias("event_date"),
                      trim(col("technician_id")).alias("technician_id"),
                      upper(trim(col("action_code"))).alias("action_code"),
                      trim(col("notes")).alias("notes"),
                      col("source_file"))
              .filter(col("log_id").isNotNull() & col("registration_number").isNotNull()
                      & col("event_date").isNotNull() & col("action_code").isNotNull())
              .dropDuplicates(["log_id"]))
    cleansed = silver.count()
    _write_with_retry(silver, SILVER_PATH, "silver")

    # ---- Gold: apply the approved labelling contract ----------------------------------------------
    gold = silver.filter(~upper(col("action_code")).isin(list(EXCLUDED_CODES))) if EXCLUDED_CODES else silver
    gold = gold.withColumn("ingested_run_id", lit(run_id))
    business_ready = gold.count()
    _write_with_retry(gold, GOLD_PATH, "gold")

    rows = gold.collect()

    # ---- PostgreSQL ------------------------------------------------------------------------------
    # Excluded codes are filtered out when Gold is built, so the difference between the two layers is
    # the excluded count; `classify` only ever excludes rows that filter already removed.
    excluded = cleansed - business_ready
    inserted = updated = skipped_unknown_aircraft = 0
    unknown: set[str] = set()
    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute("select upper(registration_number), id from aircraft")
            fleet = {r[0]: r[1] for r in cur.fetchall()}

            cur.execute(
                """INSERT INTO pipeline_run (id, status_id, job_type, status, trigger, requested_at_utc,
                                             started_at_utc, parameters, worker_host)
                   VALUES (%s, 1, 'TelemetryImport', 'Running', 'Cli', %s, %s, %s, %s)""",
                (run_id, started, started, Jsonb({"source": BRONZE_PATH, "kind": "maintenance"}),
                 os.uname().nodename if hasattr(os, "uname") else "spark"))

            for row in rows:
                mapped = classify(row["action_code"])
                if mapped is None:
                    continue
                event_type, verified, confirmed = mapped
                aircraft_id = fleet.get(row["registration_number"])
                if aircraft_id is None:
                    unknown.add(row["registration_number"])
                    skipped_unknown_aircraft += 1
                    continue

                occurred = dt.datetime.fromisoformat(row["event_date"]).replace(tzinfo=dt.timezone.utc)
                description = f"[{row['log_id']}] {row['action_code']}"
                if row["technician_id"]:
                    description += f" by {row['technician_id']}"
                if row["notes"]:
                    description += f": {row['notes']}"

                # Source log id is carried in the description, making the upsert key stable.
                cur.execute(
                    """SELECT id FROM maintenance_log
                       WHERE aircraft_id = %s AND occurred_at_utc = %s AND description LIKE %s""",
                    (aircraft_id, occurred, f"[{row['log_id']}]%"))
                existing = cur.fetchone()
                if existing:
                    cur.execute(
                        """UPDATE maintenance_log SET event_type = %s, description = %s, is_verified = %s,
                                  actual_failure_confirmed = %s WHERE id = %s""",
                        (event_type, description[:4000], verified, confirmed, existing[0]))
                    updated += 1
                else:
                    cur.execute(
                        """INSERT INTO maintenance_log (id, status_id, aircraft_id, event_type, occurred_at_utc,
                                                        description, actual_failure_confirmed, is_verified,
                                                        source, created_at_utc)
                           VALUES (%s, 1, %s, %s, %s, %s, %s, %s, 'Import', %s)""",
                        (str(uuid.uuid4()), aircraft_id, event_type, occurred, description[:4000],
                         confirmed, verified, dt.datetime.now(dt.timezone.utc)))
                    inserted += 1

            summary = {"total_rows": total, "cleansed_rows": cleansed, "business_ready_rows": business_ready,
                       "inserted": inserted, "updated": updated, "excluded_codes": excluded,
                       "skipped_unknown_aircraft": skipped_unknown_aircraft,
                       "unknown_aircraft": sorted(unknown)[:20],
                       "failure_codes": sorted(FAILURE_CODES)}
            cur.execute(
                """UPDATE pipeline_run SET status = 'Succeeded', completed_at_utc = %s,
                          records_processed = %s, result_summary = %s WHERE id = %s""",
                (dt.datetime.now(dt.timezone.utc), total, Jsonb(summary), run_id))
        conn.commit()

    print(f"MAINTENANCE total={total} cleansed={cleansed} business_ready={business_ready} "
          f"inserted={inserted} updated={updated} excluded={excluded} unknown_aircraft={skipped_unknown_aircraft}")
    print(f"MAINTENANCE reconcile: cleansed = business_ready + excluded -> "
          f"{business_ready} + {excluded} = {business_ready + excluded} vs cleansed {cleansed} "
          f"{'OK' if business_ready + excluded == cleansed else 'MISMATCH'}")
    spark.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
