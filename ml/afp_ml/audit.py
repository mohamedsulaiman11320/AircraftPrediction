"""Dataset audit and failure-prediction readiness gate (pure functions over DataFrames)."""

from __future__ import annotations

import datetime as dt
from typing import Any

import numpy as np
import pandas as pd

from .config import Settings
from .features import BreachLimits, build_failure_features, chronological_split, label_examples

SYNTHETIC_SOURCE_MARKERS = ("SYNTHETIC",)
LABEL_EVENT_TYPE = "Unscheduled"
LABEL_NOT_CONFIRMED_REASON = ("Label definition not confirmed by the domain owner "
                              "(set ML_LABEL_DEFINITION_CONFIRMED=true after sign-off).")


def label_definition(settings: Settings) -> dict[str, Any]:
    """The failure-label definition in force (recorded in audits and model metadata for traceability)."""
    return {
        "event_type": LABEL_EVENT_TYPE,
        "is_verified": True,
        "require_confirmed_failure": settings.label_require_confirmed_failure,
        "definition_confirmed": settings.label_definition_confirmed,
        "failure_codes": list(settings.failure_codes),
        "failure_codes_applied": False,
        "failure_codes_note": "Recorded only: maintenance_log has no failure_code column, so codes do not filter labels.",
        "horizon_days": settings.prediction_horizon_days,
    }


def _is_missing(v: Any) -> bool:
    return v is None or v is pd.NA or (isinstance(v, float) and np.isnan(v))


def _is_true(v: Any) -> bool:
    return not _is_missing(v) and bool(v) is True


def _is_false(v: Any) -> bool:
    return not _is_missing(v) and bool(v) is False


def _rate(num: float, den: float) -> float | None:
    return round(float(num) / float(den), 6) if den else None


def _iso(ts: Any) -> str | None:
    if ts is None or pd.isna(ts):
        return None
    return pd.Timestamp(ts).tz_convert("UTC").isoformat().replace("+00:00", "Z")


# --------------------------------------------------------------------------------------------
# Labels
# --------------------------------------------------------------------------------------------

def select_label_events(mlog: pd.DataFrame, require_confirmed: bool = False) -> pd.DataFrame:
    """Verified ``Unscheduled`` maintenance events used as failure labels.

    Default: verified Unscheduled events unless ``actual_failure_confirmed`` is explicitly false.
    With ``require_confirmed`` only events with ``actual_failure_confirmed = true`` count.
    """
    if mlog.empty:
        return mlog.copy()
    unscheduled = mlog["event_type"].astype(str) == LABEL_EVENT_TYPE
    verified = mlog["is_verified"].map(_is_true).astype(bool)
    conf = mlog["actual_failure_confirmed"]
    conf_ok = conf.map(_is_true).astype(bool) if require_confirmed else ~conf.map(_is_false).astype(bool)
    out = mlog.loc[unscheduled & verified & conf_ok].copy()
    out["occurred_at_utc"] = pd.to_datetime(out["occurred_at_utc"], utc=True)
    return out.sort_values("occurred_at_utc").reset_index(drop=True)


