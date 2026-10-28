"""Horizon experiment: development only, read-only, existing gates, nothing operational touched (tests only)."""

import json
import os
import stat
from dataclasses import replace

import pandas as pd
import pytest

from afp_ml import cli
from afp_ml.artifacts import ArtifactStore
from afp_ml.features import label_examples
from afp_ml.horizon_experiment import (EXPERIMENT_ID, OUTCOME_INSUFFICIENT, OUTCOME_NO_IMPROVEMENT, OUTCOME_PROMISING,
                                       OUTCOME_UNCALIBRATED, decide, run_horizon_experiment)
from afp_ml.jobs import JobRefused, Services
from conftest import FakeRepository, T0, failure_settings, make_failure_dataset

WRITE_METHODS = ("create_run", "update_run", "claim_next_queued_run", "insert_model_version", "mark_model_registered",
                 "set_artifact_uri", "insert_anomaly_results", "insert_risk_predictions")
NOW = (T0 + pd.Timedelta(days=420)).to_pydatetime()


class ReadOnlyRepository(FakeRepository):
    """Any write fails the test: the experiment must not touch a database."""


for _name in WRITE_METHODS:
    def _refuse(self, *a, _n=_name, **k):
        raise AssertionError(f"the horizon experiment called the write method {_n}")
    setattr(ReadOnlyRepository, _name, _refuse)


class FakeBucket:
    bucket = "synthetic-dev"

    def __init__(self):
        self.objects = {}

    def put(self, key, data, content_type):
        self.objects[key] = data

    def get(self, key):
        return self.objects.get(key)


@pytest.fixture(scope="module")
def experiment(tmp_path_factory):
    tel, mlog = make_failure_dataset(n_aircraft=8, days=400)
    model_dir = tmp_path_factory.mktemp("models-synthetic")
    settings = replace(failure_settings(cli.Settings(model_dir=model_dir)), environment="development",
                       calibration_min_test_positives=10)
    repo, bucket = ReadOnlyRepository(telemetry=tel, mlog=mlog), FakeBucket()
    svc = Services(settings=settings, repo=repo, store=ArtifactStore(model_dir, bucket), clock=lambda: NOW, host="t")
    return run_horizon_experiment(svc), repo, bucket, model_dir


def test_refused_outside_the_development_environment(settings):
    svc = Services(settings=settings, repo=ReadOnlyRepository(), store=ArtifactStore(settings.model_dir))
    with pytest.raises(JobRefused, match="development"):
        run_horizon_experiment(svc)


def test_cli_refuses_an_operational_environment_before_connecting(monkeypatch, capsys):
    monkeypatch.setattr("afp_ml.repositories.PostgresRepository.__init__",
                        lambda *a, **k: (_ for _ in ()).throw(AssertionError("connected")))
    assert cli.run(["horizon-experiment"]) == cli.EXIT_CONFIG
    assert "AFP_ENVIRONMENT=development" in capsys.readouterr().err


def test_writes_nothing_to_the_database_and_registers_nothing(experiment):
    report, repo, _, _ = experiment
    assert repo.models == [] and repo.runs == {}          # no model_version, no pipeline_run
    assert report["environment"] == "development"
    assert report["decision"]["outcome"] in (OUTCOME_PROMISING, OUTCOME_UNCALIBRATED, OUTCOME_INSUFFICIENT,
                                             OUTCOME_NO_IMPROVEMENT)


def test_compares_both_horizons_on_the_same_span_with_the_unchanged_gates(experiment):
    report, *_ = experiment
    base, cand = report["runs"]["30d_equivalent_span"], report["runs"]["7d_equivalent_span"]
    assert report["gates"] == {"max_expected_calibration_error": 0.10, "brier_skill_must_exceed": 0.0,
                               "min_test_positives": 10}
    for run in (base, cand):
        assert run["status"] == "trained"
        assert run["calibration_criteria"]["max_expected_calibration_error"] == 0.10
        assert max(run["evaluation"]["calibration_fit_folds"]) < min(run["evaluation"]["held_out_test_folds"])
        assert sum(f["rows"] for f in run["held_out_by_fold"]) == run["test"]["rows"]
    assert base["data"]["cutoff_from_utc"] == cand["data"]["cutoff_from_utc"]
    assert base["data"]["cutoff_to_utc"] == cand["data"]["cutoff_to_utc"]
    assert base["evaluation"]["purge_gap_days"] == 30 and cand["evaluation"]["purge_gap_days"] == 7
    # A shorter horizon is censored less: its full eligible span reaches later.
    assert report["runs"]["7d_full_eligible_span"]["data"]["cutoff_to_utc"] > cand["data"]["cutoff_to_utc"]


def test_artifacts_are_saved_only_under_the_experiment_area_read_only_and_uploaded(experiment):
    report, _, bucket, model_dir = experiment
    folder = model_dir / "experiments" / EXPERIMENT_ID / report["run_stamp"]
    assert sorted(p.name for p in folder.iterdir()) == ["candidate_7d_bundle.joblib", "manifest.json", "report.json"]
    assert [p for p in model_dir.rglob("*") if p.is_file() and folder not in p.parents] == []   # nothing else written
    assert not os.stat(folder / "report.json").st_mode & stat.S_IWRITE
    assert set(bucket.objects) == {f"experiments/{EXPERIMENT_ID}/{report['run_stamp']}/{n}" for n in
                                   ("candidate_7d_bundle.joblib", "manifest.json", "report.json")}
    saved = json.loads((folder / "report.json").read_text())
    assert saved["decision"] == report["decision"] and "NOT validated" in saved["scope"]


