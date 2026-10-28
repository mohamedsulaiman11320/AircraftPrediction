"""Failure-model training on a small SYNTHETIC labelled dataset (tests only - never shipped as data)."""

import json

import numpy as np
import pandas as pd
import pytest

from afp_ml.audit import compute_readiness
from afp_ml.failure import evaluate_binary, false_alerts_per_aircraft_period, train_failure_model
from afp_ml.features import FEATURE_SCHEMA_VERSION, chronological_split
from afp_ml.jobs import execute_job
from afp_ml.registry import validate_for_registration
from conftest import FakeRepository, T0, failure_settings, make_failure_dataset, make_services, make_telemetry


@pytest.fixture(scope="module")
def dataset():
    return make_failure_dataset(n_aircraft=8, days=400)


def test_evaluate_binary_metrics():
    y = np.array([0, 0, 0, 1, 1, 0, 1, 0])
    p = np.array([0.1, 0.2, 0.6, 0.7, 0.9, 0.1, 0.3, 0.05])
    m = evaluate_binary(y, p, 0.5)
    assert m["precision"] == pytest.approx(2 / 3) and m["recall"] == pytest.approx(2 / 3)
    assert m["false_alert_rate"] == pytest.approx(1 / 5)
    assert m["brier"] == pytest.approx(np.mean((p - y) ** 2))
    assert 0 < m["pr_auc"] <= 1
    assert sum(b["count"] for b in m["reliability_bins"]) == len(y)
    assert m["specificity"] == pytest.approx(4 / 5)          # tn 4 / (tn 4 + fp 1)
    assert 0 < m["roc_auc"] <= 1


def test_roc_auc_is_not_reported_for_a_single_class_split():
    m = evaluate_binary(np.array([1, 1, 1]), np.array([0.2, 0.6, 0.9]), 0.5)
    assert m["roc_auc"] is None and m["specificity"] is None


def test_train_failure_model_produces_all_metrics(dataset, settings):
    tel, mlog = dataset
    s = failure_settings(settings)
    readiness, labeled, _ = compute_readiness(tel, mlog, s, (T0 + pd.Timedelta(days=420)).to_pydatetime())
    assert readiness["status"] == "Ready"
    split = chronological_split(labeled, purge_gap_days=s.prediction_horizon_days)
    result = train_failure_model(split, horizon_days=s.prediction_horizon_days, random_state=0,
                                 calibration_max_ece=s.calibration_max_ece,
                                 calibration_min_test_positives=s.calibration_min_test_positives)
    m = result.metrics
    for key in ("precision", "recall", "pr_auc", "false_alert_rate", "brier"):
        assert m[key] is not None and np.isfinite(m[key]), key
    assert set(m["candidates"]) == {"random_forest", "logistic_regression", "hist_gradient_boosting"}
    for cand in m["candidates"].values():
        assert cand["validation_uncalibrated"]["pr_auc"] is not None
    # The approved specification is always the deployed model; the others are comparison only.
    assert m["selected_candidate"] == "random_forest" and m["selection_metric"] == "approved_specification"
    params = m["candidates"]["random_forest"]["params"]
    assert (params["n_estimators"], params["max_depth"], params["class_weight"]) == (300, 10, "balanced")
    assert params["random_state"] == 0   # the caller's seed (production default AFP_RANDOM_SEED=42)
    assert result.algorithm == "RandomForest"
    imp = m["feature_importance"]
    assert {r["feature"] for r in imp} == set(result.feature_names)
    assert sum(r["importance"] for r in imp) == pytest.approx(1.0)
    assert isinstance(m["calibration_validated"], bool)
    assert m["calibration_validated"] == all(m["calibration_checks"].values())
    assert len(m["test"]["reliability_bins"]) == 10
    # The signal is real in this synthetic set, so the model must beat the prevalence baseline.
    assert m["pr_auc"] > m["test"]["prevalence"]
    # Baseline comparison and alert-burden metrics.
    assert m["pr_auc_baseline"] == m["test"]["prevalence"]
    assert m["pr_auc_lift"] == pytest.approx(m["pr_auc"] / m["test"]["prevalence"])
    assert m["baseline"]["model"] == "DummyClassifier(strategy='prior')"
    for part in ("validation", "test"):
        b = m["baseline"][part]
        assert set(b) >= {"pr_auc", "precision", "recall", "prevalence", "positives"}
        assert b["pr_auc"] == pytest.approx(b["prevalence"])  # a constant score has PR-AUC = prevalence
    assert m["beats_baseline"] is (m["pr_auc"] > m["baseline"]["test"]["pr_auc"])
    assert m["beats_baseline"] is True
    test_days = (split.test["cutoff_utc"].max() - split.test["cutoff_utc"].min()).days + 1
    expected = m["test"]["confusion_matrix"]["fp"] / (split.test["aircraft_id"].nunique() * test_days / 30)
    assert m["false_alerts_per_aircraft_per_30_days"] == pytest.approx(expected)
    for key in ("train_positives", "validation_positives", "test_positives"):
        assert m["split"][key] > 0
    assert result.algorithm in ("RandomForest", "LogisticRegression", "HistGradientBoosting")


