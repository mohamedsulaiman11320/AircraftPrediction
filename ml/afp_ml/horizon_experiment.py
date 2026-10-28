"""Horizon experiment (DEVELOPMENT ONLY): does a shorter failure horizon give better-calibrated probabilities?

Reuses the operational pipeline unchanged - ``compute_readiness`` (verified label events, features from data before
the cutoff, per-aircraft right-censoring) and ``train_failure_model_rolling`` (specified RandomForest, rolling-origin
folds with purge gap = horizon, Platt mapping fitted on the earlier folds and judged on the later ones, the existing
calibration gates). Only ``prediction_horizon_days`` differs between runs.

It never writes to a database: the repository it is given is read-only (no writer DSN), no ``pipeline_run`` or
``model_version`` row is created, and nothing is registered, approved, activated or scored. Results go to
``<AFP_MODEL_DIR>/experiments/<EXPERIMENT_ID>/<stamp>/`` and the environment's (synthetic) bucket under the same key.
Refused unless ``AFP_ENVIRONMENT=development``, which config never allows on the operational database.
"""
from __future__ import annotations

import io
import json
import time
from dataclasses import replace
from pathlib import Path
from typing import Any, Sequence

import joblib
import pandas as pd

from . import failure
from .artifacts import _make_read_only, dependency_versions, put_immutable, sha256_bytes
from .audit import compute_readiness, label_definition
from .config import ENVIRONMENT_DEVELOPMENT
from .features import FEATURE_SCHEMA_VERSION, build_failure_features
from .jobs import JobRefused, Services
from .util import iso, to_jsonable

EXPERIMENT_ID = "7-day-experiment"
BASELINE_HORIZON_DAYS = 30
CANDIDATE_HORIZON_DAYS = 7

# MinIO on this Windows host intermittently reports a storage-layer lock as S3 AccessDenied (seen 2026-10-27; the
# same upload succeeds moments later). Bounded retry; a failure that persists is still raised.
UPLOAD_RETRY_DELAYS_SECONDS = (1.0, 2.0, 4.0)

OUTCOME_PROMISING = "Promising candidate"
OUTCOME_UNCALIBRATED = "Informative but uncalibrated"
OUTCOME_INSUFFICIENT = "Insufficient evidence"
OUTCOME_NO_IMPROVEMENT = "No improvement"

_REPORTED_TEST_KEYS = ("rows", "positives", "negatives", "prevalence", "threshold", "precision", "recall", "f1",
                       "roc_auc", "pr_auc", "pr_auc_baseline", "brier", "baseline_brier_prevalence",
                       "brier_skill_score", "expected_calibration_error", "reliability_bins", "confusion_matrix")


def _event_distribution(events: pd.DataFrame) -> dict[str, Any]:
    if events.empty:
        return {"events": 0}
    ev = events.assign(occurred_at_utc=pd.to_datetime(events["occurred_at_utc"], utc=True))
    gaps = (ev.sort_values("occurred_at_utc").groupby("aircraft_id")["occurred_at_utc"].diff().dt.total_seconds()
            .dropna() / 86400)
    return {"events": int(len(ev)), "aircraft": int(ev["aircraft_id"].nunique()),
            "first_utc": iso(ev["occurred_at_utc"].min()), "last_utc": iso(ev["occurred_at_utc"].max()),
            "per_month": {k: int(v) for k, v in ev["occurred_at_utc"].dt.strftime("%Y-%m").value_counts().sort_index().items()},
            "per_aircraft": {str(k): int(v) for k, v in ev["aircraft_id"].value_counts().sort_index().items()},
            "days_between_events": ({"min": float(gaps.min()), "mean": float(gaps.mean()), "max": float(gaps.max())}
                                    if len(gaps) else None)}


