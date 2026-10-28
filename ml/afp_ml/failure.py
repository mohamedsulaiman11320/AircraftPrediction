"""Supervised failure prediction (only reachable when the readiness gate is ``Ready``).

* Examples: aircraft-day rows (cutoff = 00:00 UTC), features from telemetry strictly before cutoff.
* Label: verified unscheduled maintenance in (cutoff, cutoff + horizon].
* Chronological train/validation/test split with purge gaps >= horizon (no temporal leakage).
* Model: the approved specification - SimpleImputer(median) + RandomForestClassifier(n_estimators=300,
  max_depth=10, class_weight="balanced", random_state=42).  LogisticRegression and
  HistGradientBoosting are still fitted and reported, for comparison only; they are never selected.
* The selected model is sigmoid-calibrated on validation and evaluated ONCE on the untouched
  test split.  Metrics are computed, never assumed.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import numpy as np
import pandas as pd
import sklearn
from sklearn.calibration import CalibratedClassifierCV
from sklearn.dummy import DummyClassifier
from sklearn.ensemble import HistGradientBoostingClassifier, RandomForestClassifier
from sklearn.frozen import FrozenEstimator
from sklearn.impute import SimpleImputer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (average_precision_score, confusion_matrix, f1_score,
                             precision_recall_curve, precision_score, recall_score, roc_auc_score)
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

from .features import ChronoSplit, failure_feature_names

MODEL_NAME = "afp-failure-prediction"
MODEL_TYPE = "FailurePrediction"

LIMITATIONS_TEMPLATE = (
    "Supervised model estimating whether verified unscheduled maintenance occurs within {h} days of a daily cutoff, "
    "trained only on telemetry-derived features (temperature_c, pressure_kpa aggregates, data-quality and recency) "
    "available before the cutoff. Labels come from maintenance_log and inherit its completeness and verification "
    "quality; unrecorded events appear as negatives. Performance was measured on a single chronological test period "
    "and may not hold for other fleets, seasons or sensor configurations. Probabilities are only meaningful when "
    "metrics.calibration_validated is true. Outputs are advisory, require review by qualified maintenance personnel "
    "and must never change aircraft operational status automatically."
)


class CandidateSpec:
    def __init__(self, key: str, algorithm: str, factory: Any) -> None:
        self.key, self.algorithm, self.factory = key, algorithm, factory


# The approved model specification. It is always the selected candidate.
SPECIFIED_CANDIDATE = "random_forest"


def candidate_specs(random_state: int) -> list[CandidateSpec]:
    def imputer() -> SimpleImputer:
        return SimpleImputer(strategy="median", add_indicator=True, keep_empty_features=True)

    return [
        # Exactly the specified pipeline. keep_empty_features only keeps the column count stable
        # when a feature is entirely missing in training; it does not change any imputed value.
        CandidateSpec(SPECIFIED_CANDIDATE, "RandomForest", lambda: Pipeline([
            ("imputer", SimpleImputer(strategy="median", keep_empty_features=True)),
            ("model", RandomForestClassifier(n_estimators=300, max_depth=10, class_weight="balanced",
                                             random_state=random_state, n_jobs=-1))])),
        CandidateSpec("logistic_regression", "LogisticRegression", lambda: Pipeline([
            ("imputer", imputer()), ("scaler", StandardScaler()),
            ("model", LogisticRegression(class_weight="balanced", max_iter=5000))])),
        CandidateSpec("hist_gradient_boosting", "HistGradientBoosting", lambda: Pipeline([
            ("imputer", imputer()),
            ("model", HistGradientBoostingClassifier(class_weight="balanced", learning_rate=0.05, max_iter=200,
                                                     random_state=random_state))])),
    ]


def reliability_bins(y: np.ndarray, p: np.ndarray, n_bins: int = 10) -> tuple[list[dict[str, Any]], float]:
    edges = np.linspace(0.0, 1.0, n_bins + 1)
    idx = np.clip(np.digitize(p, edges[1:-1], right=False), 0, n_bins - 1)
    bins, ece = [], 0.0
    for b in range(n_bins):
        mask = idx == b
        n = int(mask.sum())
        entry = {"bin_lower": float(edges[b]), "bin_upper": float(edges[b + 1]), "count": n,
                 "mean_predicted": float(p[mask].mean()) if n else None,
                 "observed_frequency": float(y[mask].mean()) if n else None}
        if n:
            ece += (n / len(p)) * abs(entry["mean_predicted"] - entry["observed_frequency"])
        bins.append(entry)
    return bins, float(ece)


def evaluate_binary(y_true: np.ndarray, prob: np.ndarray, threshold: float) -> dict[str, Any]:
    y = np.asarray(y_true).astype(int)
    p = np.clip(np.asarray(prob, dtype="float64"), 0.0, 1.0)
    pred = (p >= threshold).astype(int)
    tn, fp, fn, tp = confusion_matrix(y, pred, labels=[0, 1]).ravel()
    positives = int(y.sum())
    prevalence = float(y.mean()) if len(y) else None
    bins, ece = reliability_bins(y, p)
    brier = float(np.mean((p - y) ** 2)) if len(y) else None  # Brier score (same as brier_score_loss)
    baseline = float(np.mean((prevalence - y) ** 2)) if len(y) else None
    pr_auc = float(average_precision_score(y, p)) if positives else None
    # ROC-AUC is undefined unless both classes are present; report None rather than a meaningless value.
    roc_auc = float(roc_auc_score(y, p)) if 0 < positives < len(y) else None
    return {
        "rows": int(len(y)), "positives": positives, "negatives": int(len(y) - positives), "prevalence": prevalence,
        "threshold": float(threshold),
        "precision": float(precision_score(y, pred, zero_division=0)),
        "recall": float(recall_score(y, pred, zero_division=0)),
        "f1": float(f1_score(y, pred, zero_division=0)),
        "pr_auc": pr_auc,
        "roc_auc": roc_auc,
        "specificity": float(tn / (tn + fp)) if (tn + fp) else None,
        # PR-AUC of a no-skill classifier equals the prevalence of the evaluated split.
        "pr_auc_baseline": prevalence,
        "pr_auc_lift": (pr_auc / prevalence) if (pr_auc is not None and prevalence) else None,
        "false_alert_rate": float(fp / (fp + tn)) if (fp + tn) else None,
        "brier": brier,
        "baseline_brier_prevalence": baseline,
        "brier_skill_score": (1 - brier / baseline) if (brier is not None and baseline) else None,
        "expected_calibration_error": ece,
        "reliability_bins": bins,
        "confusion_matrix": {"tp": int(tp), "fp": int(fp), "tn": int(tn), "fn": int(fn)},
    }


BASELINE_MODEL = "DummyClassifier(strategy='prior')"
HELD_OUT_FOLD_KEYS = ("rows", "positives", "negatives", "prevalence", "precision", "recall", "roc_auc", "pr_auc",
                      "brier", "baseline_brier_prevalence", "brier_skill_score", "expected_calibration_error",
                      "reliability_bins")
BASELINE_METRIC_KEYS = ("rows", "positives", "prevalence", "threshold", "pr_auc", "precision", "recall")
FALSE_ALERT_PERIOD_DAYS = 30


def baseline_metrics(Xtr: np.ndarray, ytr: np.ndarray, splits: dict[str, tuple[np.ndarray, np.ndarray]],
                     threshold: float) -> dict[str, Any]:
    """Prior-probability baseline fitted on train and evaluated on the given splits."""
    dummy = DummyClassifier(strategy="prior").fit(Xtr, ytr)
    out: dict[str, Any] = {"model": BASELINE_MODEL}
    for name, (X, y) in splits.items():
        m = evaluate_binary(y, dummy.predict_proba(X)[:, 1], threshold)
        out[name] = {k: m[k] for k in BASELINE_METRIC_KEYS}
    return out


def false_alerts_per_aircraft_period(frame: pd.DataFrame, false_positives: int,
                                     period_days: int = FALSE_ALERT_PERIOD_DAYS) -> float | None:
    """FP / (distinct aircraft x covered days / period_days); None when it cannot be computed."""
    if "aircraft_id" not in frame.columns or "cutoff_utc" not in frame.columns or frame.empty:
        return None
    cutoffs = pd.to_datetime(frame["cutoff_utc"], utc=True).dropna()
    n_aircraft = int(frame["aircraft_id"].dropna().nunique())
    if cutoffs.empty or n_aircraft == 0:
        return None
    days = (cutoffs.max() - cutoffs.min()).total_seconds() / 86400.0 + 1.0  # daily cutoffs, inclusive
    exposure = n_aircraft * days / period_days
    return float(false_positives / exposure) if exposure > 0 else None


def best_f1_threshold(y: np.ndarray, p: np.ndarray) -> float:
    precision, recall, thresholds = precision_recall_curve(y, p)
    if len(thresholds) == 0:
        return 0.5
    f1 = np.where((precision + recall) > 0, 2 * precision * recall / np.maximum(precision + recall, 1e-12), 0.0)[:-1]
    return float(thresholds[int(np.argmax(f1))])


@dataclass
class FailureTrainingResult:
    bundle: dict[str, Any]
    algorithm: str
    feature_names: list[str]
    hyperparameters: dict[str, Any]
    metrics: dict[str, Any]
    training_rows: int


def train_failure_model(split: ChronoSplit, *, horizon_days: int, random_state: int, calibration_max_ece: float,
                        calibration_min_test_positives: int) -> FailureTrainingResult:
    names = failure_feature_names()
    for part_name, part in (("train", split.train), ("validation", split.validation), ("test", split.test)):
        if part["label"].nunique() < 2:
            raise ValueError(f"the {part_name} split does not contain both classes")
    Xtr, ytr = split.train[names].to_numpy("float64"), split.train["label"].to_numpy(int)
    Xva, yva = split.validation[names].to_numpy("float64"), split.validation["label"].to_numpy(int)
    Xte, yte = split.test[names].to_numpy("float64"), split.test["label"].to_numpy(int)

    candidates: dict[str, Any] = {}
    fitted: dict[str, Any] = {}
    for spec in candidate_specs(random_state):
        model = spec.factory()
        model.fit(Xtr, ytr)
        fitted[spec.key] = (spec, model)
        p_va = model.predict_proba(Xva)[:, 1]
        candidates[spec.key] = {"algorithm": spec.algorithm,
                                "validation_uncalibrated": evaluate_binary(yva, p_va, 0.5),
                                "params": _jsonable_params(model.named_steps["model"].get_params())}
    selected_key = SPECIFIED_CANDIDATE
    spec, model = fitted[selected_key]
    forest = model.named_steps["model"]
    importances = sorted(zip(names, (float(v) for v in forest.feature_importances_)), key=lambda kv: -kv[1])

    calibrated = CalibratedClassifierCV(FrozenEstimator(model), method="sigmoid")
    calibrated.fit(Xva, yva)
    p_va_cal = calibrated.predict_proba(Xva)[:, 1]
    threshold = best_f1_threshold(yva, p_va_cal)
    p_te = calibrated.predict_proba(Xte)[:, 1]
    test_metrics = evaluate_binary(yte, p_te, threshold)
    validation_metrics = evaluate_binary(yva, p_va_cal, threshold)

    criteria = {
        "min_test_positives": calibration_min_test_positives,
        "max_expected_calibration_error": calibration_max_ece,
        "brier_skill_score_must_exceed": 0.0,
    }
    checks = {
        "test_positives_ok": test_metrics["positives"] >= calibration_min_test_positives,
        "ece_ok": test_metrics["expected_calibration_error"] <= calibration_max_ece,
        "brier_skill_ok": (test_metrics["brier_skill_score"] or 0.0) > 0.0,
    }
    calibration_validated = all(checks.values())
    baseline = baseline_metrics(Xtr, ytr, {"validation": (Xva, yva), "test": (Xte, yte)}, threshold)
    base_pr = baseline["test"]["pr_auc"]
    beats_baseline = (test_metrics["pr_auc"] > base_pr) if (test_metrics["pr_auc"] is not None
                                                             and base_pr is not None) else None
    fa_per_aircraft = false_alerts_per_aircraft_period(split.test, test_metrics["confusion_matrix"]["fp"])

    metrics = {
        "metric_type": "supervised_chronological_holdout",
        "horizon_days": horizon_days,
        "selection_metric": "approved_specification",
        "selected_candidate": selected_key,
        "feature_importance": [{"feature": f, "importance": v} for f, v in importances],
        "selected_algorithm": spec.algorithm,
        "decision_threshold": threshold,
        "decision_threshold_rule": "maximises F1 on the calibrated validation predictions",
        "calibration_method": "sigmoid (Platt) fitted on the validation split",
        "candidates": candidates,
        "validation": validation_metrics,
        "test": test_metrics,
        "precision": test_metrics["precision"], "recall": test_metrics["recall"], "pr_auc": test_metrics["pr_auc"],
        "false_alert_rate": test_metrics["false_alert_rate"], "brier": test_metrics["brier"],
        "pr_auc_baseline": test_metrics["pr_auc_baseline"], "pr_auc_lift": test_metrics["pr_auc_lift"],
        "baseline": baseline,
        "beats_baseline": beats_baseline,
        "false_alerts_per_aircraft_per_30_days": fa_per_aircraft,
        "false_alerts_per_aircraft_definition": (
            f"test-split false positives / (distinct aircraft x test-period days / {FALSE_ALERT_PERIOD_DAYS})"),
        "calibration_validated": calibration_validated,
        "calibration_criteria": criteria,
        "calibration_checks": checks,
        "split": {"train_rows": len(split.train), "validation_rows": len(split.validation), "test_rows": len(split.test),
                  "train_positives": int(ytr.sum()), "validation_positives": int(yva.sum()),
                  "test_positives": int(yte.sum()), "purge_gap_days": split.purge_gap_days,
                  "purged_rows": split.purged_rows, "boundaries": split.boundaries},
        "sklearn_version": sklearn.__version__,
    }
    hyper = {"selected": candidates[selected_key]["params"], "class_imbalance": "class_weight balanced",
             "imputer": "SimpleImputer(median, add_indicator=True)", "random_state": random_state}
    bundle = {
        "kind": MODEL_TYPE, "algorithm": spec.algorithm, "pipeline": calibrated, "feature_names": names,
        "feature_config": {"type": "failure_aircraft_day", "windows_days": [1, 7, 30]},
        "decision_threshold": threshold,
        "risk_thresholds": {"high": threshold, "elevated": threshold / 2.0},
        "calibration_validated": calibration_validated, "horizon_days": horizon_days,
        "sklearn_version": sklearn.__version__,
    }
    return FailureTrainingResult(bundle, spec.algorithm, names, hyper, metrics, int(len(split.train)))


def _jsonable_params(params: dict[str, Any]) -> dict[str, Any]:
    out = {}
    for k, v in params.items():
        out[k] = v if isinstance(v, (int, float, str, bool, type(None))) else str(v)
    return out


# --------------------------------------------------------------------------------------------
# Rolling-origin (expanding-window) evaluation
# --------------------------------------------------------------------------------------------

class PlattCalibratedModel:
    """The deployed model: a fitted pipeline plus a Platt (sigmoid) mapping learned from OUT-OF-FOLD scores.

    Module-level and dependency-free so the joblib artifact unpickles anywhere ``afp_ml`` is installed.
    """

    def __init__(self, base: Any, coef: float, intercept: float) -> None:
        self.base, self.coef, self.intercept = base, float(coef), float(intercept)

    def predict_proba(self, X: np.ndarray) -> np.ndarray:
        score = self.base.predict_proba(X)[:, 1]
        p = 1.0 / (1.0 + np.exp(-(self.coef * score + self.intercept)))
        return np.column_stack([1.0 - p, p])


def _platt(scores: np.ndarray, y: np.ndarray) -> tuple[float, float]:
    lr = LogisticRegression(C=1e6, max_iter=1000).fit(scores.reshape(-1, 1), y)
    return float(lr.coef_[0, 0]), float(lr.intercept_[0])


def _apply_platt(scores: np.ndarray, coef: float, intercept: float) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-(coef * scores + intercept)))


def rolling_origin_folds(cutoffs: pd.Series, n_folds: int, purge_days: int) -> list[dict[str, Any]]:
    """Time is cut into ``n_folds + 1`` equal blocks; block 0 only trains. Fold k tests block k and trains on every
    cutoff at least ``purge_days`` before that block starts, so no training label window (c, c + horizon] reaches
    into the test block when purge_days >= horizon."""
    cut = pd.to_datetime(cutoffs, utc=True)
    t0, t1 = cut.min(), cut.max()
    block = (t1 - t0) / (n_folds + 1)
    gap = pd.Timedelta(days=purge_days)
    folds = []
    for k in range(1, n_folds + 1):
        start = t0 + block * k
        end = t1 if k == n_folds else start + block
        folds.append({"fold": k, "train": (cut <= start - gap).to_numpy(), "test": ((cut > start) & (cut <= end)).to_numpy(),
                      "boundaries": {"train_to": (start - gap).isoformat(), "test_from_exclusive": start.isoformat(),
                                     "test_to": end.isoformat()}})
    return folds


def train_failure_model_rolling(examples: pd.DataFrame, *, horizon_days: int, random_state: int, n_folds: int,
                                calibration_max_ece: float, calibration_min_test_positives: int) -> FailureTrainingResult:
    """Specified RandomForest evaluated by rolling-origin cross-validation (the standard for time series).

    * Each fold model is trained only on the past (purge gap = horizon) and scores a later block it never saw.
    * The Platt calibration is fitted on the EARLIER folds' out-of-fold scores and judged on the LATER folds' - so
      ``metrics.test`` is data that neither a fold model nor the calibrator saw. ``metrics.validation`` holds the
      earlier folds (calibration fitted there; the decision threshold is chosen there too).
    * The deployed model is the specified pipeline refitted on all examples, with a Platt mapping refitted on all
      out-of-fold scores. Calibration is validated only if the existing criteria pass on ``metrics.test``.
    """
    names = failure_feature_names()
    spec = next(c for c in candidate_specs(random_state) if c.key == SPECIFIED_CANDIDATE)
    ex = examples.sort_values("cutoff_utc", kind="mergesort").reset_index(drop=True)
    X, y = ex[names].to_numpy("float64"), ex["label"].to_numpy(int)
    if len(set(y)) < 2:
        raise ValueError("Training cannot continue because the target contains only one class.")

    folds, oof = [], []
    for f in rolling_origin_folds(ex["cutoff_utc"], n_folds, horizon_days):
        tr, te = f["train"], f["test"]
        info = {"fold": f["fold"], **f["boundaries"], "train_rows": int(tr.sum()), "test_rows": int(te.sum()),
                "test_positives": int(y[te].sum()), "test_negatives": int(te.sum() - y[te].sum())}
        if te.sum() == 0 or len(set(y[tr])) < 2:
            folds.append({**info, "used": False, "reason": "training block has one class" if te.sum() else "empty test block"})
            continue
        model = spec.factory().fit(X[tr], y[tr])
        scores = model.predict_proba(X[te])[:, 1]
        folds.append({**info, "used": True,
                      "raw": {k: v for k, v in evaluate_binary(y[te], scores, 0.5).items() if k != "reliability_bins"}})
        oof.append((f["fold"], np.flatnonzero(te), scores))
    if len(oof) < 2:
        raise ValueError(f"rolling-origin evaluation needs at least 2 usable folds; got {len(oof)} of {n_folds}")

    n_fit = max(1, len(oof) // 2)
    fit_idx = np.concatenate([i for _, i, _ in oof[:n_fit]]); fit_s = np.concatenate([s for _, _, s in oof[:n_fit]])
    ev_idx = np.concatenate([i for _, i, _ in oof[n_fit:]]); ev_s = np.concatenate([s for _, _, s in oof[n_fit:]])
    for part, idx in (("calibration-fit", fit_idx), ("held-out test", ev_idx)):
        if len(set(y[idx])) < 2:
            raise ValueError(f"the {part} folds do not contain both classes")
    coef, intercept = _platt(fit_s, y[fit_idx])
    p_fit, p_ev = _apply_platt(fit_s, coef, intercept), _apply_platt(ev_s, coef, intercept)
    threshold = best_f1_threshold(y[fit_idx], p_fit)
    validation_metrics = evaluate_binary(y[fit_idx], p_fit, threshold)
    test_metrics = evaluate_binary(y[ev_idx], p_ev, threshold)
    # Each held-out fold on its own (same mapping, same threshold): shows drift that the pooled test can hide.
    held_out_by_fold = []
    for fold, idx, scores in oof[n_fit:]:
        m = evaluate_binary(y[idx], _apply_platt(scores, coef, intercept), threshold)
        held_out_by_fold.append({"fold": fold, **{k: m[k] for k in HELD_OUT_FOLD_KEYS}})

    criteria = {"min_test_positives": calibration_min_test_positives,
                "max_expected_calibration_error": calibration_max_ece, "brier_skill_score_must_exceed": 0.0}
    checks = {"test_positives_ok": test_metrics["positives"] >= calibration_min_test_positives,
              "ece_ok": test_metrics["expected_calibration_error"] <= calibration_max_ece,
              "brier_skill_ok": (test_metrics["brier_skill_score"] or 0.0) > 0.0}
    calibration_validated = all(checks.values())

    all_idx = np.concatenate([i for _, i, _ in oof]); all_s = np.concatenate([s for _, _, s in oof])
    pooled_raw = {k: v for k, v in evaluate_binary(y[all_idx], all_s, 0.5).items() if k != "reliability_bins"}
    final_base = spec.factory().fit(X, y)
    final_coef, final_intercept = _platt(all_s, y[all_idx])
    deployed = PlattCalibratedModel(final_base, final_coef, final_intercept)
    importances = sorted(zip(names, (float(v) for v in final_base.named_steps["model"].feature_importances_)),
                         key=lambda kv: -kv[1])
    prior = float(y[fit_idx].mean())
    base_test = evaluate_binary(y[ev_idx], np.full(len(ev_idx), prior), threshold)
    params = _jsonable_params(final_base.named_steps["model"].get_params())
    metrics = {
        "metric_type": "rolling_origin_cross_validation",
        "evaluation": {"folds_requested": n_folds, "folds_used": len(oof), "calibration_fit_folds": [f for f, _, _ in oof[:n_fit]],
                       "held_out_test_folds": [f for f, _, _ in oof[n_fit:]], "purge_gap_days": horizon_days,
                       "description": "expanding window; each fold trains on the past only (purge gap = horizon) and "
                                      "scores a later block; calibration fitted on earlier folds, judged on later ones"},
        "folds": folds,
        "horizon_days": horizon_days,
        "selection_metric": "approved_specification",
        "selected_candidate": SPECIFIED_CANDIDATE, "selected_algorithm": spec.algorithm,
        "feature_importance": [{"feature": f, "importance": v} for f, v in importances],
        "decision_threshold": threshold,
        "decision_threshold_rule": "maximises F1 on the calibrated calibration-fit folds",
        "calibration_method": "Platt (sigmoid) on out-of-fold scores; deployed mapping refitted on all folds",
        "candidates": {SPECIFIED_CANDIDATE: {"algorithm": spec.algorithm, "params": params}},
        "validation": validation_metrics, "test": test_metrics, "held_out_by_fold": held_out_by_fold,
        "pooled_out_of_fold_raw": pooled_raw,
        "precision": test_metrics["precision"], "recall": test_metrics["recall"], "pr_auc": test_metrics["pr_auc"],
        "false_alert_rate": test_metrics["false_alert_rate"], "brier": test_metrics["brier"],
        "pr_auc_baseline": test_metrics["pr_auc_baseline"], "pr_auc_lift": test_metrics["pr_auc_lift"],
        "baseline": {"model": BASELINE_MODEL, "test": {k: base_test[k] for k in BASELINE_METRIC_KEYS}},
        "beats_baseline": (test_metrics["pr_auc"] > base_test["pr_auc"]) if (test_metrics["pr_auc"] is not None
                                                                             and base_test["pr_auc"] is not None) else None,
        "calibration_validated": calibration_validated, "calibration_criteria": criteria, "calibration_checks": checks,
        "split": {"train_rows": int(len(ex)), "validation_rows": int(len(fit_idx)), "test_rows": int(len(ev_idx)),
                  "train_positives": int(y.sum()), "validation_positives": int(y[fit_idx].sum()),
                  "test_positives": int(y[ev_idx].sum()), "purge_gap_days": horizon_days,
                  "boundaries": {f"fold_{f['fold']}": {k: f[k] for k in ("train_to", "test_from_exclusive", "test_to")}
                                 for f in folds}},
        "sklearn_version": sklearn.__version__,
    }
    hyper = {"selected": params, "class_imbalance": "class_weight balanced", "imputer": "SimpleImputer(median)",
             "random_state": random_state, "calibration": {"coef": final_coef, "intercept": final_intercept}}
    bundle = {"kind": MODEL_TYPE, "algorithm": spec.algorithm, "pipeline": deployed, "feature_names": names,
              "feature_config": {"type": "failure_aircraft_day", "windows_days": [1, 7, 30]},
              "decision_threshold": threshold, "risk_thresholds": {"high": threshold, "elevated": threshold / 2.0},
              "calibration_validated": calibration_validated, "horizon_days": horizon_days,
              "sklearn_version": sklearn.__version__}
    return FailureTrainingResult(bundle, spec.algorithm, names, hyper, metrics, int(len(ex)))


def predict_failure(bundle: dict[str, Any], features: pd.DataFrame) -> np.ndarray:
    X = features[bundle["feature_names"]].to_numpy("float64")
    return bundle["pipeline"].predict_proba(X)[:, 1]
