"""Model registration: the ONLY lifecycle transition the ML subsystem performs (Trained -> Registered).

Approval (Leadership) and activation (Data Engineer) happen through the API by humans.
"""

from __future__ import annotations

import logging
from typing import Any

from .artifacts import ArtifactError
from .jobs import JobRefused, JobResult, Services
from .util import get_param

log = logging.getLogger(__name__)

MODEL_TYPES = ("AnomalyDetection", "FailurePrediction", "SensorForecast")
REQUIRED_METRICS: dict[str, tuple[str, ...]] = {
    "AnomalyDetection": ("training_rows", "contamination", "score_percentiles", "severity_thresholds"),
    "FailurePrediction": ("precision", "recall", "pr_auc", "false_alert_rate", "brier", "calibration_validated",
                          "reliability_bins_present"),
    # A forecaster is registrable only with its validation evidence and at least one supported horizon.
    "SensorForecast": ("forecast_validation", "validation_criteria", "supported_horizons"),
}


def validate_for_registration(model: dict[str, Any]) -> list[str]:
    """Return a list of problems (empty = registrable). Does not check the artifact file."""
    problems: list[str] = []
    if model.get("status") != "Trained":
        problems.append(f"model status is {model.get('status')!r}; only 'Trained' models can be registered")
    mtype = model.get("model_type")
    if mtype not in MODEL_TYPES:
        problems.append(f"unknown model_type {mtype!r}")
        return problems
    metrics = model.get("metrics") or {}
    if not metrics:
        problems.append("metrics are missing")
    for key in REQUIRED_METRICS[mtype]:
        if key == "reliability_bins_present":
            if not (metrics.get("test") or {}).get("reliability_bins"):
                problems.append("metrics.test.reliability_bins is missing")
        elif key not in metrics:
            problems.append(f"metrics.{key} is missing")
    if mtype == "SensorForecast" and not metrics.get("supported_horizons"):
        problems.append("no forecast horizon passed validation (metrics.supported_horizons is empty)")
    if mtype == "FailurePrediction" and not model.get("prediction_horizon_days"):
        problems.append("prediction_horizon_days is missing")
    if not model.get("artifact_sha256"):
        problems.append("artifact_sha256 is missing")
    return problems


def register_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    repo = svc.repo
    model_id = get_param(params, "modelVersionId", "model_version_id")
    latest = bool(get_param(params, "latest", default=False))
    model_type = get_param(params, "modelType", "model_type")
    if model_type is not None and model_type not in MODEL_TYPES:
        raise JobRefused(f"--model-type must be one of {', '.join(MODEL_TYPES)}")
    if model_id:
        model = repo.get_model_version(str(model_id))
        if model is None:
            raise JobRefused(f"model_version {model_id} not found")
    elif latest:
        candidates = repo.list_model_versions(model_type, ["Trained"])
        if not candidates:
            raise JobRefused("no model_version with status Trained" + (f" and model_type {model_type}" if model_type else ""))
        model = candidates[0]
    else:
        raise JobRefused("specify --model-version-id <uuid> or --latest")

    problems = validate_for_registration(model)
    if problems:
        raise JobRefused(f"cannot register {model.get('model_name')} {model.get('version')}: " + "; ".join(problems))
    try:
        path = svc.store.resolve(model["artifact_path"])
        svc.store.verify(model["artifact_path"], model["artifact_sha256"])
    except ArtifactError as exc:
        raise JobRefused(f"artifact verification failed: {exc}") from exc
    now = svc.clock()
    if not repo.mark_model_registered(str(model["id"]), now):
        raise JobRefused("model status changed concurrently; it is no longer Trained")
    log.info("Registered %s %s (%s)", model["model_name"], model["version"], model["id"])
    return JobResult(
        summary={"model_version_id": str(model["id"]), "model_name": model["model_name"], "version": model["version"],
                 "model_type": model["model_type"], "previous_status": "Trained", "status": "Registered",
                 "artifact_verified": True, "artifact_file": str(path),
                 "next_step": "Leadership approves (POST /api/v1/models/{id}/approve); a Data Engineer activates "
                              "(POST /api/v1/models/{id}/activate)."},
        records_processed=1, model_version_id=str(model["id"]),
        message=f"Registered {model['model_name']} {model['version']}")
