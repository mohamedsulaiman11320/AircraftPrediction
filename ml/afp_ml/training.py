"""Job handlers for DatasetAudit, TrainAnomaly and TrainFailure."""

from __future__ import annotations

import logging
from typing import Any

from . import anomaly, failure
from .artifacts import dependency_versions
from .audit import compute_readiness, label_definition, run_dataset_audit
from .features import FEATURE_SCHEMA_VERSION, chronological_split
from .jobs import JobRefused, JobResult, Services
from .util import as_bool, get_param, iso, new_uuid, version_string

log = logging.getLogger(__name__)


def _traceability(data_from: Any, data_to: Any) -> dict[str, Any]:
    """Artifact metadata that makes a model reproducible/traceable (not part of the artifact hash)."""
    return {"dependency_versions": dependency_versions(), "feature_schema_version": FEATURE_SCHEMA_VERSION,
            "training_window": {"from_utc": iso(data_from), "to_utc": iso(data_to)}}


def audit_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    repo, settings = svc.repo, svc.settings
    tel = repo.fetch_telemetry()
    mlog = repo.fetch_maintenance_log()
    aircraft = repo.fetch_aircraft()
    summary = run_dataset_audit(tel, mlog, aircraft, settings, svc.clock(), repo.fetch_breach_limits())
    return JobResult(summary=summary, records_processed=int(len(tel) + len(mlog)))


def train_anomaly_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    repo, settings, store = svc.repo, svc.settings, svc.store
    include_suspect = as_bool(get_param(params, "includeSuspect", "include_suspect"), False)
    statuses = ["Valid", "Suspect"] if include_suspect else ["Valid"]
    tel = repo.fetch_telemetry(quality_statuses=statuses)
    try:
        result = anomaly.train_anomaly_model(
            tel, include_suspect=include_suspect, contamination=settings.if_contamination,
            n_estimators=settings.if_n_estimators, window=settings.feature_window, random_state=settings.random_seed)
    except anomaly.TrainingDataError as exc:
        raise JobRefused(f"Cannot train the anomaly model: {exc}. Import validated telemetry first.") from exc
    now = svc.clock()
    version = version_string(now)
    model_id = new_uuid()
    data_sources = sorted(set(map(str, tel["data_source"].dropna().unique())))
    synthetic = any("SYNTHETIC" in s.upper() for s in data_sources)
    limitations = anomaly.LIMITATIONS + (
        " TRAINED ON SYNTHETIC DEMO DATA - for development only, not for operational use." if synthetic else "")
    metrics = {**result.metrics, "data_sources": data_sources, "synthetic_training_data": synthetic}
    saved = store.save(anomaly.MODEL_NAME, version, result.bundle, {
        "model_version_id": model_id, "model_type": anomaly.MODEL_TYPE, "algorithm": anomaly.ALGORITHM,
        "feature_names": result.feature_names, "hyperparameters": result.hyperparameters, "metrics": metrics,
        "training_data_from_utc": iso(result.data_from), "training_data_to_utc": iso(result.data_to),
        "training_run_id": run_id, "created_at_utc": iso(now), "limitations": limitations,
        **_traceability(result.data_from, result.data_to)})
    repo.insert_model_version({
        "id": model_id, "model_name": anomaly.MODEL_NAME, "version": version, "model_type": anomaly.MODEL_TYPE,
        "algorithm": anomaly.ALGORITHM, "status": "Trained", "artifact_path": saved.artifact_path, "artifact_uri": saved.artifact_uri,
        "artifact_sha256": saved.sha256, "feature_names": result.feature_names,
        "hyperparameters": result.hyperparameters, "metrics": metrics,
        "training_data_from_utc": result.data_from.to_pydatetime(), "training_data_to_utc": result.data_to.to_pydatetime(),
        "training_row_count": result.training_rows, "prediction_horizon_days": None, "limitations": limitations,
        "notes": "Created by afp_ml train --target anomaly", "created_at_utc": now, "training_run_id": run_id})
    log.info("Trained %s %s (%d rows) -> %s", anomaly.MODEL_NAME, version, result.training_rows, saved.absolute_path)
    return JobResult(
        summary={"target": "anomaly", "model_version_id": model_id, "model_name": anomaly.MODEL_NAME, "version": version,
                 "status": "Trained", "artifact_path": saved.artifact_path, "artifact_uri": saved.artifact_uri, "artifact_sha256": saved.sha256,
                 "training_rows": result.training_rows, "include_suspect": include_suspect,
                 "severity_thresholds": result.metrics["severity_thresholds"],
                 "score_percentiles": result.metrics["score_percentiles"], "synthetic_training_data": synthetic,
                 "next_step": "Register with `python -m afp_ml register --model-version-id <id>`; Leadership approves "
                              "and a Data Engineer activates via the API/UI."},
        records_processed=result.training_rows, model_version_id=model_id,
        message=f"Trained anomaly model {anomaly.MODEL_NAME} version {version} (status Trained)")


