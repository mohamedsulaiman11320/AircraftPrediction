"""Merge forecast anomalies that share (aircraft, first breaching hour, forecaster) into one record (2026-10-28).

Since 2026-10-28 limits that start breaching in the same forecast hour are ONE anomaly (AnomalyProcessingService). This
merges rows created under the earlier one-per-limit rule: the primary row (the one a work order points at, else the
lowest id) keeps its id and gets every rule code and breach; work orders and inspection flags pointing at the others
are repointed to it; the others are deleted. Every forecast anomaly's dedup_key is then set to the natural key
``{tail}_{first breach yyyyMMddHHmmss}_{forecaster id}``.

Runs as the schema owner from an env file. Refuses ``aircraft_app``. Take a ``pg_dump`` first.

    python tools/data/merge_forecast_anomalies.py --env-file .env --dry-run
    python tools/data/merge_forecast_anomalies.py --env-file .env
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib

import psycopg

PROTECTED = {"aircraft_app"}


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
        cur.execute("""SELECT r.id, r.aircraft_id, a.registration_number, r.observed_at_utc, r.model_version_id, r.rule_codes,
                              r.evidence, r.last_observed_at_utc, r.work_order_id,
                              EXISTS (SELECT 1 FROM maintenance_work_order w WHERE w.anomaly_id = r.id) AS is_primary
                         FROM aircraft_anomaly_result r JOIN aircraft a ON a.id = r.aircraft_id
                        WHERE r.source = 'ForecastModel' ORDER BY r.id""")
        rows = cur.fetchall()
        groups: dict[tuple, list] = {}
        for row in rows:
            groups.setdefault((row[1], row[3], row[4]), []).append(row)
        merged = 0
        for (_, observed, model_id), members in groups.items():
            if len(members) < 2:
                continue
            keep = next((m for m in members if m[9]), members[0])
            others = [m for m in members if m[0] != keep[0]]
            codes = sorted({c for m in members for c in (m[5] or "").split(",") if c})
            evidence = dict(keep[6] or {})
            evidence["breaches"] = sorted((b for m in members for b in (m[6] or {}).get("breaches", [])), key=lambda b: b.get("code", ""))
            lasts = [m[7] for m in members if m[7] is not None]
            evidence["lastBreachUtc"] = max(lasts).isoformat() if lasts else evidence.get("lastBreachUtc")
            work_order = keep[8] or next((m[8] for m in members if m[8]), None)
            print(f"{keep[2]} {observed.astimezone(dt.UTC):%Y-%m-%d %H:%M} UTC: keep #{keep[0]}, merge {[m[0] for m in others]} -> {','.join(codes)}")
            merged += len(others)
            if args.dry_run:
                continue
            ids = [m[0] for m in others]
            cur.execute("UPDATE maintenance_work_order SET anomaly_id = %s WHERE anomaly_id = ANY(%s)", (keep[0], ids))
            cur.execute("UPDATE inspection_flag SET anomaly_id = %s WHERE anomaly_id = ANY(%s)", (keep[0], ids))
            cur.execute("DELETE FROM aircraft_anomaly_result WHERE id = ANY(%s)", (ids,))
            cur.execute("""UPDATE aircraft_anomaly_result SET rule_codes = %s, evidence = %s::jsonb, feature_values = %s::jsonb,
                                  last_observed_at_utc = %s, work_order_id = %s, updated_at_utc = now() WHERE id = %s""",
                        (",".join(codes), json.dumps(evidence), json.dumps(evidence), max(lasts) if lasts else None, work_order, keep[0]))
        keys = 0
        for row in rows:
            if args.dry_run or row[0] not in {m[0] for g in groups.values() for m in g}:
                continue
            cur.execute("SELECT 1 FROM aircraft_anomaly_result WHERE id = %s", (row[0],))
            if cur.fetchone() is None:
                continue                                                    # merged away above
            key = f"{row[2]}_{row[3].astimezone(dt.UTC):%Y%m%d%H%M%S}_{row[4]}"   # psycopg returns session-zone times
            cur.execute("UPDATE aircraft_anomaly_result SET dedup_key = %s WHERE id = %s", (key, row[0]))
            keys += 1
        print(f"{db}: {merged} duplicate anomal{'y' if merged == 1 else 'ies'} merged; {keys} dedup keys set"
              + (" (dry run - nothing changed)" if args.dry_run else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