def _counts(labeled: pd.DataFrame) -> dict[str, Any]:
    if labeled.empty:
        return {"rows": 0, "positives": 0, "negatives": 0}
    cut = pd.to_datetime(labeled["cutoff_utc"], utc=True)
    return {"rows": int(len(labeled)), "positives": int(labeled["label"].sum()),
            "negatives": int((labeled["label"] == 0).sum()), "cutoff_from_utc": iso(cut.min()), "cutoff_to_utc": iso(cut.max()),
            "positives_per_month": {k: int(v) for k, v in labeled.loc[labeled["label"] == 1, "cutoff_utc"]
                                    .pipe(pd.to_datetime, utc=True).dt.strftime("%Y-%m").value_counts().sort_index().items()}}


def _evaluate(labeled: pd.DataFrame, horizon: int, settings: Any) -> dict[str, Any]:
    """One rolling-origin run; a data shortfall is reported as such, never papered over."""
    try:
        r = failure.train_failure_model_rolling(
            labeled, horizon_days=horizon, random_state=settings.random_seed, n_folds=settings.failure_cv_folds,
            calibration_max_ece=settings.calibration_max_ece,
            calibration_min_test_positives=settings.calibration_min_test_positives)
    except ValueError as exc:
        return {"status": "insufficient", "reason": str(exc), "data": _counts(labeled)}
    m = r.metrics
    return {"status": "trained", "horizon_days": horizon, "data": _counts(labeled),
            "evaluation": m["evaluation"], "folds": m["folds"], "held_out_by_fold": m["held_out_by_fold"],
            "decision_threshold": m["decision_threshold"], "decision_threshold_rule": m["decision_threshold_rule"],
            "calibration_method": m["calibration_method"],
            "validation": {k: m["validation"][k] for k in _REPORTED_TEST_KEYS},
            "test": {k: m["test"][k] for k in _REPORTED_TEST_KEYS},
            "calibration_criteria": m["calibration_criteria"], "calibration_checks": m["calibration_checks"],
            "gates_pass": m["calibration_validated"], "beats_prior_baseline_pr_auc": m["beats_baseline"],
            "feature_importance_top10": m["feature_importance"][:10], "_bundle": r.bundle}


def decide(baseline: dict[str, Any], candidate: dict[str, Any], min_examples: int) -> tuple[str, list[str]]:
    """Map the comparison onto the four predefined outcomes. The gates are the operational ones, unchanged."""
    if candidate.get("status") != "trained":
        return OUTCOME_INSUFFICIENT, [f"candidate could not be evaluated: {candidate.get('reason')}"]
    t, ev = candidate["test"], candidate["evaluation"]
    short = [f"held-out {k} {t[k]} < {min_examples}" for k in ("positives", "negatives") if t[k] < min_examples]
    if ev["folds_used"] < 3:
        short.append(f"only {ev['folds_used']} usable temporal folds (need >= 3: >= 1 to fit, >= 2 held out)")
    if short:
        return OUTCOME_INSUFFICIENT, short
    if candidate["gates_pass"]:
        return OUTCOME_PROMISING, ["every calibration gate passes on the chronological hold-out"]
    failed = [k for k, ok in candidate["calibration_checks"].items() if not ok]
    if baseline.get("status") != "trained":
        return OUTCOME_UNCALIBRATED, [f"gates failed: {failed}; no 30-day baseline to compare against"]
    b = baseline["test"]
    better = [name for name, c, base, higher in (
        ("roc_auc", t["roc_auc"], b["roc_auc"], True),
        ("brier_skill_score", t["brier_skill_score"], b["brier_skill_score"], True),
        ("expected_calibration_error", t["expected_calibration_error"], b["expected_calibration_error"], False))
        if c is not None and base is not None and ((c > base) if higher else (c < base))]
    if better:
        return OUTCOME_UNCALIBRATED, [f"gates failed: {failed}", f"improved vs 30-day baseline: {better}"]
    return OUTCOME_NO_IMPROVEMENT, [f"gates failed: {failed}", "no metric improved vs the 30-day baseline"]


