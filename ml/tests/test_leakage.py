import numpy as np
import pandas as pd
import pytest

from afp_ml.features import build_failure_features, chronological_split, failure_feature_names, label_examples
from conftest import T0, aircraft_ids, make_failure_dataset, make_mlog, make_telemetry


def test_failure_features_at_cutoff_ignore_future_rows():
    tel = make_telemetry(n_aircraft=2, sensors=1, days=20, interval_minutes=60)
    cutoff = T0 + pd.Timedelta(days=12)
    names = failure_feature_names()
    past_only = build_failure_features(tel[tel["recorded_at_utc"] < cutoff], until=cutoff)
    full = build_failure_features(tel)
    a = past_only[past_only["cutoff_utc"] == cutoff].set_index("aircraft_id")[names]
    b = full[full["cutoff_utc"] == cutoff].set_index("aircraft_id")[names]
    pd.testing.assert_frame_equal(a, b)
    # Modifying data at/after the cutoff must not change features at the cutoff.
    tampered = tel.copy()
    tampered.loc[tampered["recorded_at_utc"] >= cutoff, "temperature_c"] = 10_000.0
    c = build_failure_features(tampered)
    c = c[c["cutoff_utc"] == cutoff].set_index("aircraft_id")[names]
    pd.testing.assert_frame_equal(b, c)


def test_feature_names_exclude_maintenance_fields():
    # The single permitted maintenance-derived feature looks strictly backwards (see the next tests);
    # nothing derived from work orders, failures, labels or events may ever become a feature.
    allowed = {"days_since_last_maintenance"}
    for name in failure_feature_names():
        if name in allowed:
            continue
        assert not any(w in name for w in ("maintenance", "work_order", "failure", "label", "event"))


def test_days_since_last_maintenance_never_sees_the_outcome_window():
    """An event at/after the cutoff - including the very failure being predicted - must not move the feature."""
    tel = make_telemetry(n_aircraft=1, sensors=1, days=40, interval_minutes=60)
    a = tel["aircraft_id"].iloc[0]
    cutoff = T0 + pd.Timedelta(days=20)
    past = make_mlog([(a, T0 + pd.Timedelta(days=10))])
    with_future = make_mlog([(a, T0 + pd.Timedelta(days=10)), (a, cutoff), (a, cutoff + pd.Timedelta(days=5))])

    def at_cutoff(mlog):
        f = build_failure_features(tel, maintenance=mlog)
        return f.loc[f["cutoff_utc"] == cutoff, "days_since_last_maintenance"].iloc[0]

    assert at_cutoff(past) == pytest.approx(10.0)
    assert at_cutoff(with_future) == pytest.approx(10.0)   # the event AT the cutoff is not "before" it


def test_days_since_last_maintenance_is_missing_not_zero_without_history():
    tel = make_telemetry(n_aircraft=1, sensors=1, days=15, interval_minutes=60)
    f = build_failure_features(tel, maintenance=make_mlog([]))
    assert f["days_since_last_maintenance"].isna().all()


def test_breach_counts_are_unknown_not_zero_when_limits_are_not_configured():
    tel = make_telemetry(n_aircraft=1, sensors=1, days=15, interval_minutes=60)
    f = build_failure_features(tel)   # no BreachLimits -> rule not configured
    for name in ("pressure_low_breaches_1d", "pressure_low_breaches_7d",
                 "temperature_high_breaches_1d", "temperature_high_breaches_7d"):
        assert f[name].isna().all(), name


def test_breach_counts_use_the_configured_limits_and_an_exact_limit_is_not_a_breach():
    from afp_ml.features import PSI_TO_KPA, BreachLimits
    tel = make_telemetry(n_aircraft=1, sensors=1, days=10, interval_minutes=60)
    day = T0 + pd.Timedelta(days=8)
    in_day = (tel["recorded_at_utc"] >= day) & (tel["recorded_at_utc"] < day + pd.Timedelta(days=1))
    tel.loc[in_day, ["temperature_c", "pressure_kpa"]] = [650.0, round(40 * PSI_TO_KPA, 4)]
    rows = tel.index[in_day]
    # Exactly 35 PSI as the ETL stores it (kPa rounded to 4 dp) and exactly 700 C: neither is a breach.
    tel.loc[rows[0], ["temperature_c", "pressure_kpa"]] = [700.0, round(35 * PSI_TO_KPA, 4)]
    tel.loc[rows[1], "pressure_kpa"] = round(30 * PSI_TO_KPA, 4)   # below 35 PSI
    tel.loc[rows[2], "temperature_c"] = 750.0                       # above 700 C
    tel.loc[rows[3], "pressure_kpa"] = np.nan                       # missing is not a breach
    f = build_failure_features(tel, breach_limits=BreachLimits(pressure_low_psi=35.0, temperature_high_c=700.0))
    nxt = f.loc[f["cutoff_utc"] == day + pd.Timedelta(days=1)].iloc[0]
    assert nxt["pressure_low_breaches_1d"] == 1
    assert nxt["temperature_high_breaches_1d"] == 1


