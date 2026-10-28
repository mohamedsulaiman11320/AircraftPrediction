"""Per-aircraft risk indicators.

Descriptive indicator (basis ``DescriptiveIndicator``, probability always NULL).  Because the
Isolation Forest flags roughly ``contamination`` of NORMAL readings by construction, raw anomaly
counts grow with the telemetry cadence.  The rule therefore compares the aircraft's anomaly RATE in
the last ``risk_window_days`` with the rate observed on the training data (the "baseline"):

    anomaly_ratio = (anomalies / readings) / baseline_anomaly_rate
    high_ratio    = (High-severity anomalies / readings) / baseline_high_rate

* ``Undetermined`` - no telemetry within ``risk_recency_hours``, or fewer than ``risk_min_readings``
                     readings in the window (not enough data to say anything).
* ``High``         - (anomaly_ratio >= ``risk_high_rate_multiplier`` or high_ratio >= the same
                     multiplier) and at least ``risk_high_anomaly_count`` anomalies.
* ``Elevated``     - (anomaly_ratio >= ``risk_elevated_rate_multiplier`` or high_ratio >= the same
                     multiplier) and at least ``risk_elevated_anomaly_count`` anomalies.
* ``Low``          - otherwise.

Anomalies with investigation_status ``Dismissed`` are not counted.  This is a transparent rule
over anomaly rates; it is NOT a failure probability.

Forecast override (2026-10-28, user decision): an aircraft with recent telemetry and an OPEN forecast incident
(``aircraft_anomaly_result.source = 'ForecastModel'``) is ``HighCritical`` when the forecast crosses both the
engine-temperature and the oil-pressure limits, ``Critical`` when it crosses one; the rate rule applies otherwise.
"""

from __future__ import annotations

import datetime as dt
from enum import Enum
from typing import Any

import pandas as pd

from .config import Settings
from .util import iso, new_uuid

RULE_NAME = "descriptive_anomaly_rate_v1"
FORECAST_RULE_NAME = "descriptive_forecast_condition_v1"
MEASUREMENT_TEMPERATURE = "EngineTemperature"
MEASUREMENT_OIL_PRESSURE = "OilPressure"


def forecast_category(measurements: set[str]) -> str:
    """Category of an open forecast incident from the measurements of the limits it crosses."""
    both = {MEASUREMENT_TEMPERATURE, MEASUREMENT_OIL_PRESSURE} <= measurements
    return "HighCritical" if both else "Critical"


class PredictionState(str, Enum):
    """``aircraft_risk_prediction.prediction_state`` values (the single source for these strings)."""

    SCORED = "Scored"
    INSUFFICIENT_TELEMETRY = "InsufficientTelemetry"
    MISSING_FEATURES = "MissingFeatures"


def utc_timestamp(value: Any) -> pd.Timestamp:
    ts = pd.Timestamp(value)
    return ts.tz_localize("UTC") if ts.tzinfo is None else ts.tz_convert("UTC")


def fallback_cutoff(now: Any) -> dt.datetime:
    """Feature cutoff used when no telemetry time applies: the scoring time truncated to the hour (UTC)."""
    return utc_timestamp(now).floor("h").to_pydatetime()


def descriptive_category(counts: dict[str, int], readings: int, has_recent_telemetry: bool,
                         baseline: dict[str, float], settings: Settings) -> tuple[str, str, dict[str, Any]]:
    total = int(sum(counts.values()))
    high = int(counts.get("High", 0))
    base_rate = max(float(baseline.get("anomaly_rate") or settings.if_contamination), 1e-9)
    base_high = max(float(baseline.get("high_rate") or settings.if_contamination / 10.0), 1e-9)
    rate = total / readings if readings else None
    high_rate = high / readings if readings else None
    stats = {"readings_in_window": int(readings), "anomaly_total": total, "anomaly_rate": rate,
             "high_severity_rate": high_rate, "baseline_anomaly_rate": base_rate, "baseline_high_rate": base_high,
             "anomaly_ratio": (rate / base_rate) if rate is not None else None,
             "high_ratio": (high_rate / base_high) if high_rate is not None else None}
    if not has_recent_telemetry:
        return "Undetermined", f"no telemetry in the last {settings.risk_recency_hours} hours", stats
    if readings < settings.risk_min_readings:
        return "Undetermined", (f"only {readings} readings in the last {settings.risk_window_days} days "
                                f"(minimum {settings.risk_min_readings})"), stats
    ratio = max(stats["anomaly_ratio"] or 0.0, stats["high_ratio"] or 0.0)
    if ratio >= settings.risk_high_rate_multiplier and total >= settings.risk_high_anomaly_count:
        return "High", (f"anomaly rate {ratio:.1f}x the training baseline (>= {settings.risk_high_rate_multiplier}x) "
                        f"with {total} anomalies"), stats
    if ratio >= settings.risk_elevated_rate_multiplier and total >= settings.risk_elevated_anomaly_count:
        return "Elevated", (f"anomaly rate {ratio:.1f}x the training baseline "
                            f"(>= {settings.risk_elevated_rate_multiplier}x) with {total} anomalies"), stats
    return "Low", f"anomaly rate {ratio:.1f}x the training baseline", stats


