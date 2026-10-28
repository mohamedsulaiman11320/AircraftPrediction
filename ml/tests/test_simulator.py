"""Daily telemetry simulator: timing, generation, retention, atomicity, recovery and ETL confirmation."""

import datetime as dt
import json
import uuid
from dataclasses import replace
from pathlib import Path

import numpy as np
import pytest

from afp_ml.config import Settings
from afp_ml.jobs import JobRefused, execute_job
from afp_ml.simulator import (SimulatorConfig, TelemetrySimulator, add_months, apply_retention, dumps,
                              exclusive_lock, generate_records, hours_to_generate, local_midnight_utc,
                              next_local_midnight_utc, retention_start, sha256_bytes, status_flag)
from conftest import FakeRepository, make_services

UTC = dt.timezone.utc
CONFIG_FILE = Path(__file__).resolve().parents[2] / "tools" / "simulator" / "telemetry_simulator.json"
SCENARIO_CFG = SimulatorConfig.load(CONFIG_FILE)
# These tests cover the AR(1) continuation and the file/ID/ETL safety logic; the scripted scenario (enabled in the
# real config since 2026-10-28) has its own tests at the end of this file.
REAL = replace(SCENARIO_CFG, scenario=None)
AIRCRAFT = REAL.expected_aircraft


def epoch(*args):
    return int(dt.datetime(*args, tzinfo=UTC).timestamp())


def make_history(end=(2026, 10, 22, 23), days=40, seed=7, missing=0.03):
    """Hourly history for the six aircraft ending at `end` (UTC), shaped like the source file."""
    rng = np.random.default_rng(seed)
    last = epoch(*end)
    start = last - (days * 24 - 1) * 3600
    out, n = [], 100001
    for t in range(start, last + 1, 3600):
        for tail in AIRCRAFT:
            m = {"engine_temp_c": round(float(rng.normal(520, 40)), 1),
                 "oil_pressure_psi": round(float(rng.normal(25, 4)), 1),
                 "vibration_index": round(float(abs(rng.normal(1.8, 0.5))), 2)}
            for k in m:
                if rng.random() < missing:
                    m[k] = None
            out.append({"telemetry_id": f"TEL-{n}", "tail_number": tail, "timestamp_epoch": t, "metrics": m,
                        "status_flag": status_flag(m, REAL.flag_rule)})
            n += 1
    return out


class FakeEtl:
    """Stands in for `ingest-raw-telemetry --wait` (API raw-upload + Spark ETL). `imports` is the shared
    telemetry_import table: an identical file is never uploaded twice, its existing import is reused."""

    def __init__(self, imports, repo=None, final="Completed"):
        # repo: where a Completed import's records land. Like the real ETL (ON CONFLICT on source_record_id), an id
        # that is already stored is skipped silently - which is exactly how a colliding id loses a reading.
        self.imports, self.repo, self.final, self.calls = imports, repo, final, []

    def _store(self, data):
        if self.repo is None:
            return
        for r in json.loads(data):
            self.repo.stored_telemetry.setdefault(r["telemetry_id"], (r["tail_number"], r["timestamp_epoch"]))

    def __call__(self, path):
        data = Path(path).read_bytes()
        sha = sha256_bytes(data)
        existing = next((i for i in self.imports.values() if i["sha"] == sha), None)
        if existing is None:
            iid = str(uuid.uuid4())
            n = len(json.loads(data))
            existing = self.imports[iid] = {"importId": iid, "sha": sha, "status": self.final, "totalRows": n,
                                            "acceptedRows": n if self.final == "Completed" else 0, "rejectedRows": 0,
                                            "duplicateRows": 0,
                                            "errorMessage": "spark error" if self.final == "Failed" else None}
            self.calls.append(path.name)
            if existing["status"] == "Completed":
                self._store(data)
            return {**existing, "reused": False}
        if existing["status"] == "Completed":
            self._store(data)
        return {**existing, "reused": True}


@pytest.fixture
def env(tmp_path):
    source = tmp_path / "RawData" / "telemetry.json"
    source.parent.mkdir()
    history = make_history()
    source.write_bytes(dumps(history))
    cfg = replace(REAL, source_file=source, wait_timeout_seconds=0, poll_seconds=0)
    repo = FakeRepository()
    return {"cfg": cfg, "repo": repo, "source": source, "history": history, "state": tmp_path / "state",
            "imports": {}}