def test_label_window_is_cutoff_exclusive_horizon_inclusive():
    a = aircraft_ids(1)[0]
    cutoffs = pd.DataFrame({"aircraft_id": a, "cutoff_utc": pd.date_range(T0, periods=10, freq="D", tz="UTC")})
    event_time = T0 + pd.Timedelta(days=5)  # exactly at a cutoff
    labeled = label_examples(cutoffs, make_mlog([(a, event_time)]), horizon_days=2, as_of=T0 + pd.Timedelta(days=100))
    lab = dict(zip(labeled["cutoff_utc"], labeled["label"]))
    assert lab[T0 + pd.Timedelta(days=5)] == 0  # event at cutoff is NOT in (cutoff, cutoff+h]
    assert lab[T0 + pd.Timedelta(days=4)] == 1
    assert lab[T0 + pd.Timedelta(days=3)] == 1  # cutoff + 2d == event: inclusive
    assert lab[T0 + pd.Timedelta(days=2)] == 0


def test_censored_examples_removed():
    a = aircraft_ids(1)[0]
    cutoffs = pd.DataFrame({"aircraft_id": a, "cutoff_utc": pd.date_range(T0, periods=10, freq="D", tz="UTC")})
    labeled = label_examples(cutoffs, make_mlog([]), horizon_days=3, as_of=T0 + pd.Timedelta(days=6))
    assert labeled["cutoff_utc"].max() == T0 + pd.Timedelta(days=3)


def test_censoring_follows_observed_data_not_the_wall_clock():
    """Regression: windows past the last telemetry used to become negatives as the clock advanced."""
    a = aircraft_ids(1)[0]
    cutoffs = pd.DataFrame({"aircraft_id": a, "cutoff_utc": pd.date_range(T0, periods=40, freq="D", tz="UTC")})
    last_reading = T0 + pd.Timedelta(days=20)
    observed = {a: last_reading}
    early = label_examples(cutoffs, make_mlog([]), 5, T0 + pd.Timedelta(days=25), observed)
    later = label_examples(cutoffs, make_mlog([]), 5, T0 + pd.Timedelta(days=400), observed)
    # Only windows ending by the last reading count, however late the training run happens.
    assert early["cutoff_utc"].max() == T0 + pd.Timedelta(days=15)
    pd.testing.assert_frame_equal(early, later)


def test_readiness_labels_do_not_change_when_only_the_clock_advances():
    """The live defect: rerunning training 4 days later (no new data) changed the test split 103/5 -> 105/9."""
    from afp_ml.audit import compute_readiness
    from conftest import failure_settings
    from afp_ml.config import Settings
    tel, mlog = make_failure_dataset(n_aircraft=3, days=200)
    s = failure_settings(Settings())
    data_end = tel["recorded_at_utc"].max()
    _, today, _ = compute_readiness(tel, mlog, s, (data_end + pd.Timedelta(hours=1)).to_pydatetime())
    _, later, _ = compute_readiness(tel, mlog, s, (data_end + pd.Timedelta(days=4)).to_pydatetime())
    pd.testing.assert_frame_equal(today, later)


def test_aircraft_without_observations_contributes_no_examples():
    a, b = aircraft_ids(2)
    cutoffs = pd.DataFrame({"aircraft_id": [a, b] * 5,
                            "cutoff_utc": list(pd.date_range(T0, periods=5, freq="D", tz="UTC").repeat(2))})
    labeled = label_examples(cutoffs, make_mlog([]), 1, T0 + pd.Timedelta(days=100), {a: T0 + pd.Timedelta(days=100)})
    assert set(labeled["aircraft_id"]) == {a}


def test_chronological_split_with_purge_gap():
    tel, mlog = make_failure_dataset(n_aircraft=4, days=300)
    h = 14
    ex = label_examples(build_failure_features(tel), mlog, h, T0 + pd.Timedelta(days=320))
    split = chronological_split(ex, purge_gap_days=h)
    tr_max = split.train["cutoff_utc"].max()
    va_min, va_max = split.validation["cutoff_utc"].min(), split.validation["cutoff_utc"].max()
    te_min = split.test["cutoff_utc"].min()
    gap = pd.Timedelta(days=h)
    assert tr_max + gap < va_min, "train label windows must end before validation starts"
    assert va_max + gap < te_min
    assert split.purged_rows > 0
    assert len(split.train) + len(split.validation) + len(split.test) + split.purged_rows == len(ex)


def test_split_refuses_too_short_span():
    ex = pd.DataFrame({"aircraft_id": "a", "cutoff_utc": pd.date_range(T0, periods=20, freq="D", tz="UTC"), "label": 0})
    with pytest.raises(ValueError):
        chronological_split(ex, purge_gap_days=30)
