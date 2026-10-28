import datetime as dt

import pandas as pd

from afp_ml.config import Settings
from afp_ml.risk import PredictionState, build_descriptive_predictions, descriptive_category, model_category

S = Settings()  # elevated 2x / high 5x baseline, min counts 3 / 10, min readings 36
BASE = {"anomaly_rate": 0.01, "high_rate": 0.001}


def cat(counts, readings, recent=True):
    return descriptive_category(counts, readings, recent, BASE, S)[0]


def test_descriptive_rules():
    assert cat({}, 1000, recent=False) == "Undetermined"
    assert cat({"High": 50}, 1000, recent=False) == "Undetermined"
    assert cat({}, 10) == "Undetermined"                   # too few readings
    assert cat({}, 1000) == "Low"
    assert cat({"Low": 10}, 1000) == "Low"                 # 1x baseline = what IF flags on normal data
    assert cat({"Low": 25}, 1000) == "Elevated"            # 2.5x
    assert cat({"Low": 60}, 1000) == "High"                # 6x
    assert cat({"High": 3}, 1000) == "Elevated"            # High-severity rate 3x its baseline
    assert cat({"High": 6, "Low": 4}, 1000) == "High"      # High-severity rate 6x, 10 anomalies
    assert cat({"Low": 2}, 40) == "Low"                    # 5x rate but below the minimum count for Elevated


def test_build_descriptive_predictions():
    now = dt.datetime(2026, 3, 1, tzinfo=dt.UTC)
    aircraft = pd.DataFrame({"aircraft_id": ["a", "b", "c"], "registration_number": ["A", "B", "C"],
                             "last_telemetry_at_utc": pd.to_datetime(
                                 [now - dt.timedelta(hours=1), now - dt.timedelta(days=10), None], utc=True)})
    counts = pd.DataFrame({"aircraft_id": ["a", "b"], "severity": ["Medium", "High"], "count": [30, 4]})
    readings = pd.DataFrame({"aircraft_id": ["a", "b"], "readings": [1000, 1000]})
    rows = {r["aircraft_id"]: r for r in build_descriptive_predictions(
        aircraft, counts, readings, now=now, settings=S, baseline=BASE, model_version_id="m", pipeline_run_id="r")}
    assert rows["a"]["risk_category"] == "Elevated"
    assert rows["b"]["risk_category"] == "Undetermined"  # stale telemetry
    assert rows["c"]["risk_category"] == "Undetermined"  # no telemetry
    for r in rows.values():
        assert r["basis"] == "DescriptiveIndicator" and r["probability"] is None and r["probability_validated"] is False
        assert r["horizon_days"] == 30 and r["approval_status"] == "PendingReview" and r["actual_outcome"] == "Unknown"
        assert r["model_version_id"] == "m" and r["pipeline_run_id"] == "r"
    snap = rows["a"]["feature_snapshot"]
    assert snap["anomaly_counts"] == {"Low": 0, "Medium": 30, "High": 0}
    assert snap["readings_in_window"] == 1000 and abs(snap["anomaly_ratio"] - 3.0) < 1e-9


def test_open_forecast_incident_sets_critical_or_high_critical():
    now = dt.datetime(2026, 3, 1, tzinfo=dt.UTC)
    recent = now - dt.timedelta(hours=1)
    aircraft = pd.DataFrame({"aircraft_id": ["a", "b", "c", "d"], "registration_number": ["A", "B", "C", "D"],
                             "last_telemetry_at_utc": pd.to_datetime([recent, recent, recent, None], utc=True)})
    readings = pd.DataFrame({"aircraft_id": ["a", "b", "c"], "readings": [1000, 1000, 1000]})
    empty = pd.DataFrame(columns=["aircraft_id", "severity", "count"])
    conditions = {"a": {"EngineTemperature"}, "b": {"EngineTemperature", "OilPressure"}, "d": {"EngineTemperature"}}
    rows = {r["aircraft_id"]: r for r in build_descriptive_predictions(
        aircraft, empty, readings, now=now, settings=S, baseline=BASE, model_version_id="m", pipeline_run_id="r",
        forecast_conditions=conditions)}
    assert rows["a"]["risk_category"] == "Critical"
    assert rows["b"]["risk_category"] == "HighCritical"
    assert rows["c"]["risk_category"] == "Low"
    assert rows["d"]["risk_category"] == "Undetermined"          # no recent telemetry: nothing is asserted
    assert rows["b"]["feature_snapshot"]["rule"] == "descriptive_forecast_condition_v1"
    assert rows["b"]["feature_snapshot"]["forecast_breached_measurements"] == ["EngineTemperature", "OilPressure"]
    assert rows["c"]["feature_snapshot"]["rule"] == "descriptive_anomaly_rate_v1"


def test_model_category():
    th = {"high": 0.4, "elevated": 0.2}
    assert model_category(0.5, th, True) == "High"
    assert model_category(0.25, th, True) == "Elevated"
    assert model_category(0.1, th, True) == "Low"
    assert model_category(0.9, th, False) == "Undetermined"


def test_descriptive_prediction_state_and_cutoff():
    now = dt.datetime(2026, 3, 1, 10, 37, tzinfo=dt.UTC)
    last_a = now - dt.timedelta(minutes=5)
    last_b = now - dt.timedelta(days=10)
    aircraft = pd.DataFrame({"aircraft_id": ["a", "b", "c"], "registration_number": ["A", "B", "C"],
                             "last_telemetry_at_utc": pd.to_datetime([last_a, last_b, None], utc=True)})
    counts = pd.DataFrame({"aircraft_id": ["a"], "severity": ["Low"], "count": [5]})
    readings = pd.DataFrame({"aircraft_id": ["a", "b"], "readings": [1000, 1000]})
    rows = {r["aircraft_id"]: r for r in build_descriptive_predictions(
        aircraft, counts, readings, now=now, settings=S, baseline=BASE, model_version_id="m", pipeline_run_id="r")}
    assert rows["a"]["prediction_state"] == PredictionState.SCORED.value == "Scored"
    assert rows["a"]["feature_cutoff_utc"] == last_a
    assert rows["b"]["prediction_state"] == "InsufficientTelemetry" and rows["b"]["feature_cutoff_utc"] == last_b
    assert rows["c"]["prediction_state"] == "InsufficientTelemetry"
    assert rows["c"]["feature_cutoff_utc"] == dt.datetime(2026, 3, 1, 10, 0, tzinfo=dt.UTC)
