"""Sensor forecasting: future engine temperature, oil pressure and vibration per aircraft.

Method (transparent by design - every number can be recomputed by hand):
  forecast(t) = level + profile[hour_of_day(t)]
  * level   - exponentially weighted mean (half-life ``LEVEL_HALFLIFE_HOURS``) of the aircraft's hourly values over the
              last ``WINDOW_DAYS`` before the forecast origin;
  * profile - the mean deviation from each day's own mean, per hour of day, over the same window (daily pattern).
  Uncertainty: an 80 % interval from out-of-sample forecast errors, per metric and lead-time bucket.

Validation (``backtest``) is a rolling-origin evaluation on recorded history, per horizon and per metric:
  * the method is compared with two naive baselines - PERSISTENCE (the last reading repeated) and SEASONAL-NAIVE
    (the last 24 hours repeated) - by mean absolute error on the same points;
  * interval quantiles are fitted on the EARLIER origins and their coverage judged on the LATER origins, so the
    reported coverage is out-of-sample (the same discipline as the failure model's calibration);
  * a (horizon, metric) pair is SUPPORTED only if it beats BOTH baselines, its interval coverage lies within the
    configured band, and enough evaluation origins exist. Unsupported metrics are never forecast (null values).
Forecasts are anchored at each aircraft's LAST RECORDED reading; every point is strictly after it.
"""
from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from typing import Any

import numpy as np
import pandas as pd

from .features import PSI_TO_KPA

MODEL_TYPE = "SensorForecast"
MODEL_NAME = "sensor_forecast_level_profile"
ALGORITHM = "Level + hour-of-day profile (EWMA level, 28-day window)"

# Backend SensorForecastHorizon -> hours ahead.
HORIZON_HOURS = {"OneDay": 24, "SevenDays": 168, "ThirtyDays": 720}
# API field -> column in the hourly frame.
METRICS = {"temperatureC": "temperature_c", "pressurePsi": "pressure_psi", "vibrationIndex": "vibration_index"}
WINDOW_DAYS = 28
LEVEL_HALFLIFE_HOURS = 72
INTERVAL_QUANTILES = (0.10, 0.90)  # nominal 80 % interval
LEAD_BUCKETS = ((1, 24), (25, 168), (169, 720))  # lead-time buckets (hours) for interval widths


@dataclass(frozen=True)
class ValidationCriteria:
    max_mae_ratio: float = 0.95        # model MAE / best naive MAE must be AT MOST this (a meaningful improvement)
    coverage_low: float = 0.70         # out-of-sample coverage of the nominal 80 % interval
    coverage_high: float = 0.90
    min_eval_origins: int = 5          # per horizon, in the later (evaluation) half
    origin_step_hours: int = 72        # one backtest origin every 3 days

    def to_dict(self) -> dict[str, Any]:
        return {"max_mae_ratio_vs_best_naive": self.max_mae_ratio, "interval_nominal": 0.80,
                "coverage_band": [self.coverage_low, self.coverage_high], "min_eval_origins": self.min_eval_origins,
                "origin_step_hours": self.origin_step_hours, "baselines": ["persistence", "seasonal_naive_24h"]}


def hourly_frame(tel: pd.DataFrame) -> dict[str, pd.DataFrame]:
    """Per aircraft: hourly means (UTC hour start) of the Valid readings, pressure in PSI. Gaps stay NaN."""
    if tel is None or tel.empty:
        return {}
    df = tel.copy()
    if "quality_status" in df:
        df = df[df["quality_status"].fillna("Valid") == "Valid"]
    df["t"] = pd.to_datetime(df["recorded_at_utc"], utc=True).dt.floor("h")
    df["pressure_psi"] = df["pressure_kpa"] / PSI_TO_KPA
    out = {}
    for aircraft, g in df.groupby(df["aircraft_id"].astype(str)):
        h = g.groupby("t")[list(METRICS.values())].mean().sort_index()
        full = pd.date_range(h.index.min(), h.index.max(), freq="h", tz="UTC")
        out[aircraft] = h.reindex(full)
    return out


