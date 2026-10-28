import numpy as np
import pandas as pd

from afp_ml.audit import assess_readiness, audit_telemetry, compute_readiness, run_dataset_audit, select_label_events
from afp_ml.config import Settings
from afp_ml.features import build_failure_features, label_examples
from conftest import T0, aircraft_ids, failure_settings, make_aircraft, make_failure_dataset, make_mlog, make_telemetry


def test_audit_counts_missing_suspect_and_gaps():
    tel = make_telemetry(n_aircraft=2, sensors=1, days=1, interval_minutes=10)
    tel.loc[0:9, "temperature_c"] = np.nan
    tel.loc[0:9, "quality_status"] = "Suspect"
    tel.loc[0:9, "quality_flags"] = "MISSING_TEMPERATURE"
    # Remove a 2-hour block from one series to create a gap.
    a0 = tel["aircraft_id"] == aircraft_ids(1)[0]
    block = a0 & (tel["recorded_at_utc"] >= T0 + pd.Timedelta(hours=5)) & (tel["recorded_at_utc"] < T0 + pd.Timedelta(hours=7))
    tel = tel.loc[~block].reset_index(drop=True)
    aircraft = make_aircraft(3)  # third aircraft has no telemetry
    s = audit_telemetry(tel, aircraft, gap_minutes=60)
    assert s["total_rows"] == len(tel)
    assert s["quality_status"]["Suspect"] == 10
    assert s["missing_rate"]["temperature_c"] == round(10 / len(tel), 6)
    assert s["quality_flags"] == {"MISSING_TEMPERATURE": 10}
    assert s["duplicates"]["key_duplicates"] == 0
    assert s["cadence"]["gaps_over_threshold"] == 1
    assert s["cadence"]["median_interval_minutes"] == 10
    assert s["aircraft_without_telemetry"] == ["T-02"]
    assert len(s["per_aircraft"]) == 2


def test_duplicate_detection():
    tel = make_telemetry(n_aircraft=1, sensors=1, days=0.1)
    dup = tel.iloc[[0]].copy()
    dup["id"] = 999
    s = audit_telemetry(pd.concat([tel, dup], ignore_index=True), make_aircraft(1))
    assert s["duplicates"]["key_duplicates"] == 1


def test_label_selection_rules():
    a = aircraft_ids(1)[0]
    mlog = pd.concat([
        make_mlog([(a, T0)], confirmed=True),
        make_mlog([(a, T0)], confirmed=None),
        make_mlog([(a, T0)], confirmed=False),
        make_mlog([(a, T0)], verified=False),
        make_mlog([(a, T0)], event_type="Scheduled"),
    ], ignore_index=True)
    assert len(select_label_events(mlog)) == 2
    assert len(select_label_events(mlog, require_confirmed=True)) == 1


def test_readiness_blocked_without_labels():
    tel = make_telemetry(n_aircraft=2, sensors=1, days=20, interval_minutes=60)
    summary = run_dataset_audit(tel, make_mlog([]), make_aircraft(2), Settings(), (T0 + pd.Timedelta(days=60)).to_pydatetime())
    r = summary["failure_prediction_readiness"]
    assert r["status"] == "Blocked"
    assert r["positive_examples"] == 0
    assert r["horizon_days"] == 30
    assert any("No verified Unscheduled" in x for x in r["reasons"])
    assert summary["labels"]["label_events"] == 0


def test_readiness_blocked_with_too_few_labels():
    tel, mlog = make_failure_dataset(n_aircraft=8, days=400)
    few = mlog.iloc[:5]
    r, _, _ = compute_readiness(tel, few, Settings(), (T0 + pd.Timedelta(days=420)).to_pydatetime())
    assert r["status"] == "Blocked"
    assert any("minimum 30" in x for x in r["reasons"])


def test_readiness_ready_with_enough_labels(settings):
    tel, mlog = make_failure_dataset(n_aircraft=8, days=400)
    r, labeled, _ = compute_readiness(tel, mlog, failure_settings(settings), (T0 + pd.Timedelta(days=420)).to_pydatetime())
    assert r["status"] == "Ready", r["reasons"]
    assert r["positive_examples"] > 0 and r["negative_examples"] > 0
    assert r["aircraft_with_positives"] == 8
    assert r["split"]["test_positives"] >= 3
    assert int(labeled["label"].sum()) == r["positive_examples"]


def test_readiness_blocked_when_label_definition_not_confirmed(settings):
    from dataclasses import replace

    from afp_ml.audit import LABEL_NOT_CONFIRMED_REASON

    tel, mlog = make_failure_dataset(n_aircraft=8, days=400)
    as_of = (T0 + pd.Timedelta(days=420)).to_pydatetime()
    unconfirmed = replace(failure_settings(settings), label_definition_confirmed=False)
    r, _, _ = compute_readiness(tel, mlog, unconfirmed, as_of)
    assert r["status"] == "Blocked"
    assert r["reasons"] == [LABEL_NOT_CONFIRMED_REASON]  # the only failing criterion
    assert r["label_definition"]["definition_confirmed"] is False
    confirmed, _, _ = compute_readiness(tel, mlog, failure_settings(settings), as_of)
    assert confirmed["status"] == "Ready" and confirmed["reasons"] == []


def test_unconfirmed_reason_added_to_other_reasons():
    tel = make_telemetry(n_aircraft=2, sensors=1, days=20, interval_minutes=60)
    summary = run_dataset_audit(tel, make_mlog([]), make_aircraft(2),
                                Settings(failure_codes=("F01", "F02")), (T0 + pd.Timedelta(days=60)).to_pydatetime())
    r = summary["failure_prediction_readiness"]
    assert r["status"] == "Blocked"
    assert r["reasons"][0].startswith("Label definition not confirmed by the domain owner")
    assert len(r["reasons"]) > 1
    ld = summary["label_definition"]
    assert ld == {"event_type": "Unscheduled", "is_verified": True, "require_confirmed_failure": False,
                  "definition_confirmed": False, "failure_codes": ["F01", "F02"], "failure_codes_applied": False,
                  "failure_codes_note": ld["failure_codes_note"], "horizon_days": 30}
