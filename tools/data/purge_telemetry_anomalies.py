"""Delete anomalies NOT raised from the sensor forecast, and the work orders raised for them (user decision 2026-10-28).

Since 2026-10-28 anomalies and work orders come from the 30-day forecast only (source ``ForecastModel``). This removes the
older rows found in recorded telemetry (``IsolationForest``, ``ThresholdRule``) and from failure-model scores
(``FailureModel``), together with each linked work order and its reports. Foreign keys from inspection flags,
maintenance logs and risk predictions are SET NULL by the schema; audit log rows are kept.

Runs as the schema owner from an env file. Refuses ``aircraft_app``. Take a ``pg_dump`` first.

    python tools/data/purge_telemetry_anomalies.py --env-file .env --dry-run
    python tools/data/purge_telemetry_anomalies.py --env-file .env
"""
from __future__ import annotations

import argparse
import pathlib

import psycopg

PROTECTED = {"aircraft_app"}
KEEP_SOURCE = "ForecastModel"


def env_file(path: pathlib.Path) -> dict[str, str]:
    out = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip().strip('"').strip("'")
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--env-file", type=pathlib.Path, required=True)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    e = env_file(args.env_file)
    db = e["DB_NAME"]
    if db in PROTECTED:
        raise SystemExit(f"Refusing protected database {db!r}.")
    with psycopg.connect(host=e["DB_HOST"], port=e["DB_PORT"], dbname=db, user=e["DB_USER"], password=e["DB_PASSWORD"]) as conn:
        cur = conn.cursor()
        cur.execute("select current_database()")
        assert cur.fetchone()[0] == db
        cur.execute("select id from aircraft_anomaly_result where source <> %s", (KEEP_SOURCE,))
        anomalies = [r[0] for r in cur.fetchall()]
        cur.execute("select id, work_order_number, status from maintenance_work_order where anomaly_id = any(%s)", (anomalies,))
        orders = cur.fetchall()
        order_ids = [o[0] for o in orders]
        cur.execute("select count(*) from work_order_report where work_order_id = any(%s)", (order_ids,))
        reports = cur.fetchone()[0]
        print(f"{db}: {len(anomalies)} non-forecast anomalies, {len(orders)} linked work orders, {reports} reports")
        for _, number, status in orders:
            print(f"  {number} ({status})")
        if args.dry_run:
            print("Dry run - nothing deleted.")
            return 0
        cur.execute("delete from work_order_report where work_order_id = any(%s)", (order_ids,))
        cur.execute("delete from maintenance_work_order where id = any(%s)", (order_ids,))
        deleted_orders = cur.rowcount
        cur.execute("delete from aircraft_anomaly_result where id = any(%s)", (anomalies,))
        print(f"Deleted {cur.rowcount} anomalies, {deleted_orders} work orders, {reports} reports.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