def audit_labels(mlog: pd.DataFrame, label_events: pd.DataFrame, aircraft: pd.DataFrame) -> dict[str, Any]:
    if mlog.empty:
        return {"maintenance_log_rows": 0, "by_event_type": {}, "verified_unscheduled": 0,
                "actual_failure_confirmed": {"true": 0, "false": 0, "null": 0}, "label_events": 0,
                "aircraft_with_label_events": 0, "components_with_label_events": 0,
                "label_events_without_component": 0, "label_events_by_month": {}, "first_label_utc": None,
                "last_label_utc": None, "aircraft_total": int(len(aircraft))}
    unscheduled = mlog.loc[mlog["event_type"].astype(str) == LABEL_EVENT_TYPE]
    verified_unscheduled = unscheduled.loc[unscheduled["is_verified"].map(_is_true).astype(bool)]
    conf = verified_unscheduled["actual_failure_confirmed"]
    months = (pd.to_datetime(label_events["occurred_at_utc"], utc=True).dt.strftime("%Y-%m").value_counts().sort_index()
              if len(label_events) else pd.Series(dtype="int64"))
    return {
        "maintenance_log_rows": int(len(mlog)),
        "by_event_type": {str(k): int(v) for k, v in mlog["event_type"].astype(str).value_counts().items()},
        "unscheduled_total": int(len(unscheduled)),
        "unscheduled_unverified": int(len(unscheduled) - len(verified_unscheduled)),
        "verified_unscheduled": int(len(verified_unscheduled)),
        "actual_failure_confirmed": {
            "true": int(conf.map(_is_true).sum()),
            "false": int(conf.map(_is_false).sum()),
            "null": int(len(conf) - conf.map(_is_true).sum() - conf.map(_is_false).sum()),
        },
        "label_events": int(len(label_events)),
        "aircraft_with_label_events": int(label_events["aircraft_id"].nunique()) if len(label_events) else 0,
        "components_with_label_events": int(label_events["component_id"].dropna().nunique()) if len(label_events) else 0,
        "label_events_without_component": int(label_events["component_id"].isna().sum()) if len(label_events) else 0,
        "label_events_by_month": {str(k): int(v) for k, v in months.items()},
        "first_label_utc": _iso(label_events["occurred_at_utc"].min()) if len(label_events) else None,
        "last_label_utc": _iso(label_events["occurred_at_utc"].max()) if len(label_events) else None,
        "aircraft_total": int(len(aircraft)),
    }


# --------------------------------------------------------------------------------------------
# Telemetry
# --------------------------------------------------------------------------------------------

