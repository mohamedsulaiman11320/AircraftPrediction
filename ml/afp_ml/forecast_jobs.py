"""Job handlers: TrainSensorForecast (validate + record a candidate) and ForecastSensors (Active model only)."""

from __future__ import annotations

import datetime as dt
import logging
from typing import Any

from . import forecast as fc
from .artifacts import dependency_versions
from .jobs import JobResult, Services
from .util import iso, new_uuid, version_string

log = logging.getLogger(__name__)

LIMITATIONS = (
    "Forecasts each aircraft's typical level and daily pattern from its own recent readings. It does not foresee "
    "individual spikes, faults or maintenance. A horizon/metric is forecast only where the backtest beat both naive "
    "baselines by the configured margin with calibrated 80 % intervals; values are advisory, not airworthiness decisions."
)


def _criteria(settings: Any) -> fc.ValidationCriteria:
    return fc.ValidationCriteria(max_mae_ratio=settings.forecast_max_mae_ratio, coverage_low=settings.forecast_coverage_low,
                                 coverage_high=settings.forecast_coverage_high, min_eval_origins=settings.forecast_min_eval_origins)


def train_sensor_forecast_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    """Backtest the method on recorded telemetry and record a Trained candidate with its full validation evidence.

    A candidate is recorded even when no horizon passes (so the evidence is visible); registration then refuses it.
    Nothing is approved or activated here.
    """
    repo, settings, store, now = svc.repo, svc.settings, svc.store, svc.clock()
    frames = fc.hourly_frame(repo.fetch_telemetry())
    if not frames:
        return JobResult(summary={"target": "sensor_forecast"}, status="Blocked", records_processed=0,
                         message="Sensor forecasting is BLOCKED - no telemetry is recorded.")
    criteria = _criteria(settings)
    bt = fc.backtest(frames, criteria)
    version = version_string(now)
    model_id = new_uuid()
    data_from = min(f.index.min() for f in frames.values())
    data_to = max(f.index.max() for f in frames.values())
    method = {"algorithm": fc.ALGORITHM, "window_days": fc.WINDOW_DAYS, "level_halflife_hours": fc.LEVEL_HALFLIFE_HOURS,
              "interval_quantiles": list(fc.INTERVAL_QUANTILES), "lead_buckets_hours": [list(b) for b in fc.LEAD_BUCKETS],
              "horizon_hours": fc.HORIZON_HOURS}
    metrics = {"metric_type": "rolling_origin_forecast_backtest", "method": method,
               "forecast_validation": bt["results"], "validation_criteria": bt["criteria"],
               "supported_horizons": bt["supported"]}
    # "pipeline" is what every afp_ml bundle carries; for this forecaster it is the method specification itself.
    bundle = {"kind": fc.MODEL_TYPE, "pipeline": method, "method": method, "interval_quantiles": bt["interval_quantiles"],
              "supported": bt["supported"], "metrics": list(fc.METRICS)}
    saved = store.save(fc.MODEL_NAME, version, bundle, {
        "model_version_id": model_id, "model_type": fc.MODEL_TYPE, "algorithm": fc.ALGORITHM,
        "feature_names": list(fc.METRICS), "hyperparameters": method, "metrics": metrics, "training_run_id": run_id,
        "created_at_utc": iso(now), "limitations": LIMITATIONS, "dependency_versions": dependency_versions(),
        "training_window": {"from_utc": iso(data_from), "to_utc": iso(data_to)}})
    rows = int(sum(f.notna().any(axis=1).sum() for f in frames.values()))
    repo.insert_model_version({
        "id": model_id, "model_name": fc.MODEL_NAME, "version": version, "model_type": fc.MODEL_TYPE,
        "algorithm": fc.ALGORITHM, "status": "Trained", "artifact_path": saved.artifact_path, "artifact_uri": saved.artifact_uri,
        "artifact_sha256": saved.sha256, "feature_names": list(fc.METRICS), "hyperparameters": method, "metrics": metrics,
        "training_data_from_utc": data_from.to_pydatetime(), "training_data_to_utc": data_to.to_pydatetime(),
        "training_row_count": rows, "prediction_horizon_days": None, "limitations": LIMITATIONS,
        "notes": "Created by afp_ml train --target forecast", "created_at_utc": now, "training_run_id": run_id})
    supported = bt["supported"]
    message = (f"Trained sensor forecaster {fc.MODEL_NAME} version {version} (status Trained); supported: "
               + ("; ".join(f"{h}: {', '.join(m)}" for h, m in supported.items()) if supported else "none - no horizon passed validation"))
    return JobResult(summary={"target": "sensor_forecast", "model_version_id": model_id, "version": version,
                              "supported_horizons": supported, "artifact_path": saved.artifact_path},
                     records_processed=rows, model_version_id=model_id, message=message)


def forecast_sensors_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    """Write forecasts with the ACTIVE SensorForecast model only, for its validated horizons and metrics. Idempotent."""
    repo, store, now = svc.repo, svc.store, svc.clock()
    model = repo.get_active_model(fc.MODEL_TYPE)
    if model is None:
        return JobResult(summary={"forecasts_written": 0}, records_processed=0,
                         message="No Active SensorForecast model - nothing forecast (approval and activation are required).")
    bundle = store.load_verified(model["artifact_path"], model["artifact_sha256"])   # hash-checked
    supported: dict[str, list[str]] = bundle.get("supported") or {}
    since = now - dt.timedelta(days=fc.WINDOW_DAYS + 2)
    frames = fc.hourly_frame(repo.fetch_telemetry(from_utc=since))
    rows = []
    for aircraft, frame in frames.items():
        if frame.dropna(how="all").empty:
            continue
        for horizon, metrics in supported.items():
            anchor, points = fc.forecast_points(frame, horizon, metrics, bundle.get("interval_quantiles") or {})
            rows.append({"id": new_uuid(), "aircraft_id": aircraft, "model_version_id": str(model["id"]), "horizon": horizon,
                         "generated_at_utc": now, "data_as_of_utc": anchor.to_pydatetime(), "pipeline_run_id": run_id,
                         "points": points})
    inserted = repo.insert_sensor_forecasts(rows)
    return JobResult(summary={"model_version_id": str(model["id"]), "forecasts_prepared": len(rows), "forecasts_written": inserted,
                              "already_present": len(rows) - inserted, "supported_horizons": supported},
                     records_processed=inserted, model_version_id=str(model["id"]),
                     message=f"Wrote {inserted} sensor forecast(s) ({len(rows) - inserted} already present) with model {model.get('version')}")