def simulator(env, now, final="Completed"):
    svc = make_services(env["repo"], Settings(), now)
    etl = FakeEtl(env["imports"], env["repo"], final)
    return TelemetrySimulator(env["cfg"], svc, etl, env["state"], sleep=lambda s: None), etl


# ---- timing ----------------------------------------------------------------------------------

def test_cutoff_is_local_ist_midnight_whatever_the_machine_timezone():
    # 18:29 UTC on 27 Oct is 23:59 IST on 27 Oct -> today's midnight is 18:30 UTC on 26 Oct.
    assert local_midnight_utc(dt.datetime(2026, 10, 27, 18, 29, tzinfo=UTC), "Asia/Kolkata") == \
        dt.datetime(2026, 10, 26, 18, 30, tzinfo=UTC)
    # One minute later it is 00:00 IST on 28 Oct.
    assert local_midnight_utc(dt.datetime(2026, 10, 27, 18, 30, tzinfo=UTC), "Asia/Kolkata") == \
        dt.datetime(2026, 10, 27, 18, 30, tzinfo=UTC)
    assert next_local_midnight_utc(dt.datetime(2026, 10, 27, 12, 0, tzinfo=UTC), "Asia/Kolkata") == \
        dt.datetime(2026, 10, 27, 18, 30, tzinfo=UTC)


def test_only_past_hours_are_generated():
    cutoff = dt.datetime(2026, 10, 23, 18, 30, tzinfo=UTC)
    hours = hours_to_generate(epoch(2026, 10, 22, 23), cutoff, 3600)
    assert hours[0] == epoch(2026, 10, 23, 0) and hours[-1] == epoch(2026, 10, 23, 18)
    assert all(h < cutoff.timestamp() for h in hours) and len(hours) == 19
    assert hours_to_generate(epoch(2026, 10, 23, 18), cutoff, 3600) == []


# ---- source-system rules ---------------------------------------------------------------------

@pytest.mark.parametrize("metrics,expected", [
    ({"engine_temp_c": None, "oil_pressure_psi": 20.0, "vibration_index": 1.0}, "UNKNOWN"),
    ({"engine_temp_c": 500.0, "oil_pressure_psi": 20.0, "vibration_index": None}, "NORMAL"),  # vibration not used
    ({"engine_temp_c": 925.0, "oil_pressure_psi": 13.1, "vibration_index": 1.0}, "NORMAL"),  # exact limits
    ({"engine_temp_c": 925.2, "oil_pressure_psi": 13.0, "vibration_index": 1.0}, "HIGH CRITICAL"),
    ({"engine_temp_c": 930.0, "oil_pressure_psi": 30.0, "vibration_index": 1.0}, "CRITICAL"),
    ({"engine_temp_c": 390.0, "oil_pressure_psi": 30.0, "vibration_index": 1.0}, "CRITICAL"),
    ({"engine_temp_c": 500.0, "oil_pressure_psi": 72.0, "vibration_index": 1.0}, "CRITICAL"),
])
def test_status_flag_follows_the_source_rule(metrics, expected):
    assert status_flag(metrics, REAL.flag_rule) == expected


def test_retention_matches_the_dataset_convention_and_month_ends():
    assert retention_start(epoch(2026, 10, 22, 23), 6) == dt.date(2026, 4, 23)   # the supplied file's own window
    assert add_months(dt.date(2026, 8, 31), -6) == dt.date(2026, 2, 28)
    recs = [{"timestamp_epoch": epoch(2026, 4, 22, 23)}, {"timestamp_epoch": epoch(2026, 4, 23, 0)},
            {"timestamp_epoch": epoch(2026, 10, 22, 23)}]
    kept, removed = apply_retention(recs, 6)
    assert removed == 1 and kept[0]["timestamp_epoch"] == epoch(2026, 4, 23, 0)


