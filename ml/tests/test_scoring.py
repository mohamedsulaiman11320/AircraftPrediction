import numpy as np
import pandas as pd

from afp_ml.anomaly import score_features, severity_for, severity_thresholds, train_anomaly_model
from afp_ml.features import build_anomaly_features
from afp_ml.jobs import execute_job
from afp_ml.scoring import anomaly_rows, select_new_rows
from conftest import FakeRepository, T0, make_services, make_telemetry


def _train(tel):
    return train_anomaly_model(tel, include_suspect=False, contamination=0.01, n_estimators=100, window=6,
                               random_state=0)


def test_injected_outliers_score_higher():
    tel = make_telemetry(n_aircraft=2, sensors=2, days=3)
    result = _train(tel)
    clean = make_telemetry(n_aircraft=2, sensors=2, days=0.5, start=T0 + pd.Timedelta(days=3), first_id=10_000, seed=5)
    new = clean.copy()
    spikes = new.index[[20, 60, 100]]
    new.loc[spikes, "temperature_c"] += 60.0
    new.loc[spikes, "pressure_kpa"] -= 80.0
    feats = build_anomaly_features(new, 6)
    assert feats["warmup"].sum() == 4 * 3  # first min_periods readings of each of the 4 series
    scored = score_features(result.bundle, feats.loc[~feats["warmup"]]).set_index("id")
    clean_feats = build_anomaly_features(clean, 6)
    clean_scored = score_features(result.bundle, clean_feats.loc[~clean_feats["warmup"]]).set_index("id")
    spike_ids = new.loc[spikes, "id"]
    # Each injected outlier scores higher than the same reading without the excursion, and higher
    # than 95% of normal readings.
    assert (scored.loc[spike_ids, "anomaly_score"] > clean_scored.loc[spike_ids, "anomaly_score"] + 0.05).all()
    assert scored.loc[spike_ids, "anomaly_score"].min() > clean_scored["anomaly_score"].quantile(0.95)
    assert scored.loc[spike_ids, "is_anomaly"].all()
    assert clean_scored["is_anomaly"].mean() < 0.05
    assert set(scored.loc[spike_ids, "severity"]) <= {"Low", "Medium", "High"}
    assert scored.loc[~scored["is_anomaly"], "severity"].isna().all()


def test_metrics_are_distribution_not_accuracy():
    result = _train(make_telemetry(n_aircraft=1, sensors=2, days=2))
    m = result.metrics
    assert "accuracy" not in m and m["metric_type"] == "unsupervised_training_distribution"
    assert m["contamination"] == 0.01 and m["training_rows"] == result.training_rows
    assert set(m["severity_thresholds"]) >= {"low", "medium", "high"}


def test_severity_mapping():
    scores = np.linspace(0, 1, 10_001)
    th = severity_thresholds(scores, anomaly_threshold=0.99, contamination=0.01)
    assert th["low"] <= th["medium"] <= th["high"]
    assert severity_for(th["low"] + 1e-9, th) == "Low"
    assert severity_for(th["medium"], th) == "Medium"
    assert severity_for(th["high"] + 0.001, th) == "High"
    # Levels never decrease even if the quantiles fall below the decision cut.
    th2 = severity_thresholds(scores, anomaly_threshold=2.0, contamination=0.01)
    assert th2["low"] == th2["medium"] == th2["high"] == 2.0


def test_only_anomalies_are_persisted():
    scored = pd.DataFrame({"id": [1, 2, 3], "aircraft_id": "a", "sensor_id": ["S0", None, "S0"],
                           "recorded_at_utc": pd.date_range(T0, periods=3, freq="10min", tz="UTC"),
                           "f": [1.0, np.nan, 3.0], "anomaly_score": [0.1, 0.9, 0.95],
                           "is_anomaly": [False, True, True], "severity": [None, "Low", "High"]})
    rows = anomaly_rows(scored, ["f"], model_version_id="m", run_id="r", scored_at=T0)
    assert [r["telemetry_record_id"] for r in rows] == [2, 3]
    assert rows[0]["feature_values"] == {"f": None} and rows[0]["sensor_id"] is None
    assert all(r["investigation_status"] == "New" and r["is_anomaly"] for r in rows)


def test_select_new_rows_watermark_includes_skipped_rows():
    tel = make_telemetry(n_aircraft=1, sensors=1, days=0.1)
    tel.loc[tel.index[-1], "quality_status"] = "Suspect"
    eligible, wm = select_new_rows(tel, ["Valid"])
    assert wm == tel["id"].max() and len(eligible) == len(tel) - 1


def _setup_active_model(settings, tel, now):
    repo = FakeRepository(telemetry=tel)
    svc = make_services(repo, settings, now)
    out = execute_job(svc, "TrainAnomaly", {"include_suspect": False})
    assert out.status == "Succeeded", out.error
    model_id = out.result.model_version_id
    assert repo.models[0]["status"] == "Trained"
    repo.activate(model_id)  # in production: register -> approve (Leadership) -> activate (Data Engineer) via API
    return repo, svc, model_id


def test_score_job_requires_active_model(settings):
    repo = FakeRepository(telemetry=make_telemetry(n_aircraft=1, days=1))
    out = execute_job(make_services(repo, settings), "Score", {})
    assert out.status == "Failed" and "No Active AnomalyDetection model" in out.error
    assert next(iter(repo.runs.values()))["status"] == "Failed"


