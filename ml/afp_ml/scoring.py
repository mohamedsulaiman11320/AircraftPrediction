"""Score job: anomaly scoring with the ACTIVE Isolation Forest + per-aircraft risk indicators."""

from __future__ import annotations

import logging
from typing import Any

import pandas as pd

from .anomaly import score_features
from .artifacts import ArtifactError
from .features import build_anomaly_features, build_failure_features
from .failure import predict_failure
from .config import ENVIRONMENT_DEVELOPMENT
from .jobs import JobRefused, JobResult, Services
from .risk import PredictionState, build_descriptive_predictions, fallback_cutoff, model_category, utc_timestamp
from .util import as_bool, get_param, new_uuid, to_jsonable

log = logging.getLogger(__name__)

NO_ACTIVE_MODEL_MESSAGE = (
    "No Active AnomalyDetection model. Train one (`python -m afp_ml train --target anomaly`), register it "
    "(`python -m afp_ml register --latest`), then have Leadership approve it and a Data Engineer activate it in the "
    "API/UI (POST /api/v1/models/{id}/approve, then /activate). Scoring refuses to run without an Active model."
)


def select_new_rows(new_rows: pd.DataFrame, quality_statuses: list[str]) -> tuple[pd.DataFrame, int]:
    """Rows eligible for scoring (quality filter) and the new watermark (max id seen, any status)."""
    if new_rows.empty:
        return new_rows, 0
    watermark = int(new_rows["id"].max())
    eligible = new_rows.loc[new_rows["quality_status"].astype(str).isin(quality_statuses)]
    return eligible, watermark


def anomaly_rows(scored: pd.DataFrame, feature_names: list[str], *, model_version_id: str, run_id: str | None,
                 scored_at: Any) -> list[dict[str, Any]]:
    """Rows for aircraft_anomaly_result - ONLY readings flagged anomalous are persisted."""
    flagged = scored.loc[scored["is_anomaly"].astype(bool)]
    rows = []
    for r in flagged.to_dict("records"):
        rows.append({
            "aircraft_id": str(r["aircraft_id"]), "telemetry_record_id": int(r["id"]),
            "sensor_id": r["sensor_id"] if r["sensor_id"] is not None and not pd.isna(r["sensor_id"]) else None,
            "observed_at_utc": pd.Timestamp(r["recorded_at_utc"]).to_pydatetime(),
            "anomaly_score": float(r["anomaly_score"]), "is_anomaly": True, "severity": r["severity"],
            "model_version_id": model_version_id, "pipeline_run_id": run_id, "scored_at_utc": scored_at,
            "feature_values": to_jsonable({n: r[n] for n in feature_names}), "investigation_status": "New",
        })
    return rows