def test_train_failure_job_blocked_creates_no_model(settings):
    repo = FakeRepository(telemetry=make_telemetry(n_aircraft=2, sensors=1, days=20, interval_minutes=60))
    out = execute_job(make_services(repo, settings), "TrainFailure", {"target": "failure"})
    assert out.status == "Blocked" and out.exit_code == 0
    assert repo.models == []
    run = repo.runs[out.run_id]
    assert run["status"] == "Blocked"
    assert run["result_summary"]["failure_prediction_readiness"]["reasons"]
    assert "BLOCKED" in run["error_message"]


def test_train_failure_job_ready_and_register(dataset, settings):
    tel, mlog = dataset
    s = failure_settings(settings)
    repo = FakeRepository(telemetry=tel, mlog=mlog)
    svc = make_services(repo, s, (T0 + pd.Timedelta(days=420)).to_pydatetime())
    out = execute_job(svc, "TrainFailure", {"target": "failure"})
    assert out.status == "Succeeded", out.error
    model = repo.models[0]
    assert model["status"] == "Trained" and model["model_type"] == "FailurePrediction"
    assert model["prediction_horizon_days"] == 14 and model["limitations"]
    assert model["training_run_id"] == out.run_id
    assert validate_for_registration(model) == []
    meta = json.loads((svc.store.root / model["artifact_path"]).with_name("metadata.json").read_text("utf-8"))
    assert set(meta["dependency_versions"]) == {"python", "numpy", "pandas", "scikit-learn", "scipy", "joblib",
                                                "psycopg"}
    assert meta["dependency_versions"]["scikit-learn"] and meta["dependency_versions"]["python"]
    assert meta["feature_schema_version"] == FEATURE_SCHEMA_VERSION
    assert meta["label_definition"]["event_type"] == "Unscheduled" and meta["label_definition"]["is_verified"]
    assert meta["label_definition"]["definition_confirmed"] is True and meta["label_definition"]["horizon_days"] == 14
    assert meta["label_definition"]["require_confirmed_failure"] is False
    assert meta["label_definition"]["failure_codes"] == []
    assert meta["training_window"]["from_utc"] and meta["training_window"]["to_utc"]
    assert model["metrics"]["label_definition"] == meta["label_definition"]

    reg = execute_job(svc, "RegisterModel", {"model_version_id": model["id"]})
    assert reg.status == "Succeeded", reg.error
    assert model["status"] == "Registered" and model["registered_at_utc"] is not None
    again = execute_job(svc, "RegisterModel", {"model_version_id": model["id"]})
    assert again.status == "Failed" and "only 'Trained'" in again.error


