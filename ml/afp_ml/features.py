"""Feature engineering.

All features are computed from PAST data only:

* Anomaly (reading-level) features: per aircraft/sensor series ordered by ``recorded_at_utc``.
  Rolling statistics use ``shift(1)`` so the current reading is never part of its own baseline,
  and no later reading can influence an earlier row.
* Failure (aircraft-day) features: for a cutoff ``c`` (00:00 UTC of a day) only telemetry with
  ``recorded_at_utc < c`` is used.  The only maintenance-derived feature is
  ``days_since_last_maintenance``, computed from events strictly before ``c``; nothing about the
  outcome window (c, c + horizon] is ever visible to a feature (no target leakage).

Missing values are left as NaN here; imputation (median + missing indicators) happens inside the
persisted sklearn Pipeline so the statistics are learned on training data only.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass

import numpy as np
import pandas as pd

# Bump whenever a feature definition, name or order changes (recorded in model artifact metadata).
# v2: vibration aggregates, 24 h min/max, BR-01 threshold-breach counts, days since last maintenance.
FEATURE_SCHEMA_VERSION = "2"
# Reading-level anomaly features. Deliberately unchanged: the active anomaly model's feature
# contract depends on it.
MEASUREMENTS = ("temperature_c", "pressure_kpa")
# Aircraft-day failure features also use vibration (no threshold is ever applied to it).
FAILURE_MEASUREMENTS = ("temperature_c", "pressure_kpa", "vibration_index")
# Mirrors telemetry_rules.PSI_TO_KPA in the ETL, which converts the source PSI into canonical kPa.
PSI_TO_KPA = 6.894757293168
NO_SENSOR = "<no-sensor>"


# --------------------------------------------------------------------------------------------
# Reading-level features for anomaly detection
# --------------------------------------------------------------------------------------------

def anomaly_feature_names(window: int) -> list[str]:
    names: list[str] = []
    for m in MEASUREMENTS:
        names += [m, f"{m}_diff1", f"{m}_roll_mean_{window}", f"{m}_roll_std_{window}", f"{m}_dev_roll_mean_{window}"]
    names.append("minutes_since_prev")
    return names


def anomaly_min_periods(window: int) -> int:
    return max(2, window // 2)


def build_anomaly_features(telemetry: pd.DataFrame, window: int = 6) -> pd.DataFrame:
    """Return identifying columns + :func:`anomaly_feature_names` for each telemetry row.

    Output is sorted by aircraft, sensor, time, id.  Input must contain ``id``, ``aircraft_id``,
    ``sensor_id``, ``recorded_at_utc``, ``temperature_c`` and ``pressure_kpa``.
    """
    if window < 2:
        raise ValueError("window must be >= 2")
    min_periods = anomaly_min_periods(window)
    cols = ["id", "aircraft_id", "sensor_id", "recorded_at_utc", *MEASUREMENTS]
    df = telemetry[cols].copy()
    df["recorded_at_utc"] = pd.to_datetime(df["recorded_at_utc"], utc=True)
    for m in MEASUREMENTS:
        df[m] = pd.to_numeric(df[m], errors="coerce").astype("float64")
    df["_sensor_key"] = df["sensor_id"].astype(object).where(df["sensor_id"].notna(), NO_SENSOR).astype(str)
    df = df.sort_values(["aircraft_id", "_sensor_key", "recorded_at_utc", "id"], kind="mergesort").reset_index(drop=True)
    keys = [df["aircraft_id"], df["_sensor_key"]]

    for m in MEASUREMENTS:
        prev = df.groupby(keys, sort=False)[m].shift(1)
        df[f"{m}_diff1"] = df[m] - prev
        grouped_prev = prev.groupby(keys, sort=False)
        roll_mean = grouped_prev.transform(lambda s: s.rolling(window, min_periods=min_periods).mean())
        roll_std = grouped_prev.transform(lambda s: s.rolling(window, min_periods=min_periods).std())
        df[f"{m}_roll_mean_{window}"] = roll_mean
        df[f"{m}_roll_std_{window}"] = roll_std
        df[f"{m}_dev_roll_mean_{window}"] = df[m] - roll_mean

    delta = df.groupby(keys, sort=False)["recorded_at_utc"].diff()
    df["minutes_since_prev"] = delta.dt.total_seconds() / 60.0
    # Number of earlier readings in the series; rows with < min_periods are "warm-up" rows whose
    # rolling baseline is undefined. They are excluded from training and not scored.
    df["history_count"] = df.groupby(keys, sort=False).cumcount()
    df["warmup"] = df["history_count"] < min_periods
    return df.drop(columns=["_sensor_key"])


# --------------------------------------------------------------------------------------------
# Aircraft-day features for failure prediction
# --------------------------------------------------------------------------------------------

FAILURE_WINDOWS = (1, 7, 30)
MIN_HISTORY_DAYS = 7


@dataclass(frozen=True)
class BreachLimits:
    """BR-01 limits read from the seeded ``health_threshold`` table (never literals in code).

    ``None`` means the limit is not configured: the matching breach-count feature is then NaN
    (unknown), never 0, so an unconfigured rule can never look like "no breach".
    """
    pressure_low_psi: float | None = None
    temperature_high_c: float | None = None


def failure_feature_names() -> list[str]:
    names: list[str] = []
    for m in FAILURE_MEASUREMENTS:
        names += [f"{m}_mean_1d", f"{m}_min_1d", f"{m}_max_1d", f"{m}_mean_7d", f"{m}_std_7d", f"{m}_min_7d",
                  f"{m}_max_7d", f"{m}_mean_30d", f"{m}_std_30d", f"{m}_delta_1d_vs_30d"]
    names += ["readings_1d", "readings_7d", "suspect_fraction_7d", "missing_temperature_fraction_7d",
              "missing_pressure_fraction_7d", "missing_vibration_fraction_7d", "days_since_last_telemetry",
              "pressure_low_breaches_1d", "pressure_low_breaches_7d",
              "temperature_high_breaches_1d", "temperature_high_breaches_7d",
              "days_since_last_maintenance"]
    return names


def _daily(telemetry: pd.DataFrame, limits: BreachLimits) -> pd.DataFrame:
    df = telemetry[["aircraft_id", "recorded_at_utc", "temperature_c", "pressure_kpa", "quality_status"]].copy()
    # Callers/fixtures without vibration: absent means missing, never zero.
    df["vibration_index"] = (pd.to_numeric(telemetry["vibration_index"], errors="coerce").astype("float64")
                             if "vibration_index" in telemetry.columns else np.nan)
    df["recorded_at_utc"] = pd.to_datetime(df["recorded_at_utc"], utc=True)
    df["day"] = df["recorded_at_utc"].dt.floor("D")
    df["suspect"] = (df["quality_status"].astype(str) == "Suspect").astype("int64")
    # BR-01 breaches per reading. Pressure is compared in PSI (the unit the rule is written in) after
    # rounding, because the ETL stores kPa rounded to 4 dp: an exact 35 PSI reading comes back as
    # 34.9999992 PSI and must not count as "below 35". A missing reading is not a breach.
    psi = (df["pressure_kpa"] / PSI_TO_KPA).round(4)
    df["p_breach"] = (psi < limits.pressure_low_psi).astype("int64") if limits.pressure_low_psi is not None else 0
    df["t_breach"] = ((df["temperature_c"] > limits.temperature_high_c).astype("int64")
                      if limits.temperature_high_c is not None else 0)
    agg_spec: dict[str, tuple[str, str]] = {"n": ("recorded_at_utc", "size"), "suspect": ("suspect", "sum"),
                                            "last_ts": ("recorded_at_utc", "max"),
                                            "p_breach": ("p_breach", "sum"), "t_breach": ("t_breach", "sum")}
    for m in FAILURE_MEASUREMENTS:
        df[f"{m}__sq"] = df[m] ** 2
        agg_spec[f"{m}__sum"] = (m, "sum")
        agg_spec[f"{m}__sqsum"] = (f"{m}__sq", "sum")
        agg_spec[f"{m}__cnt"] = (m, "count")
        agg_spec[f"{m}__min"] = (m, "min")
        agg_spec[f"{m}__max"] = (m, "max")
    return df.groupby(["aircraft_id", "day"]).agg(**agg_spec).reset_index()


def _window_mean_std(daily: pd.DataFrame, m: str, w: int) -> tuple[pd.Series, pd.Series]:
    s = daily[f"{m}__sum"].rolling(w, min_periods=1).sum()
    q = daily[f"{m}__sqsum"].rolling(w, min_periods=1).sum()
    c = daily[f"{m}__cnt"].rolling(w, min_periods=1).sum()
    mean = s / c.where(c > 0)
    var = (q - c * mean ** 2) / (c - 1).where(c > 1)
    std = np.sqrt(var.clip(lower=0))
    return mean, std


def build_failure_features(telemetry: pd.DataFrame, until: dt.datetime | pd.Timestamp | None = None, *,
                           maintenance: pd.DataFrame | None = None,
                           breach_limits: BreachLimits | None = None) -> pd.DataFrame:
    """Aircraft-day feature rows; ``cutoff_utc`` = 00:00 UTC; uses telemetry strictly before cutoff.

    One row per aircraft and day from its first telemetry day + :data:`MIN_HISTORY_DAYS` up to
    ``until`` (floored to a day; default = day after the last reading).  Rows with no telemetry in
    the previous 30 days are dropped (nothing to say).

    ``maintenance`` (``aircraft_id``, ``occurred_at_utc``, ``is_verified``) feeds
    ``days_since_last_maintenance`` from verified events strictly before the cutoff; without it the
    feature is NaN.  ``breach_limits`` feeds the BR-01 breach counts; without it they are NaN.
    """
    names = failure_feature_names()
    if telemetry.empty:
        return pd.DataFrame(columns=["aircraft_id", "cutoff_utc", *names])
    limits = breach_limits or BreachLimits()
    daily_all = _daily(telemetry, limits)
    maint_by_aircraft = _verified_maintenance_times(maintenance)
    until_ts = pd.Timestamp(until).tz_convert("UTC").floor("D") if until is not None else None
    frames = []
    for aircraft_id, daily in daily_all.groupby("aircraft_id", sort=True):
        first = daily["day"].min()
        last = until_ts if until_ts is not None else daily["day"].max() + pd.Timedelta(days=1)
        if last < first:
            continue
        idx = pd.date_range(first, last, freq="D", tz="UTC")
        d = daily.set_index("day").reindex(idx)
        for col in ("n", "suspect", "p_breach", "t_breach") + tuple(
                f"{m}__{k}" for m in FAILURE_MEASUREMENTS for k in ("sum", "sqsum", "cnt")):
            d[col] = d[col].fillna(0.0).astype("float64")
        d = d.reset_index(drop=True)
        out = pd.DataFrame({"aircraft_id": aircraft_id, "cutoff_utc": idx})
        # Each statistic over days [d-w+1, d] is shifted by one row => row for cutoff c uses days < c.
        for m in FAILURE_MEASUREMENTS:
            mean1, _ = _window_mean_std(d, m, 1)
            mean7, std7 = _window_mean_std(d, m, 7)
            mean30, std30 = _window_mean_std(d, m, 30)
            out[f"{m}_mean_1d"] = mean1.shift(1).to_numpy()
            out[f"{m}_min_1d"] = d[f"{m}__min"].astype("float64").shift(1).to_numpy()
            out[f"{m}_max_1d"] = d[f"{m}__max"].astype("float64").shift(1).to_numpy()
            out[f"{m}_mean_7d"] = mean7.shift(1).to_numpy()
            out[f"{m}_std_7d"] = std7.shift(1).to_numpy()
            out[f"{m}_min_7d"] = d[f"{m}__min"].astype("float64").rolling(7, min_periods=1).min().shift(1).to_numpy()
            out[f"{m}_max_7d"] = d[f"{m}__max"].astype("float64").rolling(7, min_periods=1).max().shift(1).to_numpy()
            out[f"{m}_mean_30d"] = mean30.shift(1).to_numpy()
            out[f"{m}_std_30d"] = std30.shift(1).to_numpy()
            out[f"{m}_delta_1d_vs_30d"] = out[f"{m}_mean_1d"] - out[f"{m}_mean_30d"]
        n1 = d["n"].shift(1)
        n7 = d["n"].rolling(7, min_periods=1).sum().shift(1)
        n30 = d["n"].rolling(30, min_periods=1).sum().shift(1)
        out["readings_1d"] = n1.fillna(0.0).to_numpy()
        out["readings_7d"] = n7.fillna(0.0).to_numpy()
        n7_pos = n7.where(n7 > 0)
        out["suspect_fraction_7d"] = (d["suspect"].rolling(7, min_periods=1).sum().shift(1) / n7_pos).to_numpy()
        out["missing_temperature_fraction_7d"] = (
            1.0 - d["temperature_c__cnt"].rolling(7, min_periods=1).sum().shift(1) / n7_pos).to_numpy()
        out["missing_pressure_fraction_7d"] = (
            1.0 - d["pressure_kpa__cnt"].rolling(7, min_periods=1).sum().shift(1) / n7_pos).to_numpy()
        out["missing_vibration_fraction_7d"] = (
            1.0 - d["vibration_index__cnt"].rolling(7, min_periods=1).sum().shift(1) / n7_pos).to_numpy()
        for col, limit, name in (("p_breach", limits.pressure_low_psi, "pressure_low_breaches"),
                                 ("t_breach", limits.temperature_high_c, "temperature_high_breaches")):
            if limit is None:
                out[f"{name}_1d"] = np.nan
                out[f"{name}_7d"] = np.nan
            else:
                out[f"{name}_1d"] = d[col].shift(1).fillna(0.0).to_numpy()
                out[f"{name}_7d"] = d[col].rolling(7, min_periods=1).sum().shift(1).fillna(0.0).to_numpy()
        last_ts = pd.to_datetime(d["last_ts"], utc=True).ffill().shift(1)
        out["days_since_last_telemetry"] = ((pd.Series(idx) - last_ts).dt.total_seconds() / 86400.0).to_numpy()
        out["days_since_last_maintenance"] = _days_since_previous(idx, maint_by_aircraft.get(str(aircraft_id)))
        keep = (n30.fillna(0.0).to_numpy() > 0) & (idx >= first + pd.Timedelta(days=MIN_HISTORY_DAYS))
        frames.append(out.loc[keep])
    if not frames:
        return pd.DataFrame(columns=["aircraft_id", "cutoff_utc", *names])
    result = pd.concat(frames, ignore_index=True)
    return result[["aircraft_id", "cutoff_utc", *names]]


def _verified_maintenance_times(maintenance: pd.DataFrame | None) -> dict[str, np.ndarray]:
    if maintenance is None or maintenance.empty:
        return {}
    m = maintenance.loc[maintenance["is_verified"].fillna(False).astype(bool), ["aircraft_id", "occurred_at_utc"]]
    m = m.assign(occurred_at_utc=pd.to_datetime(m["occurred_at_utc"], utc=True))
    return {str(a): np.sort(g["occurred_at_utc"].dt.tz_convert(None).to_numpy(dtype="datetime64[ns]"))
            for a, g in m.groupby("aircraft_id")}


def _days_since_previous(cutoffs: pd.DatetimeIndex, times: np.ndarray | None) -> np.ndarray:
    """Days from the latest event STRICTLY before each cutoff; NaN when there is none."""
    out = np.full(len(cutoffs), np.nan)
    if times is None or len(times) == 0:
        return out
    cut = cutoffs.tz_convert(None).to_numpy(dtype="datetime64[ns]")
    pos = np.searchsorted(times, cut, side="left") - 1  # last index with time < cutoff
    has = pos >= 0
    prev = times[np.maximum(pos, 0)]
    out[has] = ((cut - prev)[has] / np.timedelta64(1, "s")) / 86400.0
    return out


# --------------------------------------------------------------------------------------------
# Labels and chronological split
# --------------------------------------------------------------------------------------------

def label_examples(examples: pd.DataFrame, label_events: pd.DataFrame, horizon_days: int,
                   as_of: dt.datetime | pd.Timestamp,
                   observed_until: dict[str, pd.Timestamp] | None = None) -> pd.DataFrame:
    """Attach ``label`` = 1 if a label event for the aircraft occurs in (cutoff, cutoff + horizon].

    Examples whose window end is after the observation end are censored (outcome not observable)
    and removed - never kept as negatives.  The observation end is ``as_of``, further limited per
    aircraft by ``observed_until`` (its last telemetry reading): the wall clock moving forward must
    not turn days nobody observed into "no failure".  ``label_events`` needs ``aircraft_id`` and
    ``occurred_at_utc``.
    """
    horizon = pd.Timedelta(days=horizon_days)
    as_of_ts = pd.Timestamp(as_of)
    as_of_ts = as_of_ts.tz_localize("UTC") if as_of_ts.tzinfo is None else as_of_ts.tz_convert("UTC")
    ex = examples.copy()
    ex["cutoff_utc"] = pd.to_datetime(ex["cutoff_utc"], utc=True)
    window_end = ex["cutoff_utc"] + horizon
    keep = window_end <= as_of_ts
    if observed_until is not None:
        obs = pd.to_datetime(ex["aircraft_id"].astype(str).map({str(k): v for k, v in observed_until.items()}),
                             utc=True)
        # An aircraft with no observation record has nothing observed: every window is censored.
        keep &= obs.notna() & (window_end <= obs)
    ex = ex.loc[keep].copy()
    labels = np.zeros(len(ex), dtype="int64")
    if len(ex) and len(label_events):
        ev = label_events.copy()
        ev["occurred_at_utc"] = pd.to_datetime(ev["occurred_at_utc"], utc=True)
        by_aircraft = {a: np.sort(g["occurred_at_utc"].dt.tz_convert(None).to_numpy(dtype="datetime64[ns]"))
                       for a, g in ev.groupby("aircraft_id")}
        cut = ex["cutoff_utc"].dt.tz_convert(None).to_numpy(dtype="datetime64[ns]")
        horizon_np = np.timedelta64(int(horizon.total_seconds()), "s")
        aircraft = ex["aircraft_id"].to_numpy()
        for a, times in by_aircraft.items():
            mask = aircraft == a
            if not mask.any():
                continue
            c = cut[mask]
            pos = np.searchsorted(times, c, side="right")  # first event strictly after cutoff
            has_next = pos < len(times)
            nxt = np.where(has_next, times[np.minimum(pos, len(times) - 1)], np.datetime64("NaT", "ns"))
            labels[mask] = (has_next & (nxt <= c + horizon_np)).astype("int64")
    ex["label"] = labels
    return ex.reset_index(drop=True)


@dataclass(frozen=True)
class ChronoSplit:
    train: pd.DataFrame
    validation: pd.DataFrame
    test: pd.DataFrame
    boundaries: dict[str, str]
    purge_gap_days: int
    purged_rows: int


def chronological_split(examples: pd.DataFrame, purge_gap_days: int, train_frac: float = 0.6,
                        validation_frac: float = 0.2) -> ChronoSplit:
    """Split by cutoff time into train / validation / test with a purge gap between splits.

    Rows with cutoff in (train_end, train_end + gap] and (val_end, val_end + gap] are dropped so a
    training label window (cutoff, cutoff + horizon] can never overlap a later split when
    ``purge_gap_days >= horizon_days``.
    """
    if examples.empty:
        raise ValueError("no examples to split")
    cut = pd.to_datetime(examples["cutoff_utc"], utc=True)
    t0, t1 = cut.min(), cut.max()
    gap = pd.Timedelta(days=purge_gap_days)
    usable = (t1 - t0) - 2 * gap
    if usable <= pd.Timedelta(0):
        raise ValueError(f"time span {(t1 - t0).days} days is too short for two purge gaps of {purge_gap_days} days")
    train_end = t0 + usable * train_frac
    val_start = train_end + gap
    val_end = val_start + usable * validation_frac
    test_start = val_end + gap
    train_mask = cut <= train_end
    val_mask = (cut > val_start) & (cut <= val_end)
    test_mask = cut > test_start
    purged = int(len(examples) - train_mask.sum() - val_mask.sum() - test_mask.sum())
    boundaries = {k: v.isoformat() for k, v in {
        "train_from": t0, "train_to": train_end, "validation_from_exclusive": val_start,
        "validation_to": val_end, "test_from_exclusive": test_start, "test_to": t1}.items()}
    return ChronoSplit(examples.loc[train_mask].reset_index(drop=True), examples.loc[val_mask].reset_index(drop=True),
                       examples.loc[test_mask].reset_index(drop=True), boundaries, purge_gap_days, purged)
