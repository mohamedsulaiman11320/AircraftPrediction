"""
Rewrite the last N days of telemetry with the scripted scenario (user decision 2026-10-28: "back up, then rewrite").

For every reading in the window, the values are replaced IN PLACE with the deterministic scenario reading for that
aircraft-hour's role (afp_ml.scenario; config tools/simulator/telemetry_simulator.json -> scenario), and the row is
labelled data_source = 'SCENARIO'. Ids and timestamps are kept, so references (anomaly results) stay valid. Runs as the
schema owner (the ML roles cannot update telemetry). Refuses aircraft_app; take a pg_dump backup first.

  --env-file .env           operational database; with --raw-file also rewrites the simulator's raw source file
                            (same values, atomic write under the simulator lock, previous version kept).
  --env-file .env.synthetic synthetic database; with --extend-to-now also fills the hours after its last reading up to
                            the current IST midnight with scenario readings, loaded through the ETL validation rules.
  --dry-run                 report what would change, write nothing.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "ml"))
sys.path.insert(0, str(ROOT / "tools" / "data"))

import psycopg  # noqa: E402

from afp_ml.features import PSI_TO_KPA  # noqa: E402
from afp_ml.scenario import metrics_for, roles_at  # noqa: E402
from afp_ml.simulator import (SimulatorConfig, atomic_write, default_state_dir, dumps, exclusive_lock,  # noqa: E402
                              local_midnight_utc, status_flag)

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
    ap.add_argument("--raw-file", action="store_true")
    ap.add_argument("--extend-to-now", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    cfg = SimulatorConfig.load()
    sc = cfg.scenario
    if sc is None or not sc.enabled:
        raise SystemExit("The scenario is not enabled in telemetry_simulator.json; nothing to apply.")
    e = env_file(args.env_file)
    db = e["DB_NAME"]
    if db in PROTECTED:
        raise SystemExit(f"Refusing protected database {db!r}.")
    conn = psycopg.connect(host=e["DB_HOST"], port=e["DB_PORT"], dbname=db, user=e["DB_USER"], password=e["DB_PASSWORD"])
    with conn, conn.cursor() as cur:
        cur.execute("select current_database()")
        assert cur.fetchone()[0] == db
        cur.execute("select max(recorded_at_utc) from telemetry_record")
        latest = cur.fetchone()[0]
        start = latest - dt.timedelta(days=sc.rewrite_history_days)
        cur.execute("""select t.id, a.registration_number, t.recorded_at_utc, t.source_record_id
                         from telemetry_record t join aircraft a on a.id = t.aircraft_id
                        where t.recorded_at_utc > %s order by t.recorded_at_utc""", (start,))
        rows = cur.fetchall()
        updates = []
        new_values: dict[str, dict] = {}
        for rid, tail, ts, source_id in rows:
            if tail not in cfg.expected_aircraft:
                continue
            epoch = int(ts.timestamp())
            role = roles_at(sc, cfg.expected_aircraft, ts)[tail]
            m = metrics_for(sc, tail, role, epoch, cfg.decimals)
            flag = status_flag(m, cfg.flag_rule)
            updates.append((m["engine_temp_c"], m["engine_temp_c"], m["oil_pressure_psi"] * PSI_TO_KPA, m["oil_pressure_psi"],
                            m["vibration_index"], flag, rid))
            if source_id:
                new_values[source_id] = {"metrics": m, "status_flag": flag}
        print(f"{db}: {len(updates)} readings in ({start:%Y-%m-%d %H:%M}, {latest:%Y-%m-%d %H:%M}] UTC to rewrite")
        if not args.dry_run:
            cur.executemany("""update telemetry_record set temperature_c = %s, temperature_value = %s, temperature_unit = 'C',
                                      pressure_kpa = %s, pressure_value = %s, pressure_unit = 'psi', vibration_index = %s,
                                      source_status_flag = %s, quality_status = 'Valid', quality_flags = '[]'::jsonb,
                                      data_source = 'SCENARIO'
                                where id = %s""", updates)
            print(f"  updated {cur.rowcount if cur.rowcount >= 0 else len(updates)} rows (data_source = SCENARIO)")

    if args.raw_file:
        state = default_state_dir()
        with exclusive_lock(state / "simulator.lock"):
            data = cfg.source_file.read_bytes()
            records = json.loads(data)
            changed = 0
            for r in records:
                v = new_values.get(r["telemetry_id"])
                if v is not None:
                    r["metrics"], r["status_flag"] = v["metrics"], v["status_flag"]
                    changed += 1
            print(f"raw file {cfg.source_file.name}: {changed} records to rewrite")
            if not args.dry_run and changed:
                versions = state / "versions"
                versions.mkdir(parents=True, exist_ok=True)
                stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
                atomic_write(versions / f"{cfg.source_file.stem}.pre-scenario.{stamp}.json", data)
                atomic_write(cfg.source_file, dumps(records))
                print(f"  rewritten; previous version kept in {versions}")

    if args.extend_to_now:
        import load_synthetic_dev as loader
        cutoff = local_midnight_utc(dt.datetime.now(dt.timezone.utc), cfg.timezone)
        first = int(latest.timestamp()) + cfg.cadence_seconds
        hours = list(range(first, int(cutoff.timestamp()), cfg.cadence_seconds))
        recs = []
        n = 0
        for epoch in hours:
            roles = roles_at(sc, cfg.expected_aircraft, dt.datetime.fromtimestamp(epoch, dt.timezone.utc))
            for tail in cfg.expected_aircraft:
                m = metrics_for(sc, tail, roles[tail], epoch, cfg.decimals)
                n += 1
                recs.append({"telemetry_id": f"{sc.id_prefix}EXT-{epoch}-{tail}", "tail_number": tail, "timestamp_epoch": epoch,
                             "metrics": m, "status_flag": status_flag(m, cfg.flag_rule)})
        print(f"extend: {len(recs)} scenario readings for {len(hours)} hours up to {cutoff:%Y-%m-%d %H:%M} UTC")
        if recs and not args.dry_run:
            out = pathlib.Path(__file__).with_name("scenarios") / f"scenario_extend_{db}_{hours[0]}_{hours[-1]}.json"
            out.write_text(json.dumps(recs, indent=1) + "\n", encoding="utf-8")
            with loader.connect(e["DB_HOST"], int(e["DB_PORT"]), db, e["DB_USER"], e["DB_PASSWORD"]) as c2:
                t = loader.load_telemetry(c2, out, "SCENARIO")
                print("  loaded:", {k: t[k] for k in ("total", "accepted", "rejected", "already_stored")})
    return 0


if __name__ == "__main__":
    sys.exit(main())