def test_seven_day_label_window_and_censoring():
    c = pd.Timestamp("2026-03-01", tz="UTC")
    ex = pd.DataFrame({"aircraft_id": ["A"] * 4, "cutoff_utc": [c, c + pd.Timedelta(days=1),
                                                                 c + pd.Timedelta(days=2), c + pd.Timedelta(days=30)]})
    events = pd.DataFrame({"aircraft_id": ["A"], "occurred_at_utc": [c + pd.Timedelta(days=8)]})
    out = label_examples(ex, events, 7, as_of=c + pd.Timedelta(days=60),
                         observed_until={"A": c + pd.Timedelta(days=33)})
    # (c, c+7d] misses an event at c+8d; (c+1d, c+8d] includes it (closed end); (c+2d, c+9d] includes it.
    # The c+30d row's window ends at c+37d, after the last reading (c+33d): censored, not a negative.
    assert out["label"].tolist() == [0, 1, 1]
    assert pd.Timestamp(out["cutoff_utc"].max()) == c + pd.Timedelta(days=2)
    at_cutoff = label_examples(ex.iloc[[1]], events.assign(occurred_at_utc=c + pd.Timedelta(days=1)), 7,
                               as_of=c + pd.Timedelta(days=60))
    assert at_cutoff["label"].tolist() == [0]           # an event AT the cutoff is not in the future window


def _run(gates_pass, roc, bss, ece, pos=20, neg=20, folds=3):
    return {"status": "trained", "gates_pass": gates_pass, "evaluation": {"folds_used": folds},
            "calibration_checks": {"ece_ok": gates_pass, "brier_skill_ok": gates_pass, "test_positives_ok": True},
            "test": {"positives": pos, "negatives": neg, "roc_auc": roc, "brier_skill_score": bss,
                     "expected_calibration_error": ece}}


def test_decision_outcomes():
    base = _run(False, 0.8, -0.5, 0.28)
    assert decide(base, _run(True, 0.9, 0.3, 0.05), 10)[0] == OUTCOME_PROMISING
    assert decide(base, _run(False, 0.85, -0.6, 0.30), 10)[0] == OUTCOME_UNCALIBRATED
    assert decide(base, _run(False, 0.7, -0.9, 0.40), 10)[0] == OUTCOME_NO_IMPROVEMENT
    assert decide(base, _run(True, 0.9, 0.3, 0.05, pos=9), 10)[0] == OUTCOME_INSUFFICIENT
    assert decide(base, _run(True, 0.9, 0.3, 0.05, neg=3), 10)[0] == OUTCOME_INSUFFICIENT
    assert decide(base, _run(True, 0.9, 0.3, 0.05, folds=2), 10)[0] == OUTCOME_INSUFFICIENT
    assert decide(base, {"status": "insufficient", "reason": "x"}, 10)[0] == OUTCOME_INSUFFICIENT


def test_a_transient_upload_failure_is_retried(experiment, tmp_path, monkeypatch):
    import afp_ml.horizon_experiment as hx
    monkeypatch.setattr(hx, "UPLOAD_RETRY_DELAYS_SECONDS", (0, 0))

    class FlakyBucket(FakeBucket):
        failures = 2

        def put(self, key, data, content_type):
            if key.endswith(".joblib") and self.failures:
                self.failures -= 1
                raise RuntimeError("AccessDenied")
            super().put(key, data, content_type)

    report, *_ = experiment
    bucket = FlakyBucket()
    svc = Services(settings=replace(cli.Settings(model_dir=tmp_path), environment="development"),
                   repo=ReadOnlyRepository(), store=ArtifactStore(tmp_path, bucket), clock=lambda: NOW, host="t")
    out = hx._save(svc, "s", {"decision": report["decision"]}, {"pipeline": "x"})
    assert len(out["uploaded"]) == 3 and bucket.failures == 0


def test_a_failed_upload_still_leaves_a_complete_local_record(tmp_path, monkeypatch):
    monkeypatch.setattr("afp_ml.horizon_experiment.UPLOAD_RETRY_DELAYS_SECONDS", (0,))

    class FailingBucket(FakeBucket):
        def put(self, key, data, content_type):
            raise RuntimeError("AccessDenied")

    tel, mlog = make_failure_dataset(n_aircraft=8, days=400)
    settings = replace(failure_settings(cli.Settings(model_dir=tmp_path)), environment="development",
                       calibration_min_test_positives=10)
    svc = Services(settings=settings, repo=ReadOnlyRepository(telemetry=tel, mlog=mlog),
                   store=ArtifactStore(tmp_path, FailingBucket()), clock=lambda: NOW, host="t")
    with pytest.raises(RuntimeError, match="AccessDenied"):            # the failure is reported, not hidden
        run_horizon_experiment(svc)
    (folder,) = (tmp_path / "experiments" / EXPERIMENT_ID).iterdir()
    assert sorted(p.name for p in folder.iterdir()) == ["candidate_7d_bundle.joblib", "manifest.json", "report.json"]