def fit_point_forecast(history: pd.Series, origin: pd.Timestamp, lead_hours: int) -> np.ndarray:
    """Level + hour-of-day profile from the ``WINDOW_DAYS`` before ``origin`` (inclusive), for leads 1..lead_hours."""
    window = history[(history.index > origin - pd.Timedelta(days=WINDOW_DAYS)) & (history.index <= origin)].dropna()
    if len(window) < 24:
        return np.full(lead_hours, np.nan)
    level = window.ewm(halflife=pd.Timedelta(hours=LEVEL_HALFLIFE_HOURS), times=window.index).mean().iloc[-1]
    daily_mean = window.groupby(window.index.floor("D")).transform("mean")
    profile = (window - daily_mean).groupby(window.index.hour).mean().reindex(range(24)).fillna(0.0)
    hours = (origin + pd.to_timedelta(np.arange(1, lead_hours + 1), unit="h")).hour
    return level + profile.to_numpy()[hours]


def _baselines(history: pd.Series, origin: pd.Timestamp, lead_hours: int) -> tuple[np.ndarray, np.ndarray]:
    past = history[history.index <= origin].dropna()
    persistence = np.full(lead_hours, past.iloc[-1] if len(past) else np.nan)
    last_day = history[(history.index > origin - pd.Timedelta(hours=24)) & (history.index <= origin)]
    pattern = last_day.reindex(pd.date_range(origin - pd.Timedelta(hours=23), origin, freq="h", tz="UTC")).ffill().bfill()
    seasonal = np.resize(pattern.to_numpy(), lead_hours) if pattern.notna().any() else np.full(lead_hours, np.nan)
    return persistence, seasonal


def _bucket(lead: int) -> str:
    for lo, hi in LEAD_BUCKETS:
        if lo <= lead <= hi:
            return f"{lo}-{hi}"
    return f"{LEAD_BUCKETS[-1][0]}-{LEAD_BUCKETS[-1][1]}"