def test_generation_is_deterministic_continues_ids_and_stays_in_range(env):
    hours = [epoch(2026, 10, 23, h) for h in range(3)]
    a = generate_records(env["history"], hours, env["cfg"])
    assert a == generate_records(env["history"], hours, env["cfg"])
    assert [r["telemetry_id"] for r in a[:2]] == ["TEL-" + str(100001 + len(env["history"])),
                                                  "TEL-" + str(100002 + len(env["history"]))]
    assert len(a) == 3 * len(AIRCRAFT) and {r["tail_number"] for r in a} == set(AIRCRAFT)
    for r in a:
        assert r["status_flag"] == status_flag(r["metrics"], env["cfg"].flag_rule)
        hist = [h["metrics"]["engine_temp_c"] for h in env["history"]
                if h["tail_number"] == r["tail_number"] and h["metrics"]["engine_temp_c"] is not None]
        if r["metrics"]["engine_temp_c"] is not None:
            assert min(hist) <= r["metrics"]["engine_temp_c"] <= max(hist)


def test_an_aircraft_without_history_is_refused_never_invented(env):
    history = [r for r in env["history"] if r["tail_number"] != "N903UA"]
    with pytest.raises(JobRefused, match="N903UA"):
        generate_records(history, [epoch(2026, 10, 23, 0)], env["cfg"])


# ---- the run ---------------------------------------------------------------------------------

NOW = dt.datetime(2026, 10, 25, 2, 0, tzinfo=UTC)   # 07:30 IST on 25 Oct -> cutoff 18:30 UTC on 24 Oct


def test_a_run_appends_to_local_midnight_trims_backs_up_and_succeeds_only_after_the_etl(env):
    sim, etl = simulator(env, NOW)
    before = env["source"].read_bytes()
    result = sim.run()
    after = json.loads(env["source"].read_bytes())
    # 23 Oct 00:00 .. 24 Oct 18:00 UTC = 43 hours x 6 aircraft
    assert result.summary["hours_appended"] == 43 and result.summary["records_appended"] == 43 * 6
    assert max(r["timestamp_epoch"] for r in after) == epoch(2026, 10, 24, 18)
    assert [r["timestamp_epoch"] for r in after] == sorted(r["timestamp_epoch"] for r in after)
    ids = [r["telemetry_id"] for r in after]
    assert len(ids) == len(set(ids))                      # no duplicate ids introduced
    assert env["source"].read_bytes() == dumps(after)     # same formatting as the source
    # Original kept once, with a checksum; previous version kept.
    original = env["state"] / "original" / "telemetry.json"
    assert original.read_bytes() == before
    assert (env["state"] / "original" / "telemetry.json.sha256").read_text().strip() == sha256_bytes(before)
    assert len(list((env["state"] / "versions").glob("*.json"))) == 1
    # Published only through the ETL, and confirmed.
    assert len(etl.calls) == 1 and result.summary["published"][0]["accepted"] == 43 * 6
    assert list((env["state"] / "outbox").glob("*.json")) == []
    # ... and verified record-by-record in PostgreSQL, not merely "import Completed".
    assert list((env["state"] / "published").glob("*.json")) == []
    assert [f.name for f in (env["state"] / "verified").iterdir()] == [
        "telemetry_delta_20261023T0000Z_20261024T1800Z.json"]
    assert result.summary["verified"][0]["verified"] == 43 * 6 and result.summary["records_rekeyed"] == 0


def test_a_second_run_the_same_day_changes_nothing(env):
    sim, etl = simulator(env, NOW)
    sim.run()
    snapshot = env["source"].read_bytes()
    again = sim.run()
    assert again.summary["records_appended"] == 0 and env["source"].read_bytes() == snapshot
    assert len(etl.calls) == 1 and "Up to date" in again.message


def test_missed_days_are_caught_up_next_run(env):
    simulator(env, NOW)[0].run()
    later = simulator(env, NOW + dt.timedelta(days=3))[0].run()
    assert later.summary["hours_appended"] == 72
    last = max(r["timestamp_epoch"] for r in json.loads(env["source"].read_bytes()))
    assert last == epoch(2026, 10, 27, 18)


