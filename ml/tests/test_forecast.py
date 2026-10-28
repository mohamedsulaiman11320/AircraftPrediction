"""Sensor forecaster: method, honest validation, forecast points and jobs (synthetic TEST data only)."""
import datetime as dt
from dataclasses import replace

import numpy as np
import pandas as pd
import pytest

from afp_ml import forecast as fc
from afp_ml.config import Settings
from afp_ml.features import PSI_TO_KPA
from afp_ml.jobs import execute_job
from afp_ml.registry import validate_for_registration
from conftest import FakeRepository, make_services

UTC = dt.timezone.utc
START = pd.Timestamp("2026-01-01", tz="UTC")


def telemetry(days=150, aircraft=("A1", "A2"), kind="daily", seed=7):
    """kind='daily': stable level + daily cycle + noise (forecastable). kind='walk': random walk (persistence optimal)."""
    rng = np.random.default_rng(seed)
    rows = []
    t = pd.date_range(START, periods=days * 24, freq="h", tz="UTC")
    for a in aircraft:
        if kind == "daily":
            temp = 600 + 40 * np.sin(2 * np.pi * t.hour / 24) + rng.normal(0, 15, len(t))
            psi = 45 + 3 * np.cos(2 * np.pi * t.hour / 24) + rng.normal(0, 1.5, len(t))
            vib = 2 + 0.3 * np.sin(2 * np.pi * t.hour / 24) + rng.normal(0, 0.1, len(t))
        else:
            temp = 600 + np.cumsum(rng.normal(0, 5, len(t)))
            psi = 45 + np.cumsum(rng.normal(0, 0.5, len(t)))
            vib = 2 + np.cumsum(rng.normal(0, 0.05, len(t)))
        for i, ts in enumerate(t):
            rows.append((a, ts, temp[i], psi[i] * PSI_TO_KPA, vib[i], "Valid"))
    return pd.DataFrame(rows, columns=["aircraft_id", "recorded_at_utc", "temperature_c", "pressure_kpa", "vibration_index", "quality_status"])


def test_hourly_frame_keeps_valid_readings_and_converts_pressure_to_psi():
    tel = telemetry(days=2, aircraft=("A1",))
    tel.loc[0, "quality_status"] = "Suspect"
    frame = fc.hourly_frame(tel)["A1"]
    assert frame.index.min() == tel.iloc[1]["recorded_at_utc"]           # the Suspect first reading is not used
    assert frame.iloc[0]["pressure_psi"] == pytest.approx(tel.iloc[1]["pressure_kpa"] / PSI_TO_KPA)


def test_level_plus_profile_reproduces_a_clean_daily_pattern():
    idx = pd.date_range(START, periods=40 * 24, freq="h", tz="UTC")
    series = pd.Series(100 + 10 * np.sin(2 * np.pi * idx.hour / 24), index=idx)
    origin = idx[-1]
    pred = fc.fit_point_forecast(series, origin, 48)
    expected = 100 + 10 * np.sin(2 * np.pi * (origin + pd.to_timedelta(np.arange(1, 49), unit="h")).hour / 24)
    assert np.allclose(pred, expected, atol=0.5)


def test_validation_supports_a_forecastable_series_with_out_of_sample_interval_coverage():
    bt = fc.backtest(fc.hourly_frame(telemetry(kind="daily")), fc.ValidationCriteria())
    for horizon in ("OneDay", "SevenDays"):
        r = bt["results"][horizon]["temperatureC"]
        assert r["supported"] and r["mae_ratio_vs_best_naive"] <= 0.95
        assert 0.70 <= r["interval_coverage"] <= 0.90
        assert r["fit_origins"] > 0 and r["eval_origins"] >= 5          # intervals fitted and judged on different origins
    assert "temperatureC" in bt["supported"]["OneDay"]


def test_validation_refuses_a_random_walk_where_persistence_is_optimal():
    bt = fc.backtest(fc.hourly_frame(telemetry(kind="walk")), fc.ValidationCriteria())
    for horizon in ("OneDay", "SevenDays"):
        for metric in fc.METRICS:
            assert bt["results"][horizon][metric]["supported"] is False
    assert not bt["supported"].get("OneDay")


def test_too_little_history_supports_nothing():
    bt = fc.backtest(fc.hourly_frame(telemetry(days=40)), fc.ValidationCriteria())
    assert bt["supported"].get("ThirtyDays") is None
    assert all(not v["supported"] for v in bt["results"]["ThirtyDays"].values())


