"""Isolation Forest anomaly detection: training and scoring (pure, DataFrame-based).

Score semantics: ``anomaly_score = -score_samples(x)``.  Higher = more anomalous.  It is a
RELATIVE score and NOT a probability of failure.  ``is_anomaly`` is the Isolation Forest decision
(``anomaly_score > -offset_``, i.e. ``predict == -1``) whose cut is set by ``contamination`` on
the training data.  Severity is relative to the training score distribution.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import numpy as np
import pandas as pd
import sklearn
from sklearn.ensemble import IsolationForest
from sklearn.impute import SimpleImputer
from sklearn.pipeline import Pipeline

from .features import anomaly_feature_names, anomaly_min_periods, build_anomaly_features

MODEL_NAME = "afp-anomaly-isolation-forest"
MODEL_TYPE = "AnomalyDetection"
ALGORITHM = "IsolationForest"
PERCENTILES = (1, 5, 25, 50, 75, 90, 95, 99, 99.5, 99.9)

LIMITATIONS = (
    "Unsupervised Isolation Forest trained on historical telemetry assumed to be mostly normal. anomaly_score is "
    "-score_samples (higher = more unusual relative to the training data); it is NOT a probability of failure and "
    "does not by itself indicate a fault. The anomaly cut is set by the contamination parameter, so roughly that "
    "fraction of training readings is flagged by construction. Severity (Low/Medium/High) is relative to the training "
    "score distribution, not to engineering limits; no approved engineering thresholds are used. Features use only "
    "temperature_c and pressure_kpa plus past-only per-aircraft/sensor rolling statistics. Results must be reviewed "
    "by qualified maintenance personnel and must never change aircraft operational status automatically."
)


class TrainingDataError(Exception):
    """Not enough or unusable training data."""


def build_pipeline(contamination: float, n_estimators: int, random_state: int) -> Pipeline:
    return Pipeline([
        ("imputer", SimpleImputer(strategy="median", add_indicator=True, keep_empty_features=True)),
        ("model", IsolationForest(n_estimators=n_estimators, contamination=contamination, max_samples="auto",
                                  random_state=random_state, n_jobs=None)),
    ])


def severity_thresholds(train_scores: np.ndarray, anomaly_threshold: float, contamination: float) -> dict[str, float]:
    """Low starts at the IF decision cut; Medium/High at upper quantiles of the training scores.

    medium = quantile(1 - contamination / 2), high = quantile(1 - contamination / 10), each forced
    to be >= the previous level.
    """
    medium = float(np.quantile(train_scores, 1 - contamination / 2))
    high = float(np.quantile(train_scores, 1 - contamination / 10))
    low = float(anomaly_threshold)
    medium = max(medium, low)
    high = max(high, medium)
    return {"low": low, "medium": medium, "high": high,
            "rule": "is_anomaly when anomaly_score > low; Medium when >= medium; High when >= high",
            "medium_quantile": 1 - contamination / 2, "high_quantile": 1 - contamination / 10}


def severity_for(score: float, thresholds: dict[str, float]) -> str:
    if score >= thresholds["high"]:
        return "High"
    if score >= thresholds["medium"]:
        return "Medium"
    return "Low"


@dataclass
class AnomalyTrainingResult:
    bundle: dict[str, Any]
    feature_names: list[str]
    hyperparameters: dict[str, Any]
    metrics: dict[str, Any]
    training_rows: int
    data_from: pd.Timestamp
    data_to: pd.Timestamp


def train_anomaly_model(telemetry: pd.DataFrame, *, include_suspect: bool, contamination: float, n_estimators: int,
                        window: int, random_state: int, min_rows: int = 200) -> AnomalyTrainingResult:
    statuses = ["Valid", "Suspect"] if include_suspect else ["Valid"]
    tel = telemetry.loc[telemetry["quality_status"].astype(str).isin(statuses)]
    if len(tel) < min_rows:
        raise TrainingDataError(f"only {len(tel)} telemetry rows with quality_status in {statuses}; need >= {min_rows}")
    feats_all = build_anomaly_features(tel, window)
    feats = feats_all.loc[~feats_all["warmup"]]
    if len(feats) < min_rows:
        raise TrainingDataError(f"only {len(feats)} rows have a full rolling-window history; need >= {min_rows}")
    names = anomaly_feature_names(window)
    X = feats[names].to_numpy(dtype="float64")
    pipe = build_pipeline(contamination, n_estimators, random_state)
    pipe.fit(X)
    scores = -pipe.score_samples(X)
    model: IsolationForest = pipe.named_steps["model"]
    anomaly_threshold = float(-model.offset_)
    thresholds = severity_thresholds(scores, anomaly_threshold, contamination)
    flagged = scores > anomaly_threshold
    baseline_rates = {"anomaly_rate": float(flagged.mean()), "high_rate": float((scores >= thresholds["high"]).mean())}
    missing_rates = {n: float(np.isnan(X[:, i]).mean()) for i, n in enumerate(names)}
    imputer: SimpleImputer = pipe.named_steps["imputer"]
    hyper = {"n_estimators": n_estimators, "contamination": contamination, "max_samples": "auto",
             "random_state": random_state, "feature_window": window, "rolling_min_periods": anomaly_min_periods(window),
             "imputer": "SimpleImputer(median, add_indicator=True)", "quality_statuses": statuses}
    metrics = {
        "metric_type": "unsupervised_training_distribution",
        "note": "Isolation Forest has no ground-truth labels: these are score-distribution statistics, NOT accuracy.",
        "training_rows": int(len(X)),
        "warmup_rows_excluded": int(feats_all["warmup"].sum()),
        "aircraft": int(feats["aircraft_id"].nunique()),
        "series": int(feats.groupby(["aircraft_id", feats["sensor_id"].astype(str)]).ngroups),
        "contamination": contamination,
        "flagged_training_rows": int(flagged.sum()),
        "flagged_training_fraction": float(flagged.mean()),
        "score_percentiles": {f"p{p:g}": float(np.percentile(scores, p)) for p in PERCENTILES},
        "score_mean": float(scores.mean()), "score_std": float(scores.std()),
        "score_min": float(scores.min()), "score_max": float(scores.max()),
        "severity_thresholds": thresholds,
        "baseline_rates": baseline_rates,
        "feature_missing_rate": missing_rates,
        "imputer_medians": {n: float(v) for n, v in zip(names, imputer.statistics_)},
        "quality_statuses_used": statuses,
        "sklearn_version": sklearn.__version__,
    }
    bundle = {
        "kind": MODEL_TYPE, "algorithm": ALGORITHM, "pipeline": pipe, "feature_names": names,
        "feature_config": {"window": window, "type": "anomaly_reading_level"},
        "quality_statuses": statuses, "thresholds": thresholds, "baseline_rates": baseline_rates,
        "sklearn_version": sklearn.__version__,
    }
    t = pd.to_datetime(tel["recorded_at_utc"], utc=True)
    return AnomalyTrainingResult(bundle, names, hyper, metrics, int(len(X)), t.min(), t.max())


def score_features(bundle: dict[str, Any], features: pd.DataFrame) -> pd.DataFrame:
    """Return ``features`` + ``anomaly_score``, ``is_anomaly``, ``severity`` (severity only for anomalies)."""
    names = bundle["feature_names"]
    out = features.copy()
    if out.empty:
        out["anomaly_score"] = pd.Series(dtype="float64")
        out["is_anomaly"] = pd.Series(dtype="bool")
        out["severity"] = pd.Series(dtype="object")
        return out
    X = out[names].to_numpy(dtype="float64")
    scores = -bundle["pipeline"].score_samples(X)
    thresholds = bundle["thresholds"]
    out["anomaly_score"] = scores
    out["is_anomaly"] = scores > thresholds["low"]
    out["severity"] = [severity_for(s, thresholds) if a else None for s, a in zip(scores, out["is_anomaly"])]
    return out