def backtest(frames: dict[str, pd.DataFrame], criteria: ValidationCriteria) -> dict[str, Any]:
    """Rolling-origin evaluation. Returns per-horizon, per-metric results, the interval quantiles and support."""
    results: dict[str, Any] = {}
    quantiles: dict[str, dict[str, list[float]]] = {}
    supported: dict[str, list[str]] = {}
    for horizon, lead_hours in HORIZON_HOURS.items():
        rows = []  # one per (origin, aircraft, metric, lead) with an observed actual
        for aircraft, frame in frames.items():
            if frame.empty:
                continue
            start = frame.index.min() + pd.Timedelta(days=WINDOW_DAYS)
            end = frame.index.max() - pd.Timedelta(hours=lead_hours)
            if end <= start:
                continue
            for origin in pd.date_range(start, end, freq=f"{criteria.origin_step_hours}h", tz="UTC"):
                future_idx = origin + pd.to_timedelta(np.arange(1, lead_hours + 1), unit="h")
                for field, col in METRICS.items():
                    series = frame[col]
                    actual = series.reindex(future_idx).to_numpy()
                    if np.isnan(actual).all():
                        continue
                    pred = fit_point_forecast(series, origin, lead_hours)
                    pers, seas = _baselines(series, origin, lead_hours)
                    for k in np.flatnonzero(~np.isnan(actual) & ~np.isnan(pred)):
                        rows.append((origin, field, k + 1, actual[k], pred[k], pers[k], seas[k]))
        df = pd.DataFrame(rows, columns=["origin", "metric", "lead", "actual", "model", "persistence", "seasonal"])
        results[horizon] = {}
        supported[horizon] = []
        quantiles[horizon] = {}
        if df.empty:
            for field in METRICS:
                results[horizon][field] = {"supported": False, "reason": "not enough history for this horizon"}
            continue
        origins = np.sort(df["origin"].unique())
        cut = origins[len(origins) // 2]           # earlier half fits intervals, later half evaluates
        fit, ev = df[df["origin"] < cut], df[df["origin"] >= cut]
        for field in METRICS:
            f, e = fit[fit["metric"] == field], ev[ev["metric"] == field]
            n_eval_origins = int(e["origin"].nunique())
            if e.empty or f.empty:
                results[horizon][field] = {"supported": False, "reason": "not enough evaluation data", "eval_origins": n_eval_origins}
                continue
            q = {}
            err_fit = f["actual"] - f["model"]
            for lo, hi in LEAD_BUCKETS:
                b = err_fit[(f["lead"] >= lo) & (f["lead"] <= hi)]
                if len(b):
                    q[f"{lo}-{hi}"] = [float(b.quantile(INTERVAL_QUANTILES[0])), float(b.quantile(INTERVAL_QUANTILES[1]))]
            quantiles[horizon][field] = q
            lo_q = e["lead"].map(lambda L: q.get(_bucket(L), [np.nan, np.nan])[0])
            hi_q = e["lead"].map(lambda L: q.get(_bucket(L), [np.nan, np.nan])[1])
            inside = (e["actual"] >= e["model"] + lo_q) & (e["actual"] <= e["model"] + hi_q)
            mae = {k: float((e["actual"] - e[k]).abs().mean()) for k in ("model", "persistence", "seasonal")}
            best_naive = min(mae["persistence"], mae["seasonal"])
            ratio = mae["model"] / best_naive if best_naive > 0 else float("inf")
            coverage = float(inside.mean())
            checks = {"beats_both_naive_baselines": ratio <= criteria.max_mae_ratio,
                      "interval_coverage_in_band": criteria.coverage_low <= coverage <= criteria.coverage_high,
                      "enough_eval_origins": n_eval_origins >= criteria.min_eval_origins}
            ok = all(checks.values())
            results[horizon][field] = {
                "supported": ok, "checks": checks, "mae_model": mae["model"], "mae_persistence": mae["persistence"],
                "mae_seasonal_naive": mae["seasonal"], "mae_ratio_vs_best_naive": ratio, "interval_coverage": coverage,
                "eval_origins": n_eval_origins, "eval_points": int(len(e)), "fit_origins": int(f["origin"].nunique()),
            }
            if ok:
                supported[horizon].append(field)
    return {"results": results, "interval_quantiles": quantiles,
            "supported": {h: m for h, m in supported.items() if m}, "criteria": criteria.to_dict()}


def forecast_points(frame: pd.DataFrame, horizon: str, metrics: list[str], quantiles: dict[str, Any]) -> tuple[pd.Timestamp, list[dict]]:
    """Hourly points strictly after the aircraft's last recorded reading; only ``metrics`` (validated) get values."""
    lead_hours = HORIZON_HOURS[horizon]
    anchor = frame.dropna(how="all").index.max()
    times = anchor + pd.to_timedelta(np.arange(1, lead_hours + 1), unit="h")
    points = [{"timestampUtc": t.isoformat().replace("+00:00", "Z")} for t in times]
    for field, col in METRICS.items():
        validated = field in metrics
        pred = fit_point_forecast(frame[col], anchor, lead_hours) if validated else None
        q = quantiles.get(horizon, {}).get(field, {})
        for i, p in enumerate(points):
            value = None if pred is None or np.isnan(pred[i]) else float(pred[i])
            band = q.get(_bucket(i + 1)) if value is not None else None
            p[field] = None if value is None else round(value, 3)
            p[field + "Low"] = None if band is None else round(value + band[0], 3)
            p[field + "High"] = None if band is None else round(value + band[1], 3)
    return anchor, points


def utc_iso(ts: pd.Timestamp | dt.datetime) -> str:
    return pd.Timestamp(ts).tz_convert("UTC").isoformat().replace("+00:00", "Z")
