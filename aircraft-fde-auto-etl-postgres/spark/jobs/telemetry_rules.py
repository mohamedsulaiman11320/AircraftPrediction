"""Pure validation/mapping rules for raw JSON telemetry -> aircraft_predict_db.telemetry_record.

Kept free of Spark and database code so it can be unit-tested. Rules mirror the API's CSV import
(Backend TelemetryRowValidator): physical plausibility only - no invented engineering thresholds.
`status_flag` is preserved as an operational flag and is NEVER turned into a failure label.
"""
from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

ABSOLUTE_ZERO_C = -273.15
PSI_TO_KPA = 6.894757293168
MAX_ID_LENGTH = 64
MAX_FLAG_LENGTH = 32
REGISTRATION_PATTERN = re.compile(r"^[A-Za-z0-9-]{2,16}$")
FUTURE_SKEW = timedelta(minutes=5)
EARLIEST = datetime(1990, 1, 1, tzinfo=timezone.utc)

MISSING_TEMPERATURE = "MISSING_TEMPERATURE"
MISSING_PRESSURE = "MISSING_PRESSURE"
MISSING_VIBRATION = "MISSING_VIBRATION"


@dataclass
class ValidRecord:
    telemetry_id: str
    registration: str
    recorded_at_utc: datetime
    temperature_c: float | None
    pressure_psi: float | None
    pressure_kpa: float | None
    vibration_index: float | None
    status_flag: str | None
    quality_flags: list[str] = field(default_factory=list)
    row_number: int = 0  # 1-based position in the source file (for traceability)

    @property
    def quality_status(self) -> str:
        return "Suspect" if self.quality_flags else "Valid"


def _number(value, name: str, errors: list[str]) -> float | None:
    if value is None:
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        errors.append(f"{name} '{value}' is not a number")
        return None
    if not math.isfinite(number):
        errors.append(f"{name} is not a finite number")
        return None
    return number


def validate(record: dict, now_utc: datetime) -> tuple[ValidRecord | None, list[str]]:
    """Returns (valid record, []) or (None, reasons)."""
    errors: list[str] = []
    telemetry_id = (record.get("telemetry_id") or "").strip() if isinstance(record.get("telemetry_id"), str) else None
    if not telemetry_id:
        errors.append("telemetry_id is required")
    elif len(telemetry_id) > MAX_ID_LENGTH:
        errors.append(f"telemetry_id is longer than {MAX_ID_LENGTH} characters")

    tail = record.get("tail_number")
    registration = tail.strip().upper() if isinstance(tail, str) else None
    if not registration:
        errors.append("tail_number is required")
    elif not REGISTRATION_PATTERN.match(registration):
        errors.append("tail_number must be 2-16 letters, digits or hyphens")

    recorded_at = None
    epoch = record.get("timestamp_epoch")
    if epoch is None:
        errors.append("timestamp_epoch is required")
    else:
        try:
            recorded_at = datetime.fromtimestamp(int(epoch), tz=timezone.utc)
        except (TypeError, ValueError, OverflowError, OSError):
            errors.append(f"timestamp_epoch '{epoch}' is not a valid Unix time")
        else:
            if recorded_at > now_utc + FUTURE_SKEW:
                errors.append("timestamp_epoch is in the future")
            elif recorded_at < EARLIEST:
                errors.append("timestamp_epoch is implausibly old")

    metrics = record.get("metrics")
    if metrics is not None and not isinstance(metrics, dict):
        errors.append("metrics must be an object")
        metrics = {}
    metrics = metrics or {}
    temperature = _number(metrics.get("engine_temp_c"), "engine_temp_c", errors)
    pressure = _number(metrics.get("oil_pressure_psi"), "oil_pressure_psi", errors)
    vibration = _number(metrics.get("vibration_index"), "vibration_index", errors)
    if temperature is not None and temperature < ABSOLUTE_ZERO_C:
        errors.append("engine_temp_c is below absolute zero")
    if pressure is not None and pressure < 0:
        errors.append("oil_pressure_psi is negative (absolute pressure expected)")
    if temperature is None and pressure is None and not any("not a" in e for e in errors):
        errors.append("both engine_temp_c and oil_pressure_psi are missing")

    flag = record.get("status_flag")
    flag = flag.strip().upper() if isinstance(flag, str) and flag.strip() else None
    if flag is not None and len(flag) > MAX_FLAG_LENGTH:
        errors.append(f"status_flag is longer than {MAX_FLAG_LENGTH} characters")

    if errors:
        return None, errors
    flags = [name for name, value in ((MISSING_TEMPERATURE, temperature), (MISSING_PRESSURE, pressure),
                                      (MISSING_VIBRATION, vibration)) if value is None]
    return ValidRecord(
        telemetry_id=telemetry_id, registration=registration, recorded_at_utc=recorded_at,
        temperature_c=temperature, pressure_psi=pressure,
        pressure_kpa=round(pressure * PSI_TO_KPA, 4) if pressure is not None else None,
        vibration_index=vibration, status_flag=flag, quality_flags=flags,
    ), []


def deduplicate(records: list[ValidRecord]) -> tuple[list[ValidRecord], list[ValidRecord]]:
    """Keeps the first occurrence of each telemetry_id; returns (unique, in-file duplicates)."""
    seen: set[str] = set()
    unique, duplicates = [], []
    for r in records:
        (duplicates if r.telemetry_id in seen else unique).append(r)
        seen.add(r.telemetry_id)
    return unique, duplicates


def s3a_to_location(path: str) -> str:
    """Spark reports s3a://bucket/key; the application stores s3://bucket/key."""
    return "s3://" + path.split("://", 1)[1] if "://" in path else path


def assert_safe_database(name: str, expected: str, forbidden: set[str]) -> None:
    if not name or name.lower() in {f.lower() for f in forbidden}:
        raise RuntimeError(f"Refusing to write to protected database '{name}'.")
    if name != expected:
        raise RuntimeError(f"Connected to '{name}' but APP_DB_NAME is '{expected}'.")
