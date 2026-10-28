#!/usr/bin/env python3
"""Generate a SYNTHETIC, DEVELOPMENT-ONLY telemetry CSV for the Aircraft Failure Prediction Platform.

!!! ALL VALUES PRODUCED BY THIS SCRIPT ARE SYNTHETIC. !!!
They are smooth random curves chosen only to look plausible in charts. They are NOT real aircraft
sensor ranges, NOT manufacturer limits and NOT engineering thresholds, and must never be used to
judge the condition of a real aircraft.  Every row carries ``data_source=SYNTHETIC_DEMO`` and the
aircraft are named ``DEMO-A01`` .. ``DEMO-A12`` so the data cannot be mistaken for real records.

Output format = the API telemetry import format (POST /api/v1/telemetry/imports):

    aircraft_registration,sensor_id,timestamp_utc,temperature,temperature_unit,pressure,pressure_unit,data_source

* two sensors per aircraft (``<reg>-ENG1-TP``, ``<reg>-ENG2-TP``), fixed cadence (default 10 minutes),
  ``--days`` days (default 60) ending at ``--end`` (default today 00:00Z, exclusive);
* some sensors report temperature in F and/or pressure in psi (the API normalises units);
* a small number of injected anomalous excursions/spikes (for the anomaly detector);
* a few deliberately INVALID rows to exercise import validation: missing unit, naive timestamp,
  exact duplicates, missing values (one missing -> Suspect, both missing -> rejected), temperature
  below absolute zero, negative absolute pressure and an unknown registration.

It deliberately generates NO maintenance events / failure labels, so supervised failure
prediction correctly stays ``Blocked`` on this data.

Standard library only.  Example (PowerShell):

    py -3.13 tools\\data\\generate_synthetic_telemetry.py --out $env:TEMP\\synthetic_telemetry.csv --seed 7
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import math
import random
import sys
from dataclasses import dataclass, field

HEADER = ["aircraft_registration", "sensor_id", "timestamp_utc", "temperature", "temperature_unit", "pressure",
          "pressure_unit", "data_source"]
DATA_SOURCE = "SYNTHETIC_DEMO"
N_AIRCRAFT = 12


@dataclass
class Sensor:
    registration: str
    sensor_id: str
    temp_unit: str          # C or F
    pressure_unit: str      # kPa or psi
    temp_base_c: float
    pressure_base_kpa: float
    phase: float
    temp_drift: float = 0.0
    pressure_drift: float = 0.0
    excursions: list[tuple[int, int, float, float]] = field(default_factory=list)  # (start, length, dT, dP)


def fmt_ts(ts: dt.datetime) -> str:
    return ts.astimezone(dt.UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def to_unit_temp(c: float, unit: str) -> float:
    return c * 9.0 / 5.0 + 32.0 if unit == "F" else c


def to_unit_pressure(kpa: float, unit: str) -> float:
    return kpa / 6.894757293168 if unit == "psi" else kpa


def parse_end(value: str | None) -> dt.datetime:
    if not value:
        now = dt.datetime.now(dt.UTC)
        return now.replace(hour=0, minute=0, second=0, microsecond=0)
    text = value.strip().replace("Z", "+00:00")
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"--end must be ISO-8601 (e.g. 2026-10-20 or 2026-10-20T00:00:00Z): {exc}")
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.UTC)
    return parsed.astimezone(dt.UTC)


def build_sensors(rng: random.Random, steps: int) -> list[Sensor]:
    sensors: list[Sensor] = []
    for i in range(1, N_AIRCRAFT + 1):
        reg = f"DEMO-A{i:02d}"
        for engine in (1, 2):
            sensors.append(Sensor(
                registration=reg, sensor_id=f"{reg}-ENG{engine}-TP",
                temp_unit="F" if (i % 3 == 0 and engine == 2) else "C",
                pressure_unit="psi" if (i % 4 == 0 and engine == 1) else "kPa",
                temp_base_c=rng.uniform(70.0, 95.0), pressure_base_kpa=rng.uniform(260.0, 340.0),
                phase=rng.uniform(0, 2 * math.pi)))
    # Injected anomalous excursions (synthetic, arbitrary magnitudes).
    for s in rng.sample(sensors, k=8):
        length = rng.randint(3, 12)
        start = rng.randint(steps // 10, steps - length - 1)
        s.excursions.append((start, length, rng.uniform(25.0, 40.0), -rng.uniform(30.0, 50.0)))
    for s in rng.sample(sensors, k=10):  # single-reading spikes
        start = rng.randint(steps // 10, steps - 2)
        s.excursions.append((start, 1, rng.choice([-1, 1]) * rng.uniform(20.0, 35.0), rng.uniform(-40.0, 40.0)))
    return sensors


def generate(end: dt.datetime, days: float, interval_minutes: int, seed: int) -> tuple[list[list[str]], dict[str, int]]:
    rng = random.Random(seed)
    steps = int(round(days * 24 * 60 / interval_minutes))
    start = end - dt.timedelta(minutes=interval_minutes * steps)
    sensors = build_sensors(rng, steps)
    per_day = 24 * 60 / interval_minutes
    rows: list[list[str]] = []
    for step in range(steps):
        ts = start + dt.timedelta(minutes=interval_minutes * step)
        for s in sensors:
            s.temp_drift = 0.995 * s.temp_drift + rng.gauss(0, 0.05)
            s.pressure_drift = 0.995 * s.pressure_drift + rng.gauss(0, 0.08)
            angle = 2 * math.pi * step / per_day + s.phase
            temp_c = s.temp_base_c + 4.0 * math.sin(angle) + s.temp_drift + rng.gauss(0, 0.3)
            pres_kpa = s.pressure_base_kpa + 3.0 * math.cos(angle) + s.pressure_drift + rng.gauss(0, 0.4)
            for ex_start, length, d_t, d_p in s.excursions:
                if ex_start <= step < ex_start + length:
                    shape = math.sin(math.pi * (step - ex_start + 0.5) / length) if length > 1 else 1.0
                    temp_c += d_t * shape
                    pres_kpa += d_p * shape
            rows.append([s.registration, s.sensor_id, fmt_ts(ts), f"{to_unit_temp(temp_c, s.temp_unit):.2f}",
                         s.temp_unit, f"{to_unit_pressure(pres_kpa, s.pressure_unit):.3f}", s.pressure_unit,
                         DATA_SOURCE])

    stats = {"valid_grid_rows": len(rows), "anomalous_excursions": 8, "single_spikes": 10}
    # ---- deliberately invalid / degraded rows (expected API outcome in comments) -------------
    picks = rng.sample(range(len(rows) // 20, len(rows)), k=22)
    idx = iter(picks)
    for _ in range(3):  # missing temperature unit -> rejected
        rows[next(idx)][4] = ""
    for _ in range(3):  # naive timestamp (no Z/offset) -> rejected
        r = rows[next(idx)]
        r[2] = r[2].rstrip("Z")
    for _ in range(5):  # missing temperature only -> accepted as Suspect (MISSING_TEMPERATURE)
        rows[next(idx)][3] = ""
    for _ in range(5):  # missing pressure only -> accepted as Suspect (MISSING_PRESSURE)
        rows[next(idx)][5] = ""
    for _ in range(2):  # both missing -> rejected
        r = rows[next(idx)]
        r[3] = ""
        r[5] = ""
    for _ in range(2):  # below absolute zero -> rejected
        r = rows[next(idx)]
        r[3] = "-300.00" if r[4] == "C" else "-520.00"
    r = rows[next(idx)]  # negative absolute pressure -> rejected
    r[5] = "-5.000"
    r = rows[next(idx)]  # value with an invalid unit -> rejected
    r[6] = "mmHg"
    taken = set(picks)
    dup_sources = rng.sample([i for i in range(len(rows) // 20, len(rows)) if i not in taken], k=3)
    duplicates = [list(rows[i]) for i in dup_sources]  # exact duplicates -> rejected as duplicates
    unknown = [f"DEMO-X99", "DEMO-X99-ENG1-TP", rows[0][2], "80.00", "C", "300.000", "kPa", DATA_SOURCE]
    rows.extend(duplicates)
    rows.append(unknown)  # unknown registration -> rejected
    rows.sort(key=lambda r: (r[2].rstrip("Z"), r[0], r[1]))
    stats.update({"missing_unit_rows": 3, "naive_timestamp_rows": 3, "missing_temperature_rows": 5,
                  "missing_pressure_rows": 5, "missing_both_rows": 2, "below_absolute_zero_rows": 2,
                  "negative_pressure_rows": 1, "invalid_unit_rows": 1, "duplicate_rows": len(duplicates),
                  "unknown_aircraft_rows": 1, "total_data_rows": len(rows)})
    return rows, stats


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Generate a SYNTHETIC dev-only telemetry CSV (not real sensor data).")
    p.add_argument("--out", required=True, help="output CSV path")
    p.add_argument("--days", type=float, default=60.0, help="number of days (default 60)")
    p.add_argument("--end", type=parse_end, default=None,
                   help="end timestamp, exclusive (ISO-8601, UTC if no offset; default today 00:00Z)")
    p.add_argument("--seed", type=int, default=42, help="random seed (default 42)")
    p.add_argument("--interval-minutes", type=int, default=10, help="reading cadence in minutes (default 10)")
    args = p.parse_args(argv)
    if args.days <= 0 or args.interval_minutes <= 0:
        p.error("--days and --interval-minutes must be positive")
    end = args.end or parse_end(None)
    if end > dt.datetime.now(dt.UTC) + dt.timedelta(minutes=5):
        print("WARNING: --end is in the future; the API rejects timestamps more than 5 minutes ahead.", file=sys.stderr)
    rows, stats = generate(end, args.days, args.interval_minutes, args.seed)
    with open(args.out, "w", newline="", encoding="utf-8") as fh:
        writer = csv.writer(fh)
        writer.writerow(HEADER)
        writer.writerows(rows)
    print(f"Wrote {len(rows)} SYNTHETIC rows (+ header) to {args.out}")
    for k, v in stats.items():
        print(f"  {k}: {v}")
    print("NOTE: values are synthetic and are NOT real aircraft sensor ranges or thresholds. No maintenance labels "
          "are generated, so failure prediction stays Blocked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
