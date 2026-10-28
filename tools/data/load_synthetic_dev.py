"""
Load the SyntheticTrainingData files into the ISOLATED synthetic development database.

Why this exists
---------------
Training the supervised candidate needs the synthetic records in a database the ML package can read.
The operational path is Spark reading MinIO Bronze, but that Spark container is configured (via Docker
Compose env) to write to the OPERATIONAL database. Re-pointing it would put synthetic rows one
mis-set variable away from `aircraft_predict_db`, so this loader writes to the synthetic database
directly instead.

It is not a second ETL: it imports and reuses the very same `telemetry_rules` module the Spark job
uses, so validation, unit normalisation, quality flags, duplicate detection and rejection reasons are
identical. Only the transport differs.

Safety
------
* Refuses the operational and legacy databases outright, and re-checks the connected database name
  with `telemetry_rules.assert_safe_database` (the same guard the Spark job applies).
* Idempotent: re-running updates the same rows rather than duplicating telemetry.
* Maintenance events become Unscheduled/verified/confirmed only for the approved failure code.
  `technician_id` is null in the source and is left null - no identity is invented.

Usage:
  py -3.13 -I tools/data/load_synthetic_dev.py --telemetry <file.json> --maintenance <file.json>
      --database aircraft_predict_db_synth --user <u> --password <p>
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import sys
import uuid

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]
                       / "aircraft-fde-auto-etl-postgres" / "spark" / "jobs"))

import psycopg  # noqa: E402
from psycopg.types.json import Jsonb  # noqa: E402

import telemetry_rules as rules  # noqa: E402  (the SAME rules the Spark ETL applies)

OPERATIONAL = {"aircraft_predict_db", "aircraft_app"}
FAILURE_CODES = {"ENG_UNSCHED_REMOVAL"}
INSPECTION_CODES = {"ENG_INSP"}
EXCLUDED_CODES = {"CANCELLED"}
MAX_STORED_REJECTS = 1000


def connect(host, port, database, user, password):
    if database in OPERATIONAL:
        raise SystemExit("Refusing to load synthetic data into the operational database " + repr(database))
    conn = psycopg.connect(host=host, port=port, dbname=database, user=user,
                           password=password, connect_timeout=10)
    with conn.cursor() as cur:
        cur.execute("select current_database()")
        actual = cur.fetchone()[0]
    rules.assert_safe_database(actual, database, OPERATIONAL)
    return conn


def load_telemetry(conn, path, data_source="SYNTHETIC_DEV"):
    records = json.loads(path.read_text(encoding="utf-8"))
    now = dt.datetime.now(dt.timezone.utc)

    valid = []
    rejects = []
    for i, raw in enumerate(records, start=1):
        record, reasons = rules.validate(raw, now)
        if record is None:
            rejects.append((i, json.dumps(raw)[:2000], "; ".join(reasons)))
        else:
            record.row_number = i
            valid.append(record)

    loadable, in_file_dups = rules.deduplicate(valid)
    for dup in in_file_dups:
        rejects.append((dup.row_number, dup.telemetry_id, "duplicate telemetry_id in this file"))

    import_id = str(uuid.uuid4())
    with conn.cursor() as cur:
        cur.execute(
            "INSERT INTO telemetry_import (id, status_id, file_name, file_sha256, stored_path, source, status,"
            " total_rows, accepted_rows, rejected_rows, duplicate_rows, warning_count, warnings,"
            " requested_at_utc, started_at_utc, file_size_bytes)"
            " VALUES (%s, 1, %s, %s, %s, 'RawJson', 'Processing', %s, 0, 0, 0, 0, %s, %s, %s, %s)",
            (import_id, path.name, uuid.uuid4().hex + uuid.uuid4().hex,
             "synthetic-dev/bronze/" + path.name, len(records), Jsonb([]), now, now, path.stat().st_size))

        # Aircraft are auto-registered exactly as the ETL does, including the status reason the
        # derived-state rule recognises as "awaiting release".
        created = []
        for reg in sorted({r.registration for r in loadable}):
            cur.execute(
                "INSERT INTO aircraft (id, status_id, registration_number, model, manufacturer, fleet_group,"
                " operational_status, status_reason, status_changed_at_utc, created_at_utc, updated_at_utc)"
                " VALUES (%s, 1, %s, 'Unknown', 'Unknown', 'Unassigned', 'UnderMaintenance', %s, %s, %s, %s)"
                " ON CONFLICT (registration_number) DO NOTHING",
                (str(uuid.uuid4()), reg,
                 "Auto-registered by the telemetry ETL; complete model/manufacturer and release via the application.",
                 now, now, now))
            if cur.rowcount:
                created.append(reg)

        cur.execute("select upper(registration_number), id from aircraft")
        fleet = {r[0]: r[1] for r in cur.fetchall()}

        inserted = 0
        already = 0
        for r in loadable:
            aircraft_id = fleet.get(r.registration.upper())
            if aircraft_id is None:
                rejects.append((r.row_number, r.telemetry_id, "unregistered aircraft"))
                continue
            cur.execute(
                "INSERT INTO telemetry_record (status_id, aircraft_id, import_id, source_record_id,"
                " recorded_at_utc, temperature_c, temperature_value, temperature_unit,"
                " pressure_kpa, pressure_value, pressure_unit, vibration_index,"
                " source_status_flag, data_source, quality_status, quality_flags, ingested_at_utc)"
                " VALUES (1, %s, %s, %s, %s, %s, %s, 'C', %s, %s, 'psi', %s, %s, %s, %s, %s, %s)"
                " ON CONFLICT (aircraft_id, sensor_id, recorded_at_utc) DO NOTHING",
                (aircraft_id, import_id, r.telemetry_id, r.recorded_at_utc,
                 r.temperature_c, r.temperature_c, r.pressure_kpa, r.pressure_psi, r.vibration_index,
                 r.status_flag, data_source, r.quality_status, Jsonb(r.quality_flags), now))
            if cur.rowcount:
                inserted += 1
            else:
                already += 1

        for row_number, raw, reasons in rejects[:MAX_STORED_REJECTS]:
            cur.execute(
                "INSERT INTO telemetry_rejected_row (status_id, import_id, row_number, raw_line, reasons)"
                " VALUES (1, %s, %s, %s, %s)",
                (import_id, row_number, str(raw)[:2000], reasons[:1000]))

        cur.execute(
            "UPDATE telemetry_import SET status = 'Completed', accepted_rows = %s, rejected_rows = %s,"
            " duplicate_rows = %s, completed_at_utc = %s WHERE id = %s",
            (inserted, len(rejects), already, dt.datetime.now(dt.timezone.utc), import_id))
    conn.commit()

    return {"import_id": import_id, "total": len(records), "accepted": inserted,
            "rejected": len(rejects), "already_stored": already,
            "in_file_duplicates": len(in_file_dups), "auto_registered": created}


def load_maintenance(conn, path):
    rows = json.loads(path.read_text(encoding="utf-8"))
    now = dt.datetime.now(dt.timezone.utc)
    inserted = 0
    updated = 0
    excluded = 0
    unknown = 0

    with conn.cursor() as cur:
        cur.execute("select upper(registration_number), id from aircraft")
        fleet = {r[0]: r[1] for r in cur.fetchall()}

        for row in rows:
            code = (row.get("action_code") or "").strip().upper()
            if not code or code in EXCLUDED_CODES:
                excluded += 1
                continue
            if code in FAILURE_CODES:
                event_type, verified, confirmed = "Unscheduled", True, True
            elif code in INSPECTION_CODES:
                event_type, verified, confirmed = "Inspection", True, False
            else:
                event_type, verified, confirmed = "Scheduled", True, False

            aircraft_id = fleet.get((row.get("aircraft_sn") or "").strip().upper())
            if aircraft_id is None:
                unknown += 1
                continue

            occurred = dt.datetime.fromisoformat(row["event_date"].strip()[:10]).replace(tzinfo=dt.timezone.utc)
            # technician_id is null in the source and stays null: no identity is invented.
            technician = row.get("technician_id")
            description = "[" + row["log_id"] + "] " + code
            if technician:
                description += " by " + str(technician)
            if row.get("notes"):
                description += ": " + row["notes"]

            cur.execute(
                "SELECT id FROM maintenance_log WHERE aircraft_id = %s AND occurred_at_utc = %s"
                " AND description LIKE %s",
                (aircraft_id, occurred, "[" + row["log_id"] + "]%"))
            existing = cur.fetchone()
            if existing:
                cur.execute(
                    "UPDATE maintenance_log SET event_type = %s, description = %s, is_verified = %s,"
                    " actual_failure_confirmed = %s WHERE id = %s",
                    (event_type, description[:4000], verified, confirmed, existing[0]))
                updated += 1
            else:
                cur.execute(
                    "INSERT INTO maintenance_log (id, status_id, aircraft_id, event_type, occurred_at_utc,"
                    " description, actual_failure_confirmed, is_verified, source, created_at_utc)"
                    " VALUES (%s, 1, %s, %s, %s, %s, %s, %s, 'Import', %s)",
                    (str(uuid.uuid4()), aircraft_id, event_type, occurred, description[:4000],
                     confirmed, verified, now))
                inserted += 1
    conn.commit()
    return {"total": len(rows), "inserted": inserted, "updated": updated,
            "excluded": excluded, "unknown_aircraft": unknown}


def archive_sources(env_file: pathlib.Path, files: list[pathlib.Path]) -> list[str]:
    """Archive the supplied synthetic source files, unchanged, in the DEVELOPMENT bucket (bronze/ prefix).

    Uses the development environment's own MinIO user (AFP_ARTIFACT_* in .env.synthetic), which can only reach
    its own bucket. Refuses unless the file declares AFP_ENVIRONMENT=development and its bucket is not forbidden.
    Objects are immutable: an identical re-run is a no-op, different content under the same key is refused.
    """
    from afp_ml.artifacts import MinioObjectStore, put_immutable, sha256_bytes
    from afp_ml.config import parse_env_file

    env = parse_env_file(env_file)
    if env.get("AFP_ENVIRONMENT", "").lower() != "development":
        raise SystemExit(f"{env_file} is not a development configuration (AFP_ENVIRONMENT=development); not archiving.")
    bucket = env.get("AFP_ARTIFACT_BUCKET")
    forbidden = {b.strip().lower() for b in env.get("AFP_ARTIFACT_FORBIDDEN_BUCKETS", "").split(",") if b.strip()}
    if not bucket or bucket.lower() in forbidden:
        raise SystemExit("The development bucket is not configured (run tools/minio/provision_model_storage.py).")
    remote = MinioObjectStore(env["AFP_ARTIFACT_ENDPOINT"], bucket, env["AFP_ARTIFACT_ACCESS_KEY"],
                              env["AFP_ARTIFACT_SECRET_KEY"])
    uris = []
    for f in files:
        data = f.read_bytes()
        kind = "telemetry" if "telemetry" in f.name else "maintenance" if "maintenance" in f.name else "docs"
        key = f"bronze/{kind}/{f.name}"
        written = put_immutable(remote, key, data, "application/json" if f.suffix == ".json" else "text/plain")
        uris.append(f"s3://{bucket}/{key} sha256={sha256_bytes(data)[:12]}... "
                    f"({'archived' if written else 'already archived, identical'})")
    return uris


def main():
    ap = argparse.ArgumentParser(description="Load synthetic development data into an isolated database.")
    ap.add_argument("--telemetry", type=pathlib.Path, required=True)
    ap.add_argument("--maintenance", type=pathlib.Path, help="maintenance file (optional for a telemetry-only fixture)")
    ap.add_argument("--data-source", default="SYNTHETIC_DEV",
                    help="telemetry_record.data_source label, e.g. SCENARIO_FIXTURE for a separately identified scenario")
    ap.add_argument("--database", required=True)
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=5432)
    ap.add_argument("--user", required=True)
    ap.add_argument("--password", required=True)
    ap.add_argument("--archive-env-file", type=pathlib.Path,
                    help="archive the source files in the development MinIO bucket first (e.g. .env.synthetic)")
    ap.add_argument("--archive-only", action="store_true", help="archive the source files and stop (no database load)")
    args = ap.parse_args()

    if args.archive_env_file:
        extra = [p for p in (args.telemetry.parent / "README_SYNTHETIC_TRAINING_DATA.txt",) if p.is_file()]
        for line in archive_sources(args.archive_env_file, [f for f in (args.telemetry, args.maintenance) if f] + extra):
            print("ARCHIVE " + line)
        if args.archive_only:
            return 0

    with connect(args.host, args.port, args.database, args.user, args.password) as conn:
        print("connected to " + args.database)
        t = load_telemetry(conn, args.telemetry, args.data_source)
        total_accounted = t["accepted"] + t["rejected"] + t["already_stored"]
        print("TELEMETRY   total={total} accepted={accepted} rejected={rejected} "
              "already_stored={already_stored}".format(**t))
        print("            reconcile: {} + {} + {} = {} vs total {} -> {}".format(
            t["accepted"], t["rejected"], t["already_stored"], total_accounted, t["total"],
            "OK" if total_accounted == t["total"] else "MISMATCH"))
        print("            in-file duplicates: {}".format(t["in_file_duplicates"]))
        print("            auto-registered: {}".format(t["auto_registered"]))
        if not args.maintenance:
            return 0
        m = load_maintenance(conn, args.maintenance)
        print("MAINTENANCE total={total} inserted={inserted} updated={updated} excluded={excluded} "
              "unknown_aircraft={unknown_aircraft}".format(**m))
    return 0


if __name__ == "__main__":
    sys.exit(main())