def test_an_etl_failure_fails_the_run_keeps_the_update_and_is_confirmed_later(env):
    svc = make_services(env["repo"], Settings(), NOW)
    out = execute_job(svc, "SimulateTelemetry", {}, record=False,
                      handlers={"SimulateTelemetry": lambda s, p, r: TelemetrySimulator(
                          env["cfg"], s, FakeEtl(env["imports"], env["repo"], "Failed"), env["state"], sleep=lambda x: None).run()})
    assert out.status == "Failed" and "FAILED" in out.error
    pending = list((env["state"] / "outbox").glob("*.json"))
    assert len(pending) == 1                              # still owed to the ETL
    # A Data Engineer retries the import; the next run confirms it without regenerating anything.
    only = next(iter(env["imports"].values()))
    only.update(status="Completed", acceptedRows=only["totalRows"])
    snapshot = env["source"].read_bytes()
    sim, etl = simulator(env, NOW)
    result = sim.run()
    assert env["source"].read_bytes() == snapshot and etl.calls == []
    assert result.summary["published"][0]["import_id"] == only["importId"]
    assert result.summary["published"][0]["reused_existing_import"] is True


def test_an_etl_that_does_not_finish_in_time_is_reported_not_claimed(env):
    sim, _ = simulator(env, NOW, final="Queued")
    with pytest.raises(JobRefused, match="still Queued"):
        sim.run()
    assert len(list((env["state"] / "outbox").glob("*.json"))) == 1


def test_crash_leftovers_are_recovered_and_the_source_is_never_half_written(env):
    original = env["source"].read_bytes()
    env["source"].with_name("telemetry.json.tmp").write_text("[{ half written")
    (env["state"] / "outbox").mkdir(parents=True)
    # A delta whose records never reached the source (crash between outbox write and replace).
    orphan = generate_records(env["history"], [epoch(2026, 10, 23, 0)], env["cfg"])
    (env["state"] / "outbox" / "telemetry_delta_orphan.json").write_bytes(dumps(orphan))
    sim, etl = simulator(env, NOW)
    result = sim.run()
    assert not env["source"].with_name("telemetry.json.tmp").exists()
    assert any("interrupted temporary file" in n for n in result.summary["recovery"])
    assert any("never reached the source" in n for n in result.summary["recovery"])
    assert len(etl.calls) == 1                            # only the real delta was published
    assert json.loads(env["source"].read_bytes())[: len(json.loads(original))] == json.loads(original)


def test_a_concurrent_run_is_refused(env):
    sim, _ = simulator(env, NOW)
    with exclusive_lock(env["state"] / "simulator.lock"):
        with pytest.raises(JobRefused, match="already running"):
            sim.run()


def test_an_invalid_source_is_refused_and_left_untouched(env):
    bad = json.loads(env["source"].read_bytes())
    bad[5]["metrics"]["rpm"] = 1
    env["source"].write_bytes(dumps(bad))
    snapshot = env["source"].read_bytes()
    with pytest.raises(JobRefused, match="failed validation"):
        simulator(env, NOW)[0].run()
    assert env["source"].read_bytes() == snapshot and not (env["state"] / "original").exists()


def test_dry_run_writes_nothing(env):
    snapshot = env["source"].read_bytes()
    result = simulator(env, NOW)[0].run(dry_run=True)
    assert result.summary["records_appended"] == 43 * 6 and "DRY RUN" in result.message
    assert env["source"].read_bytes() == snapshot and not (env["state"] / "outbox").exists()


def test_the_real_configuration_reproduces_every_flag_in_the_supplied_file():
    """The configured rule must explain the real source exactly (0 mismatches), or it is an invented rule."""
    source = REAL.source_file
    if not source.exists():
        pytest.skip("RawData file not present")
    records = json.loads(source.read_bytes())
    assert sum(status_flag(r["metrics"], REAL.flag_rule) != r["status_flag"] for r in records) == 0


# ---- ids must be unique against everything ever ingested -------------------------------------

def _first_new_id(env):
    return 1 + max(int(r["telemetry_id"][4:]) for r in env["history"])