def score_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    repo, settings, store = svc.repo, svc.settings, svc.store
    full = as_bool(get_param(params, "full", "rescoreAll", "rescore_all"), False)
    model = repo.get_active_model("AnomalyDetection")
    if model is None:
        raise JobRefused(NO_ACTIVE_MODEL_MESSAGE)
    model_id = str(model["id"])
    try:
        bundle = store.load_verified(model["artifact_path"], model["artifact_sha256"])
    except ArtifactError as exc:
        raise JobRefused(f"Refusing to score with model {model['model_name']} {model['version']}: {exc}") from exc
    if bundle.get("kind") != "AnomalyDetection":
        raise JobRefused("The Active AnomalyDetection model artifact is not an anomaly model bundle.")
    statuses = list(bundle.get("quality_statuses") or ["Valid"])
    window = int(bundle["feature_config"]["window"])
    names = list(bundle["feature_names"])

    previous = None if full else repo.get_score_watermark(model_id)
    start = 0 if previous is None else previous
    new_rows = repo.fetch_telemetry(min_id_exclusive=start)
    eligible, max_seen = select_new_rows(new_rows, statuses)
    watermark = max(start, max_seen)
    now = svc.clock()
    inserted = 0
    flagged_count = 0
    warmup_skipped = 0
    scored_rows = 0
    severity_counts = {"Low": 0, "Medium": 0, "High": 0}
    if len(eligible):
        context_from = eligible["recorded_at_utc"].min() - pd.Timedelta(hours=settings.scoring_context_hours)
        context = repo.fetch_telemetry(from_utc=context_from.to_pydatetime(),
                                       to_utc=eligible["recorded_at_utc"].max().to_pydatetime(),
                                       aircraft_ids=sorted(eligible["aircraft_id"].unique()), quality_statuses=statuses)
        combined = pd.concat([context, eligible], ignore_index=True).drop_duplicates("id")
        feats = build_anomaly_features(combined, window)
        target_all = feats.loc[feats["id"].isin(set(eligible["id"]))]
        target = target_all.loc[~target_all["warmup"]]
        warmup_skipped = int(len(target_all) - len(target))
        scored_rows = int(len(target))
        scored = score_features(bundle, target)
        rows = anomaly_rows(scored, names, model_version_id=model_id, run_id=run_id, scored_at=now)
        flagged_count = len(rows)
        for r in rows:
            severity_counts[r["severity"]] += 1
        if settings.persist_telemetry_anomalies:
            inserted = repo.insert_anomaly_results(rows)
    log.info("Scored %d telemetry rows (watermark %s -> %s); %d anomalies flagged, %d inserted",
             scored_rows, start, watermark, flagged_count, inserted)

    # Descriptive per-aircraft indicators (always produced, probability NULL).
    aircraft = repo.fetch_last_telemetry_per_aircraft()
    since = (pd.Timestamp(now) - pd.Timedelta(days=settings.risk_window_days)).to_pydatetime()
    counts = repo.fetch_anomaly_counts(model_id, since)
    readings = repo.fetch_telemetry_counts(since, statuses)
    baseline = bundle.get("baseline_rates") or (model.get("metrics") or {}).get("baseline_rates") or {}
    predictions = build_descriptive_predictions(aircraft, counts, readings, now=now, settings=settings,
                                                baseline=baseline, model_version_id=model_id, pipeline_run_id=run_id,
                                                forecast_conditions=repo.fetch_forecast_conditions())
    model_predictions, failure_info = _failure_predictions(svc, aircraft, now, run_id)
    predictions += model_predictions
    n_pred = repo.insert_risk_predictions(predictions)
    by_category: dict[str, int] = {}
    for p in predictions:
        key = f"{p['basis']}:{p['risk_category']}"
        by_category[key] = by_category.get(key, 0) + 1

    summary = {
        "model_version_id": model_id, "model_name": model["model_name"], "model_version": model["version"],
        "full_rescore": full, "watermark_start": start, "max_telemetry_record_id": watermark,
        "new_telemetry_rows": int(len(new_rows)), "eligible_rows": int(len(eligible)),
        "skipped_rows_quality": int(len(new_rows) - len(eligible)), "quality_statuses_scored": statuses,
        "scored_rows": scored_rows, "skipped_rows_insufficient_history": warmup_skipped,
        "anomalies_flagged": flagged_count, "anomalies_inserted": inserted,
        "anomalies_already_present": flagged_count - inserted, "severity_counts": severity_counts,
        "predictions_inserted": n_pred, "predictions_already_present": len(predictions) - n_pred,
        "predictions_by_basis_category": by_category,
        "failure_model": failure_info,
        "score_note": "anomaly_score = -score_samples (higher = more anomalous); NOT a probability.",
    }
    return JobResult(summary=summary, records_processed=scored_rows, model_version_id=model_id,
                     message=f"Scored {scored_rows} rows; {inserted} new anomalies; {n_pred} risk indicators")


def _failure_predictions(svc: Services, aircraft: pd.DataFrame, now: Any, run_id: str | None, *,
                         model: dict[str, Any] | None = None, cutoff: pd.Timestamp | None = None,
                         recency_reference: Any = None) -> tuple[list[dict[str, Any]], dict[str, Any] | None]:
    """Score every aircraft with ``model`` (default: the Active FailurePrediction model).

    ``cutoff`` defaults to the scoring day (00:00 UTC). ``recency_reference`` is the instant telemetry
    must be recent relative to (default ``now``).
    """
    repo, settings, store = svc.repo, svc.settings, svc.store
    fm = model if model is not None else repo.get_active_model("FailurePrediction")
    if fm is None:
        return [], None
    try:
        bundle = store.load_verified(fm["artifact_path"], fm["artifact_sha256"])
    except ArtifactError as exc:
        log.error("Active FailurePrediction model not used: %s", exc)
        return [], {"model_version_id": str(fm["id"]), "used": False, "error": str(exc)}
    metrics = fm.get("metrics") or {}
    validated = bool(metrics.get("calibration_validated") is True and bundle.get("calibration_validated") is True)
    horizon = int(fm.get("prediction_horizon_days") or bundle.get("horizon_days") or settings.prediction_horizon_days)
    cutoff = pd.Timestamp(cutoff) if cutoff is not None else pd.Timestamp(now).floor("D")
    tel = repo.fetch_telemetry(from_utc=(cutoff - pd.Timedelta(days=31)).to_pydatetime(), to_utc=cutoff.to_pydatetime())
    tel = tel.loc[tel["recorded_at_utc"] < cutoff]
    # Same inputs as training (audit.compute_readiness), so a scored row is built exactly like a trained one.
    feats = build_failure_features(tel, until=cutoff, maintenance=repo.fetch_maintenance_log(),
                                   breach_limits=repo.fetch_breach_limits())
    missing_features = [n for n in bundle["feature_names"] if n not in feats.columns]
    if missing_features:
        # Feature-schema mismatch (e.g. an artifact from a newer schema): refuse rather than mis-score.
        msg = f"model expects features this build does not produce: {missing_features}"
        log.error("Active FailurePrediction model not used: %s", msg)
        return [], {"model_version_id": str(fm["id"]), "used": False, "error": msg}
    feats = feats.loc[feats["cutoff_utc"] == cutoff]
    scores = dict(zip(feats["aircraft_id"], predict_failure(bundle, feats))) if len(feats) else {}
    feat_by_aircraft = {r["aircraft_id"]: r for r in feats.to_dict("records")}
    recency_cut = (pd.Timestamp(recency_reference if recency_reference is not None else now)
                   - pd.Timedelta(hours=settings.risk_recency_hours))
    rows = []
    for a in aircraft.itertuples(index=False):
        aid = str(a.aircraft_id)
        last = a.last_telemetry_at_utc
        recent = last is not None and not pd.isna(last) and pd.Timestamp(last) >= recency_cut
        feat_row = feat_by_aircraft.get(aid)
        score = scores.get(aid) if feat_row is not None else None
        category = "Undetermined" if score is None else model_category(float(score), bundle["risk_thresholds"], recent)
        snapshot = {n: feat_row[n] for n in bundle["feature_names"]} if feat_row is not None else {}
        if feat_row is not None:
            state, feature_cutoff = PredictionState.SCORED, utc_timestamp(feat_row["cutoff_utc"]).to_pydatetime()
        else:
            state, feature_cutoff = PredictionState.MISSING_FEATURES, fallback_cutoff(now)
        rows.append({
            "prediction_state": state.value, "feature_cutoff_utc": feature_cutoff,
            "id": new_uuid(), "aircraft_id": aid, "component_id": None, "prediction_timestamp_utc": now,
            "horizon_days": horizon, "risk_category": category, "basis": "Model",
            "probability": float(score) if (validated and score is not None) else None,
            "probability_validated": bool(validated and score is not None),
            "feature_snapshot": to_jsonable({"features": snapshot, "cutoff_utc": cutoff, "model_score": score,
                                             "risk_thresholds": bundle["risk_thresholds"],
                                             "calibration_validated": validated}),
            "model_version_id": str(fm["id"]), "pipeline_run_id": run_id,
            "approval_status": "PendingReview", "actual_outcome": "Unknown",
        })
    return rows, {"model_version_id": str(fm["id"]), "used": True, "calibration_validated": validated,
                  "predictions": len(rows), "cutoff_utc": cutoff.isoformat()}


