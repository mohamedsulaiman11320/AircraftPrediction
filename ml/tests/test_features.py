import numpy as np
import pandas as pd
import pytest

from afp_ml.anomaly import build_pipeline
from afp_ml.features import anomaly_feature_names, build_anomaly_features
from conftest import make_telemetry


def _one_series(values, start="2026-01-01T00:00:00Z"):
    n = len(values)
    return pd.DataFrame({
        "id": np.arange(1, n + 1), "aircraft_id": "a1", "sensor_id": "S0",
        "recorded_at_utc": pd.date_range(start, periods=n, freq="10min", tz="UTC"),
        "temperature_c": values, "pressure_kpa": np.full(n, 300.0),
    })


def test_rolling_features_use_previous_readings_only():
    vals = [10.0, 11.0, 12.0, 13.0, 14.0, 100.0]
    f = build_anomaly_features(_one_series(vals), window=3)
    # Row 5 (value 100): rolling mean of the PREVIOUS three readings (12, 13, 14) = 13.
    assert f.loc[5, "temperature_c_roll_mean_3"] == pytest.approx(13.0)
    assert f.loc[5, "temperature_c_dev_roll_mean_3"] == pytest.approx(87.0)
    assert f.loc[5, "temperature_c_diff1"] == pytest.approx(86.0)
    # First row has no history: features are NaN (imputed later), never filled with future data.
    assert np.isnan(f.loc[0, "temperature_c_diff1"]) and np.isnan(f.loc[0, "temperature_c_roll_mean_3"])
    assert f.loc[1, "minutes_since_prev"] == pytest.approx(10.0)


def test_future_rows_do_not_change_past_features():
    tel = make_telemetry(n_aircraft=2, sensors=2, days=1)
    cut = tel["recorded_at_utc"].quantile(0.5)
    past = tel[tel["recorded_at_utc"] <= cut]
    names = anomaly_feature_names(6)
    f_past = build_anomaly_features(past, 6).set_index("id")[names]
    f_all = build_anomaly_features(tel, 6).set_index("id")[names].loc[f_past.index]
    pd.testing.assert_frame_equal(f_past, f_all)


def test_series_are_separated_by_aircraft_and_sensor():
    a = _one_series([1.0, 2.0, 3.0])
    b = _one_series([100.0, 200.0, 300.0])
    b["sensor_id"] = "S1"
    b["id"] += 10
    f = build_anomaly_features(pd.concat([a, b]), window=2).set_index("id")
    assert f.loc[11, "temperature_c_diff1"] != f.loc[11, "temperature_c_diff1"]  # NaN: first of its own series
    assert f.loc[2, "temperature_c_diff1"] == pytest.approx(1.0)


def test_missing_values_handled_by_pipeline_with_train_statistics():
    tel = make_telemetry(n_aircraft=1, sensors=1, days=2)
    tel.loc[tel.index[::7], "pressure_kpa"] = np.nan
    names = anomaly_feature_names(6)
    X = build_anomaly_features(tel, 6)[names].to_numpy("float64")
    pipe = build_pipeline(0.01, 20, 0).fit(X)
    imputer = pipe.named_steps["imputer"]
    # Median learned on training data only, and an indicator column added for features with missing values.
    assert imputer.statistics_[names.index("pressure_kpa")] == pytest.approx(np.nanmedian(X[:, names.index("pressure_kpa")]))
    assert imputer.indicator_ is not None and len(imputer.indicator_.features_) > 0
    row = X[:1].copy()
    row[0, :] = np.nan  # an all-missing row is still scorable
    assert np.isfinite(pipe.score_samples(row)).all()