def test_new_ids_skip_ids_postgresql_already_holds(env):
    """Regression (live, 2026-10-27): PostgreSQL held TEL-126338..126397 from an earlier file version, beyond this
    file's highest id. Ids allocated from the file alone collided and the ETL silently skipped 60 readings."""
    taken = _first_new_id(env) + 99
    env["repo"].stored_telemetry[f"TEL-{taken}"] = ("N120UA", epoch(2026, 1, 1, 0))
    sim, _ = simulator(env, NOW)
    result = sim.run()
    new_ids = [int(r["telemetry_id"][4:]) for r in json.loads(env["source"].read_bytes())
               if r["timestamp_epoch"] > epoch(2026, 10, 22, 23)]
    assert min(new_ids) == taken + 1
    assert result.summary["records_rekeyed"] == 0


def test_an_already_published_collision_is_detected_rekeyed_and_republished(env, monkeypatch):
    """The live situation: a delta was published with ids PostgreSQL already used for other readings."""
    first = _first_new_id(env)
    for i in range(12):   # the first 12 ids the old allocator would hand out belong to other, older readings
        env["repo"].stored_telemetry[f"TEL-{first + i}"] = ("N903UA", epoch(2026, 1, 1, i))
    monkeypatch.setattr(env["repo"], "fetch_max_source_record_number", lambda prefix: None)   # the old behaviour
    sim, etl = simulator(env, NOW)
    result = sim.run()
    assert result.summary["records_rekeyed"] == 12 and "re-keyed" in result.message
    source = json.loads(env["source"].read_bytes())
    ids = [r["telemetry_id"] for r in source]
    assert len(ids) == len(set(ids))
    # Every generated reading is now stored as itself - nothing lost to a collision.
    for r in source:
        if r["timestamp_epoch"] > epoch(2026, 10, 22, 23):
            assert env["repo"].stored_telemetry[r["telemetry_id"]] == (r["tail_number"], r["timestamp_epoch"])
    assert len(etl.calls) == 2                                        # the delta, then the corrective delta
    assert any(f.name.endswith(".superseded.json") for f in (env["state"] / "verified").iterdir())
    assert not list((env["state"] / "published").glob("*.json"))


def test_records_missing_after_the_etl_fail_the_run(env):
    svc = make_services(env["repo"], Settings(), NOW)
    sim = TelemetrySimulator(env["cfg"], svc, FakeEtl(env["imports"], None), env["state"], sleep=lambda s: None)
    with pytest.raises(JobRefused, match="not in PostgreSQL"):
        sim.run()



def test_a_run_interrupted_before_recording_a_result_is_closed_as_failed_by_the_next_run(env):
    repo = env["repo"]
    stale = str(uuid.uuid4())
    repo.create_run(run_id=stale, job_type="SimulateTelemetry", trigger="Cli", status="Running", parameters={},
                    requested_at=NOW - dt.timedelta(days=1), started_at=NOW - dt.timedelta(days=1),
                    worker_host="test-host")
    other_host = str(uuid.uuid4())
    repo.create_run(run_id=other_host, job_type="SimulateTelemetry", trigger="Cli", status="Running", parameters={},
                    requested_at=NOW, started_at=NOW, worker_host="another-host")
    training = str(uuid.uuid4())
    repo.create_run(run_id=training, job_type="TrainFailure", trigger="Cli", status="Running", parameters={},
                    requested_at=NOW, started_at=NOW, worker_host="test-host")
    svc = make_services(repo, Settings(), NOW)
    out = execute_job(svc, "SimulateTelemetry", {}, handlers={"SimulateTelemetry": lambda s, p, r: TelemetrySimulator(
        env["cfg"], s, FakeEtl(env["imports"], repo), env["state"], sleep=lambda x: None).run(run_id=r)})
    assert out.status == "Succeeded"
    assert repo.runs[stale]["status"] == "Failed" and "Interrupted" in repo.runs[stale]["error_message"]
    assert repo.runs[other_host]["status"] == "Running"      # the lock only proves this host is idle
    assert repo.runs[training]["status"] == "Running"        # other job types are not ours to judge
    assert repo.runs[out.run_id]["status"] == "Succeeded"
    assert f"closed interrupted run {stale} as Failed" in repo.runs[out.run_id]["result_summary"]["recovery"]


