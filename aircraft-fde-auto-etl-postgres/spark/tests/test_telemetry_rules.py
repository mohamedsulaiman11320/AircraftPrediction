"""Unit tests for the ETL validation/mapping rules (no Spark, no database).

Run: ml\\.venv\\Scripts\\python -m pytest aircraft-fde-auto-etl-postgres\\spark\\tests -q
"""
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "jobs"))
import telemetry_rules as rules  # noqa: E402

NOW = datetime(2026, 10, 21, tzinfo=timezone.utc)


def record(**overrides):
    base = {"telemetry_id": "TEL-1", "tail_number": "n120ua", "timestamp_epoch": 1788220800,
            "metrics": {"engine_temp_c": 476.8, "oil_pressure_psi": 25.5, "vibration_index": 0.58}, "status_flag": "NORMAL"}
    base.update(overrides)
    return base


def test_valid_record_is_mapped_and_normalised():
    ok, errors = rules.validate(record(), NOW)
    assert errors == []
    assert ok.registration == "N120UA"
    assert ok.recorded_at_utc == datetime(2026, 9, 1, tzinfo=timezone.utc)
    assert ok.pressure_kpa == pytest.approx(25.5 * 6.894757293168, abs=1e-3)
    assert ok.quality_status == "Valid"
    assert ok.status_flag == "NORMAL"          # kept as an operational flag
    assert not hasattr(ok, "failure_label")    # never derived


@pytest.mark.parametrize("overrides, reason", [
    ({"telemetry_id": None}, "telemetry_id is required"),
    ({"telemetry_id": "x" * 65}, "longer than 64"),
    ({"tail_number": None}, "tail_number is required"),
    ({"tail_number": "BAD TAIL!"}, "2-16 letters"),
    ({"timestamp_epoch": None}, "timestamp_epoch is required"),
    ({"timestamp_epoch": 4102444800}, "in the future"),
    ({"timestamp_epoch": 100}, "implausibly old"),
    ({"metrics": {"engine_temp_c": -300, "oil_pressure_psi": 20}}, "below absolute zero"),
    ({"metrics": {"engine_temp_c": 400, "oil_pressure_psi": -1}}, "negative"),
    ({"metrics": {"engine_temp_c": "hot", "oil_pressure_psi": 20}}, "not a number"),
    ({"metrics": {"engine_temp_c": float("nan"), "oil_pressure_psi": 20}}, "not a finite number"),
    ({"metrics": {"engine_temp_c": None, "oil_pressure_psi": None}}, "both engine_temp_c and oil_pressure_psi are missing"),
    ({"metrics": "oops"}, "metrics must be an object"),
])
def test_invalid_records_are_rejected_with_reason(overrides, reason):
    ok, errors = rules.validate(record(**overrides), NOW)
    assert ok is None
    assert any(reason in e for e in errors), errors


def test_missing_single_measurement_is_suspect_not_rejected():
    ok, _ = rules.validate(record(metrics={"engine_temp_c": 480.0, "oil_pressure_psi": None, "vibration_index": None}), NOW)
    assert ok.quality_status == "Suspect"
    assert ok.quality_flags == [rules.MISSING_PRESSURE, rules.MISSING_VIBRATION]
    assert ok.pressure_kpa is None


def test_in_file_duplicates_keep_first_occurrence():
    a, _ = rules.validate(record(telemetry_id="A"), NOW)
    b, _ = rules.validate(record(telemetry_id="B"), NOW)
    a2, _ = rules.validate(record(telemetry_id="A", metrics={"engine_temp_c": 1.0}), NOW)
    unique, dups = rules.deduplicate([a, b, a2])
    assert [r.telemetry_id for r in unique] == ["A", "B"]
    assert dups == [a2]


def test_location_and_database_guard():
    assert rules.s3a_to_location("s3a://bronze/telemetry/2026/10/x.json") == "s3://bronze/telemetry/2026/10/x.json"
    rules.assert_safe_database("aircraft_predict_db", "aircraft_predict_db", {"aircraft_app"})
    with pytest.raises(RuntimeError, match="protected"):
        rules.assert_safe_database("aircraft_app", "aircraft_app", {"aircraft_app"})
    with pytest.raises(RuntimeError, match="APP_DB_NAME"):
        rules.assert_safe_database("aircraft_fde", "aircraft_predict_db", {"aircraft_app"})