def build_descriptive_predictions(aircraft: pd.DataFrame, anomaly_counts: pd.DataFrame, readings: pd.DataFrame, *,
                                  now: dt.datetime, settings: Settings, baseline: dict[str, float],
                                  model_version_id: str, pipeline_run_id: str | None,
                                  forecast_conditions: dict[str, set[str]] | None = None) -> list[dict[str, Any]]:
    """``aircraft``: aircraft_id, registration_number, last_telemetry_at_utc.
    ``anomaly_counts``: aircraft_id, severity, count (restricted to the window).
    ``readings``: aircraft_id, readings (telemetry rows in the window with the scored quality statuses).
    ``forecast_conditions``: aircraft_id -> measurements crossed by its open forecast incident.
    """
    recency_cut = pd.Timestamp(now) - pd.Timedelta(hours=settings.risk_recency_hours)
    counts_by_aircraft: dict[str, dict[str, int]] = {}
    for row in anomaly_counts.itertuples(index=False):
        counts_by_aircraft.setdefault(str(row.aircraft_id), {})[str(row.severity)] = int(row.count)
    readings_by_aircraft = {str(r.aircraft_id): int(r.readings) for r in readings.itertuples(index=False)}
    rows = []
    for a in aircraft.itertuples(index=False):
        aid = str(a.aircraft_id)
        last = a.last_telemetry_at_utc
        has_last = last is not None and not pd.isna(last)
        recent = has_last and pd.Timestamp(last) >= recency_cut
        counts = counts_by_aircraft.get(aid, {})
        category, reason, stats = descriptive_category(counts, readings_by_aircraft.get(aid, 0), recent, baseline,
                                                       settings)
        breached = (forecast_conditions or {}).get(aid)
        rule = RULE_NAME
        if recent and breached:
            category, rule = forecast_category(breached), FORECAST_RULE_NAME
            reason = f"open 30-day forecast incident crossing the {' and '.join(sorted(breached))} limit(s)"
        state = PredictionState.INSUFFICIENT_TELEMETRY if category == "Undetermined" else PredictionState.SCORED
        cutoff = utc_timestamp(last).to_pydatetime() if has_last else fallback_cutoff(now)
        rows.append({
            "prediction_state": state.value, "feature_cutoff_utc": cutoff,
            "id": new_uuid(), "aircraft_id": aid, "component_id": None,
            "prediction_timestamp_utc": now, "horizon_days": settings.prediction_horizon_days,
            "risk_category": category, "basis": "DescriptiveIndicator", "probability": None,
            "probability_validated": False,
            "feature_snapshot": {
                "rule": rule, "reason": reason, "forecast_breached_measurements": sorted(breached or []),
                "window_days": settings.risk_window_days, "recency_hours": settings.risk_recency_hours,
                "anomaly_counts": {s: counts.get(s, 0) for s in ("Low", "Medium", "High")},
                **stats,
                "elevated_rate_multiplier": settings.risk_elevated_rate_multiplier,
                "high_rate_multiplier": settings.risk_high_rate_multiplier,
                "elevated_min_anomalies": settings.risk_elevated_anomaly_count,
                "high_min_anomalies": settings.risk_high_anomaly_count,
                "min_readings": settings.risk_min_readings,
                "last_telemetry_at_utc": iso(last) if has_last else None,
                "has_recent_telemetry": bool(recent),
                "note": "Descriptive indicator from the open forecast incident or anomaly rates; not a failure probability.",
            },
            "model_version_id": model_version_id, "pipeline_run_id": pipeline_run_id,
            "approval_status": "PendingReview", "actual_outcome": "Unknown",
        })
    return rows


def model_category(score: float, thresholds: dict[str, float], has_recent_telemetry: bool) -> str:
    if not has_recent_telemetry:
        return "Undetermined"
    if score >= thresholds["high"]:
        return "High"
    if score >= thresholds["elevated"]:
        return "Elevated"
    return "Low"