def test_a_dry_run_never_closes_other_runs(env):
    repo = env["repo"]
    stale = str(uuid.uuid4())
    repo.create_run(run_id=stale, job_type="SimulateTelemetry", trigger="Cli", status="Running", parameters={},
                    requested_at=NOW, started_at=NOW, worker_host="test-host")
    simulator(env, NOW)[0].run(dry_run=True)
    assert repo.runs[stale]["status"] == "Running"


def test_a_transient_file_lock_is_retried_and_a_persistent_one_is_raised(tmp_path, monkeypatch):
    import afp_ml.simulator as sim_mod
    monkeypatch.setattr(sim_mod.time, "sleep", lambda s: None)
    target = tmp_path / "telemetry.json.tmp"
    target.write_text("x")
    real_unlink, calls = Path.unlink, []

    def flaky(self, *a, **k):          # an antivirus scan holds the file for the first two attempts
        calls.append(self)
        if len(calls) <= 2:
            raise PermissionError(32, "The process cannot access the file")
        return real_unlink(self, *a, **k)

    monkeypatch.setattr(Path, "unlink", flaky)
    sim_mod._unlink_with_retry(target)
    assert not target.exists() and len(calls) == 3

    def locked(self, *a, **k):
        raise PermissionError(32, "The process cannot access the file")

    monkeypatch.setattr(Path, "unlink", locked)
    with pytest.raises(PermissionError):
        sim_mod._unlink_with_retry(tmp_path / "other")


# ---- scripted scenario (afp_ml.scenario) ------------------------------------------------------

def test_scenario_rotates_one_critical_and_one_high_critical_aircraft_per_period():
    from afp_ml.scenario import period_index, roles_at
    sc = SCENARIO_CFG.scenario
    assert sc is not None and sc.enabled
    seen = []
    for days in range(0, sc.rotation_days * 4, sc.rotation_days):
        when = dt.datetime.combine(sc.anchor, dt.time(12), UTC) + dt.timedelta(days=days)
        roles = roles_at(sc, AIRCRAFT, when)
        assert sorted(roles.values()) == sorted(["Critical", "HighCritical"] + ["Healthy"] * (len(AIRCRAFT) - 2))
        seen.append(tuple(t for t in AIRCRAFT if roles[t] != "Healthy"))
    assert all(a != b for a, b in zip(seen, seen[1:]))                   # the at-risk pair changes every period
    assert {t for pair in seen[:3] for t in pair} == set(AIRCRAFT)       # and every aircraft takes a turn
    assert period_index(sc, dt.datetime.combine(sc.anchor, dt.time(0), UTC) - dt.timedelta(seconds=1)) == -1


def test_scenario_readings_follow_their_role_are_deterministic_and_labelled():
    from afp_ml.scenario import roles_at
    sc = SCENARIO_CFG.scenario
    start = int(dt.datetime.combine(sc.anchor, dt.time(0), UTC).timestamp())
    hours = [start + 3600 * h for h in range(48)]
    recs = generate_records([], hours, SCENARIO_CFG, first_id=900000)
    assert recs == generate_records([], hours, SCENARIO_CFG, first_id=900000)
    assert all(r["telemetry_id"].startswith(sc.id_prefix) for r in recs)
    assert [int(r["telemetry_id"][len(sc.id_prefix):]) for r in recs] == list(range(900000, 900000 + len(recs)))
    roles = roles_at(sc, AIRCRAFT, dt.datetime.fromtimestamp(start, UTC))
    by_role = {}
    for r in recs:
        by_role.setdefault(roles[r["tail_number"]], []).append(r["metrics"])
    mean = lambda role, k: sum(m[k] for m in by_role[role]) / len(by_role[role])
    assert mean("Healthy", "engine_temp_c") < 700 and mean("Healthy", "oil_pressure_psi") > 35
    assert mean("Critical", "engine_temp_c") > 700 and mean("Critical", "oil_pressure_psi") > 35
    assert mean("HighCritical", "engine_temp_c") > 700 and mean("HighCritical", "oil_pressure_psi") < 35


def test_scenario_ids_share_one_number_space_with_regular_ids():
    from afp_ml.simulator import max_id_number_any
    recs = [{"telemetry_id": "TEL-120"}, {"telemetry_id": f"{SCENARIO_CFG.scenario.id_prefix}130"}]
    assert max_id_number_any(recs, SCENARIO_CFG) == 130