def test_forecast_points_are_strictly_after_the_last_reading_and_only_for_validated_metrics():
    frame = fc.hourly_frame(telemetry(days=40, aircraft=("A1",)))["A1"]
    q = {"OneDay": {"temperatureC": {"1-24": [-20.0, 20.0]}}}
    anchor, points = fc.forecast_points(frame, "OneDay", ["temperatureC"], q)
    assert anchor == frame.index.max() and len(points) == 24
    assert all(pd.Timestamp(p["timestampUtc"]) > anchor for p in points)
    assert all(isinstance(p["temperatureC"], float) for p in points)
    assert all(p["temperatureCLow"] == pytest.approx(p["temperatureC"] - 20, abs=1e-3) for p in points)
    assert all(p["pressurePsi"] is None and p["vibrationIndex"] is None for p in points)   # not validated: no value


@pytest.fixture
def svc(tmp_path):
    repo = FakeRepository(telemetry=telemetry(kind="daily"))
    return make_services(repo, Settings(model_dir=tmp_path / "models"), dt.datetime(2026, 6, 1, tzinfo=UTC))


def test_training_records_a_trained_candidate_with_its_validation_evidence(svc):
    out = execute_job(svc, "TrainSensorForecast", {})
    assert out.status == "Succeeded", out.error
    m = next(m for m in svc.repo.models if m["model_type"] == "SensorForecast")
    assert m["status"] == "Trained"
    assert m["metrics"]["supported_horizons"]["OneDay"]
    assert m["metrics"]["validation_criteria"]["max_mae_ratio_vs_best_naive"] == 0.95
    assert validate_for_registration({**m, "status": "Trained"}) == []
    assert svc.store.load_verified(m["artifact_path"], m["artifact_sha256"])["kind"] == "SensorForecast"


def test_a_forecaster_with_no_supported_horizon_cannot_be_registered(tmp_path):
    repo = FakeRepository(telemetry=telemetry(kind="walk"))
    svc = make_services(repo, Settings(model_dir=tmp_path / "models"), dt.datetime(2026, 6, 1, tzinfo=UTC))
    assert execute_job(svc, "TrainSensorForecast", {}).status == "Succeeded"       # the evidence is still recorded
    m = repo.models[-1]
    assert any("no forecast horizon passed validation" in p for p in validate_for_registration(m))


def test_forecasts_are_written_only_by_the_active_model_and_idempotently(svc):
    nothing = execute_job(svc, "ForecastSensors", {})
    assert nothing.status == "Succeeded" and "No Active SensorForecast model" in nothing.result.message
    assert not getattr(svc.repo, "sensor_forecasts", {})

    execute_job(svc, "TrainSensorForecast", {})
    trained = svc.repo.models[-1]
    trained["status"] = "Active"                                                    # approval is the API's job
    first = execute_job(svc, "ForecastSensors", {})
    assert first.status == "Succeeded" and first.result.records_processed > 0
    rows = list(svc.repo.sensor_forecasts.values())
    assert {r["model_version_id"] for r in rows} == {trained["id"]}
    for r in rows:
        assert set(r["horizon"] for r in rows) <= set(trained["metrics"]["supported_horizons"])
        unsupported = set(fc.METRICS) - set(trained["metrics"]["supported_horizons"][r["horizon"]])
        assert all(p[m] is None for p in r["points"] for m in unsupported)
        assert all(pd.Timestamp(p["timestampUtc"]) > pd.Timestamp(r["data_as_of_utc"]) for p in r["points"])
    again = execute_job(svc, "ForecastSensors", {})
    assert again.result.records_processed == 0                                       # same data -> nothing new


def test_a_tampered_artifact_is_refused(svc):
    execute_job(svc, "TrainSensorForecast", {})
    m = svc.repo.models[-1]
    m["status"], m["artifact_sha256"] = "Active", "0" * 64
    out = execute_job(svc, "ForecastSensors", {})
    assert out.status == "Failed"
    assert not getattr(svc.repo, "sensor_forecasts", {})


def test_settings_carry_the_forecast_gates(monkeypatch):
    monkeypatch.setenv("AFP_FORECAST_MAX_MAE_RATIO", "0.9")
    s = Settings.from_env()
    assert s.forecast_max_mae_ratio == 0.9 and replace(s).forecast_coverage_low == 0.70