def test_register_refuses_tampered_artifact(settings):
    repo = FakeRepository(telemetry=make_telemetry(n_aircraft=1, sensors=1, days=3))
    svc = make_services(repo, settings)
    assert execute_job(svc, "TrainAnomaly", {}).status == "Succeeded"
    repo.models[0]["artifact_sha256"] = "0" * 64
    out = execute_job(svc, "RegisterModel", {"latest": True, "model_type": "AnomalyDetection"})
    assert out.status == "Failed" and "SHA-256 mismatch" in out.error
    assert repo.models[0]["status"] == "Trained"


def test_score_with_active_failure_model_uncalibrated_probability_is_null(dataset, settings):
    tel, mlog = dataset
    s = failure_settings(settings)
    now = (tel["recorded_at_utc"].max() + pd.Timedelta(hours=1)).to_pydatetime()
    repo = FakeRepository(telemetry=tel, mlog=mlog)
    svc = make_services(repo, s, now)
    assert execute_job(svc, "TrainAnomaly", {}).status == "Succeeded"
    assert execute_job(svc, "TrainFailure", {}).status == "Succeeded"
    for m in repo.models:
        repo.activate(m["id"])
    fm = next(m for m in repo.models if m["model_type"] == "FailurePrediction")
    fm["metrics"] = {**fm["metrics"], "calibration_validated": False}
    out = execute_job(svc, "Score", {})
    assert out.status == "Succeeded", out.error
    model_preds = [p for p in repo.predictions if p["basis"] == "Model"]
    assert len(model_preds) == 8
    assert all(p["probability"] is None and p["probability_validated"] is False for p in model_preds)
    assert all(p["model_version_id"] == fm["id"] for p in model_preds)
    assert {p["risk_category"] for p in model_preds} <= {"Low", "Elevated", "High", "Undetermined"}
    day_cutoff = pd.Timestamp(now).floor("D").to_pydatetime()
    assert all(p["prediction_state"] == "Scored" and p["feature_cutoff_utc"] == day_cutoff for p in model_preds)


def test_model_predictions_missing_features(dataset, settings):
    tel, mlog = dataset
    s = failure_settings(settings)
    now = (tel["recorded_at_utc"].max() + pd.Timedelta(hours=1, minutes=30)).to_pydatetime()
    repo = FakeRepository(telemetry=tel, mlog=mlog)
    repo.aircraft = pd.concat([repo.aircraft, pd.DataFrame({"aircraft_id": ["no-telemetry"],
                                                            "registration_number": ["T-99"]})], ignore_index=True)
    svc = make_services(repo, s, now)
    assert execute_job(svc, "TrainAnomaly", {}).status == "Succeeded"
    assert execute_job(svc, "TrainFailure", {}).status == "Succeeded"
    for m in repo.models:
        repo.activate(m["id"])
    assert execute_job(svc, "Score", {}).status == "Succeeded"
    hour_cutoff = pd.Timestamp(now).floor("h").to_pydatetime()
    missing = [p for p in repo.predictions if p["aircraft_id"] == "no-telemetry"]
    by_basis = {p["basis"]: p for p in missing}
    assert by_basis["Model"]["prediction_state"] == "MissingFeatures"
    assert by_basis["Model"]["probability"] is None and by_basis["Model"]["risk_category"] == "Undetermined"
    assert by_basis["Model"]["feature_cutoff_utc"] == hour_cutoff
    assert by_basis["DescriptiveIndicator"]["prediction_state"] == "InsufficientTelemetry"
    assert by_basis["DescriptiveIndicator"]["feature_cutoff_utc"] == hour_cutoff


def test_failure_metric_helpers_are_none_safe():
    m = evaluate_binary(np.array([0, 0, 0]), np.array([0.1, 0.2, 0.3]), 0.5)
    assert m["pr_auc"] is None and m["pr_auc_lift"] is None and m["pr_auc_baseline"] == 0.0
    assert false_alerts_per_aircraft_period(pd.DataFrame({"label": [0, 1]}), 3) is None
    assert false_alerts_per_aircraft_period(pd.DataFrame(columns=["aircraft_id", "cutoff_utc"]), 3) is None
    frame = pd.DataFrame({"aircraft_id": ["a", "a", "b"],
                          "cutoff_utc": pd.to_datetime(["2026-01-01", "2026-01-30", "2026-01-15"], utc=True)})
    assert false_alerts_per_aircraft_period(frame, 4) == pytest.approx(4 / (2 * 30 / 30))