def train_failure_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    repo, settings, store = svc.repo, svc.settings, svc.store
    now = svc.clock()
    tel = repo.fetch_telemetry()
    mlog = repo.fetch_maintenance_log()
    readiness, labeled, label_events = compute_readiness(tel, mlog, settings, now, repo.fetch_breach_limits())
    if readiness["status"] != "Ready":
        msg = ("Failure prediction is BLOCKED - no model was trained. " + " ".join(readiness["reasons"]))
        return JobResult(summary={"target": "failure", "failure_prediction_readiness": readiness},
                         status="Blocked", records_processed=int(len(labeled)), message=msg)
    h = settings.prediction_horizon_days
    try:
        if settings.failure_evaluation == "rolling_origin":
            result = failure.train_failure_model_rolling(
                labeled, horizon_days=h, random_state=settings.random_seed, n_folds=settings.failure_cv_folds,
                calibration_max_ece=settings.calibration_max_ece,
                calibration_min_test_positives=settings.calibration_min_test_positives)
        else:
            split = chronological_split(labeled, purge_gap_days=h)
            result = failure.train_failure_model(split, horizon_days=h, random_state=settings.random_seed,
                                                 calibration_max_ece=settings.calibration_max_ece,
                                                 calibration_min_test_positives=settings.calibration_min_test_positives)
    except ValueError as exc:
        readiness = {**readiness, "status": "Blocked", "reasons": [*readiness["reasons"], str(exc)]}
        return JobResult(summary={"target": "failure", "failure_prediction_readiness": readiness}, status="Blocked",
                         records_processed=int(len(labeled)), message=f"Failure prediction is BLOCKED: {exc}")
    version = version_string(now)
    model_id = new_uuid()
    limitations = failure.LIMITATIONS_TEMPLATE.format(h=h)
    labels = label_definition(settings)
    metrics = {**result.metrics, "readiness": readiness, "label_definition": labels}
    data_from = labeled["cutoff_utc"].min()
    data_to = labeled["cutoff_utc"].max()
    saved = store.save(failure.MODEL_NAME, version, result.bundle, {
        "model_version_id": model_id, "model_type": failure.MODEL_TYPE, "algorithm": result.algorithm,
        "feature_names": result.feature_names, "hyperparameters": result.hyperparameters, "metrics": metrics,
        "prediction_horizon_days": h, "training_run_id": run_id, "created_at_utc": iso(now),
        "limitations": limitations, "label_definition": labels, **_traceability(data_from, data_to)})
    repo.insert_model_version({
        "id": model_id, "model_name": failure.MODEL_NAME, "version": version, "model_type": failure.MODEL_TYPE,
        "algorithm": result.algorithm, "status": "Trained", "artifact_path": saved.artifact_path, "artifact_uri": saved.artifact_uri,
        "artifact_sha256": saved.sha256, "feature_names": result.feature_names,
        "hyperparameters": result.hyperparameters, "metrics": metrics,
        "training_data_from_utc": data_from.to_pydatetime(), "training_data_to_utc": data_to.to_pydatetime(),
        "training_row_count": result.training_rows, "prediction_horizon_days": h, "limitations": limitations,
        "notes": "Created by afp_ml train --target failure", "created_at_utc": now, "training_run_id": run_id})
    return JobResult(
        summary={"target": "failure", "model_version_id": model_id, "model_name": failure.MODEL_NAME,
                 "version": version, "algorithm": result.algorithm, "status": "Trained",
                 "artifact_path": saved.artifact_path, "artifact_uri": saved.artifact_uri, "artifact_sha256": saved.sha256,
                 "test_metrics": {k: result.metrics[k] for k in ("precision", "recall", "pr_auc", "false_alert_rate", "brier")},
                 "calibration_validated": result.metrics["calibration_validated"], "failure_prediction_readiness": readiness},
        records_processed=int(len(labeled)), model_version_id=model_id,
        message=f"Trained failure model {failure.MODEL_NAME} version {version} ({result.algorithm}, status Trained)")