def audit_telemetry(tel: pd.DataFrame, aircraft: pd.DataFrame, gap_minutes: float = 60.0) -> dict[str, Any]:
    reg = dict(zip(aircraft["aircraft_id"].astype(str), aircraft["registration_number"])) if len(aircraft) else {}
    total = int(len(tel))
    if total == 0:
        return {"total_rows": 0, "quality_status": {}, "suspect_rate": None, "first_record_utc": None,
                "last_record_utc": None, "missing_rate": {"temperature_c": None, "pressure_kpa": None},
                "per_aircraft": [], "aircraft_without_telemetry": sorted(map(str, reg.values())),
                "duplicates": {"key_duplicates": 0}, "quality_flags": {}, "sensors": {}, "cadence": {},
                "data_sources": {}, "synthetic_data_present": False}
    df = tel.copy()
    df["recorded_at_utc"] = pd.to_datetime(df["recorded_at_utc"], utc=True)
    df["_suspect"] = df["quality_status"].astype(str) == "Suspect"
    df["_sensor"] = df["sensor_id"].astype(object).where(df["sensor_id"].notna(), "<none>").astype(str)
    quality = {str(k): int(v) for k, v in df["quality_status"].astype(str).value_counts().items()}

    flags: dict[str, int] = {}
    for value in df["quality_flags"].dropna().astype(str):
        for code in value.split(","):
            code = code.strip()
            if code:
                flags[code] = flags.get(code, 0) + 1

    key_dups = int(df.duplicated(["aircraft_id", "_sensor", "recorded_at_utc"]).sum())
    value_dups = int(df.duplicated(["aircraft_id", "_sensor", "recorded_at_utc", "temperature_c", "pressure_kpa"]).sum())

    per_aircraft = []
    for aid, g in df.groupby("aircraft_id", sort=True):
        days = g["recorded_at_utc"].dt.floor("D").nunique()
        first, last = g["recorded_at_utc"].min(), g["recorded_at_utc"].max()
        per_aircraft.append({
            "aircraft_id": str(aid), "registration_number": reg.get(str(aid)), "rows": int(len(g)),
            "sensors": int(g["_sensor"].nunique()), "first_utc": _iso(first), "last_utc": _iso(last),
            "span_days": round((last - first).total_seconds() / 86400.0, 3), "days_with_data": int(days),
            "suspect_rate": _rate(g["_suspect"].sum(), len(g)),
            "missing_temperature_rate": _rate(g["temperature_c"].isna().sum(), len(g)),
            "missing_pressure_rate": _rate(g["pressure_kpa"].isna().sum(), len(g)),
        })
    seen = {p["aircraft_id"] for p in per_aircraft}
    without = sorted(str(r) for a, r in reg.items() if a not in seen)

    df = df.sort_values(["aircraft_id", "_sensor", "recorded_at_utc"], kind="mergesort")
    deltas = df.groupby(["aircraft_id", "_sensor"], sort=False)["recorded_at_utc"].diff().dt.total_seconds() / 60.0
    df["_delta_min"] = deltas
    series_stats = []
    for (aid, sensor), g in df.groupby(["aircraft_id", "_sensor"], sort=True):
        d = g["_delta_min"].dropna()
        gaps = d[d > gap_minutes]
        series_stats.append({
            "aircraft_id": str(aid), "registration_number": reg.get(str(aid)), "sensor_id": sensor,
            "rows": int(len(g)), "median_interval_minutes": float(d.median()) if len(d) else None,
            "gaps_over_threshold": int(len(gaps)), "max_gap_hours": round(float(d.max()) / 60.0, 3) if len(d) else None,
        })
    all_d = deltas.dropna()
    sensors_per_aircraft = df.groupby("aircraft_id")["_sensor"].nunique()
    sources = {str(k): int(v) for k, v in df["data_source"].astype(str).value_counts().items()}
    return {
        "total_rows": total,
        "quality_status": quality,
        "suspect_rate": _rate(df["_suspect"].sum(), total),
        "first_record_utc": _iso(df["recorded_at_utc"].min()),
        "last_record_utc": _iso(df["recorded_at_utc"].max()),
        "missing_rate": {"temperature_c": _rate(df["temperature_c"].isna().sum(), total),
                         "pressure_kpa": _rate(df["pressure_kpa"].isna().sum(), total)},
        "quality_flags": dict(sorted(flags.items())),
        "duplicates": {"key_duplicates": key_dups, "value_duplicates": value_dups,
                       "note": "key = (aircraft_id, sensor_id, recorded_at_utc); the database unique index should keep this 0"},
        "sensors": {"distinct_series": int(len(series_stats)),
                    "rows_without_sensor_id": int(df["sensor_id"].isna().sum()),
                    "sensors_per_aircraft_min": int(sensors_per_aircraft.min()),
                    "sensors_per_aircraft_max": int(sensors_per_aircraft.max())},
        "cadence": {"gap_threshold_minutes": gap_minutes,
                    "median_interval_minutes": float(all_d.median()) if len(all_d) else None,
                    "gaps_over_threshold": int((all_d > gap_minutes).sum()),
                    "max_gap_hours": round(float(all_d.max()) / 60.0, 3) if len(all_d) else None,
                    "series": series_stats},
        "per_aircraft": per_aircraft,
        "aircraft_without_telemetry": without,
        "data_sources": sources,
        "synthetic_data_present": any(any(m in s.upper() for m in SYNTHETIC_SOURCE_MARKERS) for s in sources),
    }


# --------------------------------------------------------------------------------------------
# Readiness
# --------------------------------------------------------------------------------------------

def _covered_events(label_events: pd.DataFrame, examples: pd.DataFrame, horizon_days: int) -> pd.DataFrame:
    """Label events that fall inside at least one observable example's label window."""
    if label_events.empty or examples.empty:
        return label_events.iloc[0:0]
    horizon = pd.Timedelta(days=horizon_days)
    cut_by_aircraft = {a: np.sort(pd.to_datetime(g["cutoff_utc"], utc=True).dt.tz_convert(None)
                                  .to_numpy(dtype="datetime64[ns]"))
                       for a, g in examples.groupby("aircraft_id")}
    keep = []
    for _, ev in label_events.iterrows():
        cuts = cut_by_aircraft.get(ev["aircraft_id"])
        if cuts is None:
            keep.append(False)
            continue
        e = np.datetime64(pd.Timestamp(ev["occurred_at_utc"]).tz_convert(None).to_datetime64(), "ns")
        lo = e - np.timedelta64(int(horizon.total_seconds()), "s")
        i = np.searchsorted(cuts, lo, side="left")  # first cutoff >= e - h
        keep.append(bool(i < len(cuts) and cuts[i] < e))
    return label_events.loc[keep]