# ---- development-only candidate scoring ------------------------------------------------------

def _trained_candidate(dataset, settings, environment):
    from dataclasses import replace
    tel, mlog = dataset
    s = replace(failure_settings(settings), environment=environment)
    now = (tel["recorded_at_utc"].max() + pd.Timedelta(days=5)).to_pydatetime()   # clock past the data end
    repo = FakeRepository(telemetry=tel, mlog=mlog)
    svc = make_services(repo, s, now)
    assert execute_job(svc, "TrainFailure", {}).status == "Succeeded"
    fm = next(m for m in repo.models if m["model_type"] == "FailurePrediction")
    return repo, svc, fm, tel


def test_candidate_scoring_is_refused_outside_the_development_environment(dataset, settings):
    repo, svc, fm, _ = _trained_candidate(dataset, settings, "operational")
    out = execute_job(svc, "ScoreCandidate", {"model_version_id": fm["id"]})
    assert out.status == "Failed" and "development environment" in out.error
    assert repo.predictions == []                     # nothing written


def test_candidate_scoring_scores_every_aircraft_as_of_the_last_telemetry_and_is_idempotent(dataset, settings):
    repo, svc, fm, tel = _trained_candidate(dataset, settings, "development")
    assert fm["status"] == "Trained"                  # never registered, approved or activated
    out = execute_job(svc, "ScoreCandidate", {"model_version_id": fm["id"]})
    assert out.status == "Succeeded", out.error
    preds = repo.predictions
    assert len(preds) == 8 and {p["aircraft_id"] for p in preds} == set(tel["aircraft_id"])
    cutoff = (tel["recorded_at_utc"].max().floor("D") + pd.Timedelta(days=1)).to_pydatetime()
    assert all(p["feature_cutoff_utc"] == cutoff and p["prediction_state"] == "Scored" for p in preds)
    # Recency is judged against the data end, so the clock running past it does not make everything stale.
    assert not any(p["risk_category"] == "Undetermined" for p in preds)
    assert all(p["feature_snapshot"]["environment"] == "development" for p in preds)
    for p in preds:
        assert (p["probability"] is not None) == p["probability_validated"]
        if p["probability"] is not None:
            assert 0.0 <= p["probability"] <= 1.0
    again = execute_job(svc, "ScoreCandidate", {"model_version_id": fm["id"]})
    assert again.status == "Succeeded" and again.result.summary["predictions_inserted"] == 0
    assert len(repo.predictions) == 8


def test_candidate_scoring_needs_a_failure_model(dataset, settings):
    repo, svc, _, _ = _trained_candidate(dataset, settings, "development")
    assert execute_job(svc, "ScoreCandidate", {}).status == "Failed"
    assert execute_job(svc, "ScoreCandidate", {"model_version_id": "no-such-model"}).status == "Failed"


def test_development_environment_cannot_target_the_operational_database():
    from afp_ml.config import ConfigError, Settings
    with pytest.raises(ConfigError):
        Settings.from_env({"DB_NAME": "aircraft_predict_db", "AFP_ENVIRONMENT": "development"})
    with pytest.raises(ConfigError):
        Settings.from_env({"DB_NAME": "aircraft_predict_db_synth", "AFP_ENVIRONMENT": "staging"})
    assert Settings.from_env({"DB_NAME": "aircraft_predict_db_synth",
                              "AFP_ENVIRONMENT": "development"}).environment == "development"
    assert Settings.from_env({"DB_NAME": "aircraft_predict_db"}).environment == "operational"


