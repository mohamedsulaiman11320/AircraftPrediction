"""
Phase 7 development acceptance scenario - a SEPARATELY IDENTIFIED synthetic fixture (development database only).

What it represents (not a claim about real aircraft):
  * N120UA - an oil-pressure concern: oil pressure below the approved limit for six hours.
  * N237UA - oil-pressure AND engine-temperature concerns in the same six hours.
  * N512UA, N668UA, N784UA, N903UA - within every approved limit for the whole day.

How the values are made (deterministic, no randomness):
  * One reading per aircraft per hour for 2026-10-24 00:00-23:00 UTC, i.e. the day after the synthetic 12-month
    file ends (2026-10-23 23:00 UTC), so the dashboard's latest-24-hour window covers exactly this fixture.
  * Engine temperature and vibration continue each aircraft's OWN mean over its last 24 synthetic readings, with a
    small daily sine wobble.
  * Oil pressure is a nominal 45 PSI (+/- 3 PSI wobble). This is a deliberate fixture choice, documented here:
    the synthetic file (and the operational data) never reach the approved 35 PSI limit - 100 % of their readings
    are below it - so "within limits" cannot be derived from those aircraft's own history.
  * The concern hours (12:00-17:00 UTC) set oil pressure to 30-32 PSI (both scenario aircraft) and engine
    temperature to 712-745 C (N237UA only); the limits themselves are read from tools/db/seed/csv/health_threshold.csv.
  * status_flag follows the source system's inferred rule (tools/simulator/telemetry_simulator.json).
  * Every id is prefixed TEL-SCN-P7-; loaded with --data-source SCENARIO_FIXTURE so the rows are identifiable.
  * No failure, maintenance or label is created: the scenario has no effect on model training labels.

Usage:
  ml/.venv/Scripts/python -I tools/data/scenarios/make_phase7_scenario.py      # writes phase7_health_dashboard_scenario.json
"""
from __future__ import annotations

import csv
import datetime as dt
import json
import math
import pathlib
import statistics
import sys

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "ml"))
from afp_ml.simulator import SimulatorConfig, status_flag  # noqa: E402  (the same flag rule the simulator uses)

SOURCE = ROOT / "SyntheticTrainingData" / "telemetry_synthetic_12mo.json"
LIMITS = ROOT / "tools" / "db" / "seed" / "csv" / "health_threshold.csv"
OUT = pathlib.Path(__file__).with_name("phase7_health_dashboard_scenario.json")

DAY = dt.datetime(2026, 10, 24, tzinfo=dt.timezone.utc)
CONCERN_HOURS = range(12, 18)
OIL_CONCERN = {"N120UA", "N237UA"}
TEMP_CONCERN = {"N237UA"}
NOMINAL_OIL_PSI = 45.0


def limits() -> dict[str, float]:
    with LIMITS.open(encoding="utf-8") as fh:
        rows = {r["code"]: r for r in csv.DictReader(fh)}
    return {"oil": float(rows["OilPressureLowPsi"]["threshold_value"]), "temp": float(rows["EngineTemperatureHighC"]["threshold_value"])}


def build() -> list[dict]:
    lim = limits()
    rule = SimulatorConfig.load().flag_rule
    source = json.loads(SOURCE.read_text(encoding="utf-8"))
    tails = sorted({r["tail_number"] for r in source})
    records, n = [], 0
    for tail in tails:
        recent = [r["metrics"] for r in source if r["tail_number"] == tail][-24:]
        temp_mean = statistics.mean(m["engine_temp_c"] for m in recent if m.get("engine_temp_c") is not None)
        vib_mean = statistics.mean(m["vibration_index"] for m in recent if m.get("vibration_index") is not None)
        for h in range(24):
            wobble = math.sin(2 * math.pi * h / 24)
            temp = temp_mean + 12 * wobble
            oil = NOMINAL_OIL_PSI + 3 * wobble
            if h in CONCERN_HOURS and tail in OIL_CONCERN:
                oil = lim["oil"] - 5 + (h - 12) * 0.4                      # 30.0 .. 32.0 PSI
            if h in CONCERN_HOURS and tail in TEMP_CONCERN:
                temp = lim["temp"] + 12 + (h - 12) * 6.6                   # 712 .. 745 C
            metrics = {"engine_temp_c": round(temp, 2), "oil_pressure_psi": round(oil, 2), "vibration_index": round(vib_mean + 0.1 * wobble, 3)}
            n += 1
            records.append({
                "telemetry_id": f"TEL-SCN-P7-{n:06d}",
                "tail_number": tail,
                "timestamp_epoch": int((DAY + dt.timedelta(hours=h)).timestamp()),
                "metrics": metrics,
                "status_flag": status_flag(metrics, rule),
            })
    return records


if __name__ == "__main__":
    data = build()
    OUT.write_text(json.dumps(data, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {len(data)} records to {OUT.relative_to(ROOT)}")