def assess_readiness(label_events: pd.DataFrame, labeled_examples: pd.DataFrame, settings: Settings) -> dict[str, Any]:
    h = settings.prediction_horizon_days
    covered = _covered_events(label_events, labeled_examples, h)
    positives = int(labeled_examples["label"].sum()) if len(labeled_examples) else 0
    negatives = int(len(labeled_examples) - positives)
    aircraft_pos = int(covered["aircraft_id"].nunique()) if len(covered) else 0
    months = int(pd.to_datetime(covered["occurred_at_utc"], utc=True).dt.strftime("%Y-%m").nunique()) if len(covered) else 0
    reasons: list[str] = []
    if not settings.label_definition_confirmed:
        reasons.append(LABEL_NOT_CONFIRMED_REASON)
    if len(label_events) == 0:
        reasons.append("No verified Unscheduled maintenance events (maintenance_log) are available as failure labels.")
    if len(covered) < settings.min_positive_labels:
        reasons.append(f"Only {len(covered)} verified unscheduled maintenance events are preceded by telemetry within the "
                       f"{h}-day horizon (minimum {settings.min_positive_labels}).")
    if aircraft_pos < settings.min_aircraft_with_positives:
        reasons.append(f"Only {aircraft_pos} aircraft have usable failure labels (minimum "
                       f"{settings.min_aircraft_with_positives}).")
    if months < settings.min_label_months:
        reasons.append(f"Usable failure labels span {months} distinct calendar months (minimum {settings.min_label_months}).")
    if negatives < settings.min_negative_examples:
        reasons.append(f"Only {negatives} negative aircraft-day examples with an observable {h}-day outcome "
                       f"(minimum {settings.min_negative_examples}).")
    split_info: dict[str, Any] | None = None
    if len(labeled_examples):
        try:
            split = chronological_split(labeled_examples, purge_gap_days=h)
            split_info = {"train_rows": len(split.train), "validation_rows": len(split.validation),
                          "test_rows": len(split.test), "train_positives": int(split.train["label"].sum()),
                          "validation_positives": int(split.validation["label"].sum()),
                          "test_positives": int(split.test["label"].sum()), "purged_rows": split.purged_rows,
                          "purge_gap_days": h, "boundaries": split.boundaries}
            for name in ("train", "validation", "test"):
                if split_info[f"{name}_positives"] < settings.min_split_positives:
                    reasons.append(f"The chronological {name} split would contain {split_info[f'{name}_positives']} "
                                   f"positive examples (minimum {settings.min_split_positives}).")
        except ValueError as exc:
            reasons.append(f"A chronological train/validation/test split with a {h}-day purge gap is not possible: {exc}.")
    else:
        reasons.append("No aircraft-day examples with an observable outcome could be built from telemetry.")
    return {
        "status": "Ready" if not reasons else "Blocked",
        "reasons": reasons,
        "horizon_days": h,
        "positive_examples": positives,
        "negative_examples": negatives,
        "label_events": int(len(label_events)),
        "usable_label_events": int(len(covered)),
        "aircraft_with_positives": aircraft_pos,
        "distinct_label_months": months,
        "split": split_info,
        "thresholds": settings.readiness_thresholds(),
        "label_definition": label_definition(settings),
    }


def compute_readiness(tel: pd.DataFrame, mlog: pd.DataFrame, settings: Settings,
                      as_of: dt.datetime, breach_limits: BreachLimits | None = None
                      ) -> tuple[dict[str, Any], pd.DataFrame, pd.DataFrame]:
    """Return ``(readiness, labeled_examples_with_features, label_events)``."""
    label_events = select_label_events(mlog, settings.label_require_confirmed_failure)
    features = build_failure_features(tel, maintenance=mlog, breach_limits=breach_limits)
    # Observation ends at each aircraft's last telemetry reading, not at the wall clock.
    observed_until = (pd.to_datetime(tel["recorded_at_utc"], utc=True).groupby(tel["aircraft_id"].astype(str)).max()
                      .to_dict() if len(tel) else {})
    labeled = label_examples(features, label_events, settings.prediction_horizon_days, as_of, observed_until)
    return assess_readiness(label_events, labeled, settings), labeled, label_events