def run_horizon_experiment(svc: Services, *, horizons: Sequence[int] = (BASELINE_HORIZON_DAYS, CANDIDATE_HORIZON_DAYS)
                           ) -> dict[str, Any]:
    settings, repo = svc.settings, svc.repo
    if settings.environment != ENVIRONMENT_DEVELOPMENT:
        raise JobRefused("The horizon experiment runs only in the isolated development environment "
                         "(AFP_ENVIRONMENT=development); it never runs against operational data.")
    baseline_h, candidate_h = horizons
    now = svc.clock()
    tel, mlog, limits = repo.fetch_telemetry(), repo.fetch_maintenance_log(), repo.fetch_breach_limits()

    labeled_by_h, readiness_by_h, events = {}, {}, None
    for h in horizons:
        readiness, labeled, events = compute_readiness(tel, mlog, replace(settings, prediction_horizon_days=h), now, limits)
        labeled_by_h[h], readiness_by_h[h] = labeled, readiness
    feature_rows = int(len(build_failure_features(tel, maintenance=mlog, breach_limits=limits)))

    # Equivalent comparison: identical cutoff span (the shorter horizon is censored less, so it is trimmed to the
    # longer one's eligible span); fold boundaries are then identical, only the purge gap (= horizon) differs.
    lo = max(pd.to_datetime(labeled_by_h[h]["cutoff_utc"], utc=True).min() for h in horizons)
    hi = min(pd.to_datetime(labeled_by_h[h]["cutoff_utc"], utc=True).max() for h in horizons)

    def span(df: pd.DataFrame) -> pd.DataFrame:
        cut = pd.to_datetime(df["cutoff_utc"], utc=True)
        return df.loc[(cut >= lo) & (cut <= hi)].reset_index(drop=True)

    runs: dict[str, dict[str, Any]] = {}
    for h in horizons:
        if readiness_by_h[h]["status"] != "Ready":
            runs[f"{h}d_equivalent_span"] = {"status": "insufficient", "reason": readiness_by_h[h]["reasons"],
                                             "data": _counts(labeled_by_h[h])}
        else:
            runs[f"{h}d_equivalent_span"] = _evaluate(span(labeled_by_h[h]), h, settings)
    if readiness_by_h[candidate_h]["status"] == "Ready":
        runs[f"{candidate_h}d_full_eligible_span"] = _evaluate(labeled_by_h[candidate_h], candidate_h, settings)

    baseline, candidate = runs[f"{baseline_h}d_equivalent_span"], runs[f"{candidate_h}d_equivalent_span"]
    outcome, reasons = decide(baseline, candidate, settings.calibration_min_test_positives)
    stamp = now.strftime("%Y%m%dT%H%M%SZ")
    report = {
        "experiment_id": EXPERIMENT_ID, "run_stamp": stamp, "created_at_utc": iso(now),
        "environment": settings.environment, "database": settings.db_name,
        "scope": "DEVELOPMENT ONLY - synthetic data; not an approved horizon, not a registered model, "
                 "probabilities are NOT validated for operational use",
        "horizons": {"baseline_days": baseline_h, "candidate_days": candidate_h},
        "label_definition": {**label_definition(settings),
                             "rule": "label = 1 when a label event for the aircraft falls in (cutoff, cutoff + horizon]; "
                                     "an event exactly at the cutoff belongs to the previous day; examples whose window "
                                     "ends after the aircraft's last telemetry reading are censored (removed)"},
        "leakage_controls": ["features use telemetry with recorded_at_utc < cutoff only",
                             "days_since_last_maintenance uses verified events strictly before the cutoff",
                             "rolling-origin folds: training cutoffs end >= horizon days before the test block",
                             "Platt mapping and decision threshold fitted on earlier folds, never on held-out folds"],
        "metric_definitions": {
            "ece": "10 equal-width probability bins on [0,1]; ECE = sum(count_b / n * |mean_predicted_b - observed_b|)",
            "brier_skill": "1 - Brier / Brier_baseline, where Brier_baseline = mean((p0 - y)^2) with p0 = the "
                           "prevalence of the SAME evaluated split (a constant forecast of the observed rate)",
            "decision_threshold": "maximises F1 on the calibrated calibration-fit folds; precision/recall use p >= it"},
        "gates": {"max_expected_calibration_error": settings.calibration_max_ece, "brier_skill_must_exceed": 0.0,
                  "min_test_positives": settings.calibration_min_test_positives},
        "data_provenance": {"telemetry_rows": int(len(tel)), "aircraft": int(tel["aircraft_id"].nunique()) if len(tel) else 0,
                            "telemetry_from_utc": iso(pd.to_datetime(tel["recorded_at_utc"], utc=True).min()) if len(tel) else None,
                            "telemetry_to_utc": iso(pd.to_datetime(tel["recorded_at_utc"], utc=True).max()) if len(tel) else None,
                            "maintenance_rows": int(len(mlog)), "label_events": _event_distribution(events),
                            "aircraft_day_feature_rows": feature_rows,
                            "censored_rows": {f"{h}d": feature_rows - int(len(labeled_by_h[h])) for h in horizons},
                            "feature_schema_version": FEATURE_SCHEMA_VERSION,
                            "equivalent_span_utc": [iso(lo), iso(hi)]},
        "readiness": {f"{h}d": readiness_by_h[h] for h in horizons},
        "runs": {k: {kk: vv for kk, vv in v.items() if kk != "_bundle"} for k, v in runs.items()},
        "decision": {"outcome": outcome, "reasons": reasons},
        "configuration": {"random_seed": settings.random_seed, "folds_requested": settings.failure_cv_folds,
                          "algorithm": "specified RandomForest (failure.SPECIFIED_CANDIDATE)",
                          "dependency_versions": dependency_versions()},
    }
    report["artifacts"] = _save(svc, stamp, report, runs.get(f"{candidate_h}d_equivalent_span", {}).get("_bundle"))
    return report