# ---- rolling-origin evaluation ---------------------------------------------------------------

def _labeled(dataset, settings):
    tel, mlog = dataset
    s = failure_settings(settings)
    _, labeled, _ = compute_readiness(tel, mlog, s, (T0 + pd.Timedelta(days=420)).to_pydatetime())
    return s, labeled


def test_rolling_origin_folds_never_train_on_or_near_their_test_block():
    from afp_ml.failure import rolling_origin_folds
    cutoffs = pd.Series(pd.date_range(T0, periods=300, freq="D", tz="UTC"))
    for f in rolling_origin_folds(cutoffs, 5, purge_days=14):
        train_max = cutoffs[f["train"]].max()
        test_min = cutoffs[f["test"]].min()
        assert train_max + pd.Timedelta(days=14) <= test_min      # label windows end before the test block
        assert not (f["train"] & f["test"]).any()


def test_rolling_origin_training_holds_out_later_folds_and_records_everything(dataset, settings):
    from afp_ml.failure import train_failure_model_rolling
    s, labeled = _labeled(dataset, settings)
    r = train_failure_model_rolling(labeled, horizon_days=s.prediction_horizon_days, random_state=42, n_folds=5,
                                    calibration_max_ece=s.calibration_max_ece,
                                    calibration_min_test_positives=s.calibration_min_test_positives)
    m = r.metrics
    assert m["metric_type"] == "rolling_origin_cross_validation"
    ev = m["evaluation"]
    assert max(ev["calibration_fit_folds"]) < min(ev["held_out_test_folds"])   # calibration judged on later data
    used = [f for f in m["folds"] if f["used"]]
    assert m["test"]["rows"] == sum(f["test_rows"] for f in used if f["fold"] in ev["held_out_test_folds"])
    for key in ("precision", "recall", "pr_auc", "false_alert_rate"):        # what the approval gate requires
        assert m[key] is not None
    assert m["test"]["positives"] + m["test"]["negatives"] == m["test"]["rows"]
    assert m["calibration_validated"] == all(m["calibration_checks"].values())
    per_fold = m["held_out_by_fold"]                                         # drift: each held-out fold alone
    assert [f["fold"] for f in per_fold] == ev["held_out_test_folds"]
    assert sum(f["rows"] for f in per_fold) == m["test"]["rows"]
    assert sum(f["positives"] for f in per_fold) == m["test"]["positives"]
    p = r.bundle["pipeline"].predict_proba(labeled[r.feature_names].to_numpy("float64"))[:, 1]
    assert ((p >= 0) & (p <= 1)).all()


def test_rolling_origin_refuses_a_single_class_target(dataset, settings):
    from afp_ml.failure import train_failure_model_rolling
    s, labeled = _labeled(dataset, settings)
    with pytest.raises(ValueError, match="Training cannot continue because the target contains only one class."):
        train_failure_model_rolling(labeled.assign(label=0), horizon_days=14, random_state=42, n_folds=5,
                                    calibration_max_ece=0.1, calibration_min_test_positives=5)


def test_rolling_origin_job_saves_a_loadable_calibrated_model(dataset, settings, tmp_path):
    from dataclasses import replace
    tel, mlog = dataset
    s = replace(failure_settings(settings), failure_evaluation="rolling_origin", model_dir=tmp_path / "models")
    repo = FakeRepository(telemetry=tel, mlog=mlog)
    svc = make_services(repo, s, (T0 + pd.Timedelta(days=420)).to_pydatetime())
    out = execute_job(svc, "TrainFailure", {})
    assert out.status == "Succeeded", out.error
    fm = next(m for m in repo.models if m["model_type"] == "FailurePrediction")
    bundle = svc.store.load_verified(fm["artifact_path"], fm["artifact_sha256"])   # hash-checked round trip
    assert type(bundle["pipeline"]).__name__ == "PlattCalibratedModel"
    assert fm["metrics"]["metric_type"] == "rolling_origin_cross_validation"
