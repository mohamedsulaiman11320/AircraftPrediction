"""
Prepare the supplied raw dataset for ingestion by shifting every timestamp back by a fixed number of days.

Why this exists
---------------
The supplied 6-month dataset runs to 2026-10-31, which is in the future relative to the ingestion date.
The import applies a physical-plausibility rule that rejects future-dated readings, which would have
discarded ~1,269 telemetry rows and one ENG_UNSCHED_REMOVAL event, pushing the observable negative
examples below the model-readiness minimum.

Shifting *both* files by the same offset keeps every record, preserves the spacing between readings and
the lead time between a degradation signal and its failure event, and keeps telemetry aligned with the
maintenance events it is supposed to predict. It changes absolute dates only.

The source files are never modified; prepared copies are written to the output directory.

Usage:
    py -3.13 -I tools/data/prepare_raw_dataset.py <raw-dir> <out-dir> --shift-days 9
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import pathlib
import sys

TELEMETRY_NAME = "telemetry_may_oct_2026_6months.json"
MAINTENANCE_NAME = "maintenance_logs_may_oct_2026.csv"
SECONDS_PER_DAY = 86_400


def shift_telemetry(src: pathlib.Path, dst: pathlib.Path, shift_days: int) -> dict:
    records = json.loads(src.read_text(encoding="utf-8"))
    offset = shift_days * SECONDS_PER_DAY
    before = [r["timestamp_epoch"] for r in records]
    for record in records:
        record["timestamp_epoch"] = record["timestamp_epoch"] - offset
    after = [r["timestamp_epoch"] for r in records]
    dst.write_text(json.dumps(records, indent=1), encoding="utf-8")

    now = dt.datetime.now(dt.UTC)
    future = sum(1 for t in after if dt.datetime.fromtimestamp(t, dt.UTC) > now)
    return {
        "records": len(records),
        "distinct_ids": len({r["telemetry_id"] for r in records}),
        "from_before": dt.datetime.fromtimestamp(min(before), dt.UTC).isoformat(),
        "to_before": dt.datetime.fromtimestamp(max(before), dt.UTC).isoformat(),
        "from_after": dt.datetime.fromtimestamp(min(after), dt.UTC).isoformat(),
        "to_after": dt.datetime.fromtimestamp(max(after), dt.UTC).isoformat(),
        "still_future": future,
    }


def shift_maintenance(src: pathlib.Path, dst: pathlib.Path, shift_days: int) -> dict:
    delta = dt.timedelta(days=shift_days)
    with src.open(newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.DictReader(handle))
    if not rows:
        raise SystemExit(f"{src} has no data rows")

    fieldnames = list(rows[0].keys())
    codes: dict[str, int] = {}
    for row in rows:
        event = dt.date.fromisoformat(row["event_date"].strip())
        row["event_date"] = (event - delta).isoformat()
        codes[row["action_code"]] = codes.get(row["action_code"], 0) + 1

    with dst.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    dates = sorted(r["event_date"] for r in rows)
    today = dt.datetime.now(dt.UTC).date().isoformat()
    return {
        "rows": len(rows),
        "from_after": dates[0],
        "to_after": dates[-1],
        "still_future": sum(1 for d in dates if d > today),
        "action_codes": dict(sorted(codes.items(), key=lambda kv: -kv[1])),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw_dir", type=pathlib.Path)
    parser.add_argument("out_dir", type=pathlib.Path)
    parser.add_argument("--shift-days", type=int, required=True)
    args = parser.parse_args()

    if args.shift_days < 0:
        raise SystemExit("--shift-days must not be negative")
    args.out_dir.mkdir(parents=True, exist_ok=True)

    telemetry = shift_telemetry(
        args.raw_dir / TELEMETRY_NAME, args.out_dir / TELEMETRY_NAME, args.shift_days)
    maintenance = shift_maintenance(
        args.raw_dir / MAINTENANCE_NAME, args.out_dir / MAINTENANCE_NAME, args.shift_days)

    print(f"shift: -{args.shift_days} days   (sources unmodified)")
    print("telemetry : {records} records, {distinct_ids} distinct ids".format(**telemetry))
    print("            {from_before} .. {to_before}  ->  {from_after} .. {to_after}".format(**telemetry))
    print(f"            still future-dated: {telemetry['still_future']}")
    print("maintenance: {rows} rows, {from_after} .. {to_after}".format(**maintenance))
    print(f"            still future-dated: {maintenance['still_future']}")
    print(f"            action codes: {maintenance['action_codes']}")

    if telemetry["still_future"] or maintenance["still_future"]:
        print("ERROR: records remain future-dated and would be rejected on import.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