def test_score_job_watermark_and_idempotency(settings):
    tel = make_telemetry(n_aircraft=2, sensors=2, days=3)
    now = (T0 + pd.Timedelta(days=3, hours=12)).to_pydatetime()
    repo, svc, model_id = _setup_active_model(settings, tel, now)

    first = execute_job(svc, "Score", {})
    assert first.status == "Succeeded", first.error
    s1 = first.result.summary
    assert s1["eligible_rows"] == len(tel) and s1["max_telemetry_record_id"] == tel["id"].max()
    assert s1["skipped_rows_insufficient_history"] == 4 * 3 and s1["scored_rows"] == len(tel) - 12
    assert s1["anomalies_inserted"] == len(repo.anomalies) > 0
    assert all(a["model_version_id"] == model_id for a in repo.anomalies)
    assert len(repo.predictions) == 2  # one descriptive indicator per aircraft
    p = repo.predictions[0]
    assert p["basis"] == "DescriptiveIndicator" and p["probability"] is None and not p["probability_validated"]
    assert p["approval_status"] == "PendingReview" and p["actual_outcome"] == "Unknown"
    assert p["horizon_days"] == svc.settings.prediction_horizon_days and p["model_version_id"] == model_id
    run = repo.runs[first.run_id]
    assert run["status"] == "Succeeded" and run["model_version_id"] == model_id and run["trigger"] == "Cli"

    second = execute_job(svc, "Score", {})
    assert second.result.summary["eligible_rows"] == 0
    assert second.result.summary["watermark_start"] == tel["id"].max()

    new = make_telemetry(n_aircraft=2, sensors=2, days=0.25, start=T0 + pd.Timedelta(days=3),
                         first_id=int(tel["id"].max()) + 1)
    new.loc[new.index[5], "temperature_c"] += 80
    repo.telemetry = pd.concat([tel, new], ignore_index=True)
    third = execute_job(svc, "Score", {})
    assert third.result.summary["eligible_rows"] == len(new)
    assert third.result.summary["skipped_rows_insufficient_history"] == 0  # history comes from the context window
    assert any(a["telemetry_record_id"] == int(new.loc[new.index[5], "id"]) for a in repo.anomalies)

    before = len(repo.anomalies)
    full = execute_job(svc, "Score", {"full": True})
    assert full.result.summary["eligible_rows"] == len(repo.telemetry)
    assert full.result.summary["anomalies_inserted"] == 0 and len(repo.anomalies) == before  # ON CONFLICT DO NOTHING


def test_score_does_not_persist_telemetry_anomalies_by_default(settings):
    from dataclasses import replace
    tel = make_telemetry(n_aircraft=1, sensors=1, days=3)
    tel.loc[tel.index[-5], "temperature_c"] += 80
    repo, svc, _ = _setup_active_model(replace(settings, persist_telemetry_anomalies=False), tel,
                                       (T0 + pd.Timedelta(days=3)).to_pydatetime())
    out = execute_job(svc, "Score", {})
    assert out.status == "Succeeded", out.error
    assert out.result.summary["anomalies_flagged"] > 0
    assert out.result.summary["anomalies_inserted"] == 0 and repo.anomalies == []


def test_score_refuses_tampered_active_artifact(settings):
    tel = make_telemetry(n_aircraft=1, sensors=1, days=3)
    repo, svc, _ = _setup_active_model(settings, tel, (T0 + pd.Timedelta(days=3)).to_pydatetime())
    repo.models[0]["artifact_sha256"] = "f" * 64
    out = execute_job(svc, "Score", {})
    assert out.status == "Failed" and "SHA-256 mismatch" in out.error
    assert repo.anomalies == []


def test_fake_risk_insert_is_idempotent():
    repo = FakeRepository()
    row = {"id": "1", "aircraft_id": "a", "basis": "DescriptiveIndicator", "model_version_id": "m",
           "feature_cutoff_utc": T0.to_pydatetime(), "horizon_days": 30, "probability": None,
           "probability_validated": False, "prediction_state": "Scored"}
    assert repo.insert_risk_predictions([row]) == 1
    assert repo.insert_risk_predictions([{**row, "id": "2"}]) == 0
    assert repo.insert_risk_predictions([{**row, "id": "3", "horizon_days": 14}]) == 1
    assert len(repo.predictions) == 2


def test_score_risk_rows_carry_state_and_cutoff_and_are_idempotent(settings):
    tel = make_telemetry(n_aircraft=2, sensors=2, days=3)
    now = (T0 + pd.Timedelta(days=3, hours=1, minutes=20)).to_pydatetime()
    repo, svc, _ = _setup_active_model(settings, tel, now)
    first = execute_job(svc, "Score", {})
    assert first.result.summary["predictions_inserted"] == 2
    last = tel.groupby("aircraft_id")["recorded_at_utc"].max()
    for p in repo.predictions:
        assert p["prediction_state"] == "Scored"
        assert p["feature_cutoff_utc"] == last[p["aircraft_id"]].to_pydatetime()
    again = execute_job(svc, "Score", {})
    assert again.result.summary["predictions_inserted"] == 0
    assert again.result.summary["predictions_already_present"] == 2
    assert len(repo.predictions) == 2


def test_anomaly_artifact_metadata_has_traceability(settings):
    import json

    tel = make_telemetry(n_aircraft=1, sensors=1, days=3)
    repo, svc, _ = _setup_active_model(settings, tel, (T0 + pd.Timedelta(days=3)).to_pydatetime())
    meta = json.loads((svc.store.root / repo.models[0]["artifact_path"]).with_name("metadata.json").read_text("utf-8"))
    assert meta["dependency_versions"]["numpy"] and "psycopg" in meta["dependency_versions"]
    assert meta["feature_schema_version"] and "label_definition" not in meta
    assert meta["training_window"] == {"from_utc": meta["training_data_from_utc"], "to_utc": meta["training_data_to_utc"]}