def run_dataset_audit(tel: pd.DataFrame, mlog: pd.DataFrame, aircraft: pd.DataFrame, settings: Settings,
                      as_of: dt.datetime, breach_limits: BreachLimits | None = None) -> dict[str, Any]:
    readiness, _, label_events = compute_readiness(tel, mlog, settings, as_of, breach_limits)
    tel_summary = audit_telemetry(tel, aircraft, settings.audit_gap_minutes)
    warnings: list[str] = []
    if tel_summary.get("synthetic_data_present"):
        warnings.append("Telemetry contains SYNTHETIC data_source rows: results are for development only.")
    if tel_summary["total_rows"] == 0:
        warnings.append("No telemetry records found.")
    return {
        "audit_type": "DatasetAudit",
        "generated_at_utc": _iso(pd.Timestamp(as_of)),
        "horizon_days": settings.prediction_horizon_days,
        "telemetry": tel_summary,
        "labels": audit_labels(mlog, label_events, aircraft),
        "label_definition": label_definition(settings),
        "failure_prediction_readiness": readiness,
        "warnings": warnings,
    }


def format_audit_text(summary: dict[str, Any]) -> str:
    t, lab, r = summary["telemetry"], summary["labels"], summary["failure_prediction_readiness"]
    lines = [
        f"Dataset audit at {summary['generated_at_utc']} (horizon {summary['horizon_days']} days)",
        "",
        "Telemetry",
        f"  rows: {t['total_rows']}   quality: {t.get('quality_status')}   suspect rate: {t.get('suspect_rate')}",
        f"  coverage: {t.get('first_record_utc')} .. {t.get('last_record_utc')}",
        f"  missing rate: {t.get('missing_rate')}",
        f"  duplicates (aircraft, sensor, timestamp): {t.get('duplicates', {}).get('key_duplicates')}",
    ]
    cad = t.get("cadence") or {}
    if cad:
        lines.append(f"  cadence: median {cad.get('median_interval_minutes')} min; gaps > {cad.get('gap_threshold_minutes')} "
                     f"min: {cad.get('gaps_over_threshold')}; max gap {cad.get('max_gap_hours')} h")
    if t.get("quality_flags"):
        lines.append(f"  quality flags: {t['quality_flags']}")
    if t.get("data_sources"):
        lines.append(f"  data sources: {t['data_sources']}")
    for p in t.get("per_aircraft", []):
        lines.append(f"    {p['registration_number'] or p['aircraft_id']}: {p['rows']} rows, {p['sensors']} sensors, "
                     f"{p['days_with_data']} days, suspect {p['suspect_rate']}, {p['first_utc']} .. {p['last_utc']}")
    if t.get("aircraft_without_telemetry"):
        lines.append(f"  aircraft without telemetry: {', '.join(t['aircraft_without_telemetry'])}")
    lines += [
        "",
        "Maintenance labels",
        f"  maintenance_log rows: {lab['maintenance_log_rows']}  by type: {lab.get('by_event_type')}",
        f"  verified unscheduled: {lab.get('verified_unscheduled', 0)}  actual_failure_confirmed: "
        f"{lab.get('actual_failure_confirmed')}",
        f"  label events: {lab['label_events']} on {lab['aircraft_with_label_events']} aircraft, "
        f"{lab['components_with_label_events']} components",
        "",
        f"Failure prediction readiness: {r['status']}",
        f"  positives {r['positive_examples']} / negatives {r['negative_examples']} aircraft-day examples; "
        f"usable label events {r['usable_label_events']}; aircraft with positives {r['aircraft_with_positives']}; "
        f"label months {r['distinct_label_months']}",
    ]
    lines += [f"  - {reason}" for reason in r["reasons"]]
    for w in summary.get("warnings", []):
        lines.append(f"WARNING: {w}")
    return "\n".join(lines)