# --------------------------------------------------------------------------------------------
# Development-only: score an explicitly selected (unapproved) candidate
# --------------------------------------------------------------------------------------------

def score_candidate_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    """Score every aircraft with a chosen FailurePrediction candidate - DEVELOPMENT environment only.

    Operational scoring only ever uses the approved, Active model (``score_job``). This job exists so the
    isolated synthetic environment can exercise train -> evaluate -> score end to end. It refuses to run
    unless ``AFP_ENVIRONMENT=development`` (which config forbids on the operational database), so an
    unapproved candidate can never write predictions into operational tables.
    """
    repo, settings = svc.repo, svc.settings
    if settings.environment != ENVIRONMENT_DEVELOPMENT:
        raise JobRefused("ScoreCandidate runs only in the isolated development environment (AFP_ENVIRONMENT="
                         "development). Operational predictions come from the approved, Active model.")
    model_id = get_param(params, "model_version_id", "modelVersionId")
    if not model_id:
        raise JobRefused("ScoreCandidate needs model_version_id (the candidate to score).")
    model = repo.get_model_version(str(model_id))
    if model is None or model.get("model_type") != "FailurePrediction":
        raise JobRefused(f"Model version {model_id} is not a FailurePrediction model in this environment.")

    aircraft = repo.fetch_last_telemetry_per_aircraft()
    last = aircraft["last_telemetry_at_utc"].max() if len(aircraft) else pd.NaT
    if pd.isna(last):
        raise JobRefused("No processed telemetry to score.")
    # Score as of the latest successfully processed telemetry, not the wall clock: the synthetic data has
    # a fixed end, and a cutoff after it would make every aircraft look stale.
    cutoff = pd.Timestamp(last).floor("D") + pd.Timedelta(days=1)
    now = svc.clock()
    rows, info = _failure_predictions(svc, aircraft, now, run_id, model=model, cutoff=cutoff, recency_reference=cutoff)
    if info is None or info.get("used") is False:
        raise JobRefused(f"Candidate {model_id} was not used: {(info or {}).get('error', 'unknown reason')}")
    for r in rows:
        snap = dict(r["feature_snapshot"])
        snap["environment"] = ENVIRONMENT_DEVELOPMENT
        snap["candidate_status"] = model.get("status")
        r["feature_snapshot"] = snap
    inserted = repo.insert_risk_predictions(rows)
    states: dict[str, int] = {}
    for r in rows:
        states[r["prediction_state"]] = states.get(r["prediction_state"], 0) + 1
    summary = {
        "environment": ENVIRONMENT_DEVELOPMENT, "model_version_id": str(model_id),
        "model_version": model.get("version"), "model_status": model.get("status"),
        "cutoff_utc": cutoff.isoformat(), "aircraft": int(len(aircraft)), "predictions": len(rows),
        "predictions_inserted": inserted, "predictions_already_present": len(rows) - inserted,
        "prediction_states": states, "calibration_validated": info.get("calibration_validated"),
        "note": "Development candidate scored on synthetic data - not an operational or validated prediction.",
    }
    return JobResult(summary=summary, records_processed=len(rows), model_version_id=str(model_id),
                     message=f"Scored {len(rows)} aircraft with candidate {model.get('version')} (development)")