def _save(svc: Services, stamp: str, report: dict[str, Any], bundle: dict[str, Any] | None) -> dict[str, Any]:
    """Write the report (and the unregistered candidate bundle) locally, read-only, and to the environment's bucket."""
    folder = Path(svc.settings.model_dir) / "experiments" / EXPERIMENT_ID / stamp
    folder.mkdir(parents=True, exist_ok=False)
    files: dict[str, bytes] = {"report.json": json.dumps(to_jsonable(report), indent=2).encode("utf-8")}
    if bundle is not None:
        buf = io.BytesIO()
        joblib.dump({**bundle, "experiment_id": EXPERIMENT_ID, "registered": False}, buf)
        files["candidate_7d_bundle.joblib"] = buf.getvalue()
    manifest = {name: {"sha256": sha256_bytes(data), "bytes": len(data)} for name, data in files.items()}
    files["manifest.json"] = json.dumps(manifest, indent=2).encode("utf-8")
    for name, data in files.items():                  # the local record is complete before any upload is tried
        (folder / name).write_bytes(data)
        _make_read_only(folder / name)
    uploaded: list[str] = []
    for name, data in files.items():
        if svc.store.remote is not None:
            key = f"experiments/{EXPERIMENT_ID}/{stamp}/{name}"
            content_type = "application/json" if name.endswith(".json") else "application/octet-stream"
            for delay in (*UPLOAD_RETRY_DELAYS_SECONDS, None):
                try:
                    put_immutable(svc.store.remote, key, data, content_type)
                    break
                except Exception:  # noqa: BLE001 - retried a bounded number of times, then re-raised
                    if delay is None:
                        raise
                    time.sleep(delay)
            uploaded.append(f"s3://{svc.store.remote.bucket}/{key}")
    return {"local_folder": str(folder), "files": manifest, "uploaded": uploaded}
