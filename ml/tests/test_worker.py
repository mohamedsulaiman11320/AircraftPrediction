import datetime as dt

from afp_ml.jobs import JobRefused, JobResult, execute_job
from afp_ml.worker import Worker, normalise_parameters
from conftest import FakeRepository, make_services, make_telemetry


def _handlers(calls):
    def ok(svc, params, run_id):
        calls.append(("ok", params, run_id))
        return JobResult(summary={"done": True}, records_processed=7)

    def blocked(svc, params, run_id):
        calls.append(("blocked", params, run_id))
        return JobResult(summary={"failure_prediction_readiness": {"status": "Blocked"}}, status="Blocked",
                         message="BLOCKED: not enough labels")

    def boom(svc, params, run_id):
        raise RuntimeError("kaboom")

    def refuse(svc, params, run_id):
        raise JobRefused("no active model")

    return {"DatasetAudit": ok, "TrainFailure": blocked, "TrainAnomaly": boom, "Score": refuse}


def test_worker_processes_queue_oldest_first_and_records_status(settings):
    repo = FakeRepository()
    t = dt.datetime(2026, 1, 1, tzinfo=dt.UTC)
    r_score = repo.queue_run("Score", {"full": True}, requested_at=t + dt.timedelta(minutes=3))
    r_audit = repo.queue_run("DatasetAudit", {}, requested_at=t)
    r_fail = repo.queue_run("TrainFailure", {}, requested_at=t + dt.timedelta(minutes=1))
    r_anom = repo.queue_run("TrainAnomaly", {"includeSuspect": True}, requested_at=t + dt.timedelta(minutes=2))
    r_import = repo.queue_run("TelemetryImport", {}, requested_at=t - dt.timedelta(minutes=5))
    calls = []
    worker = Worker(make_services(repo, settings), handlers=_handlers(calls))

    out = worker.process_one()
    assert out.run_id == r_audit and out.status == "Succeeded"
    assert repo.runs[r_audit]["status"] == "Succeeded" and repo.runs[r_audit]["records_processed"] == 7
    assert repo.runs[r_audit]["worker_host"] == "test-host" and repo.runs[r_audit]["started_at_utc"] is not None
    assert repo.runs[r_audit]["result_summary"] == {"done": True}

    out = worker.process_one()
    assert out.run_id == r_fail and repo.runs[r_fail]["status"] == "Blocked" and out.exit_code == 0
    assert "BLOCKED" in repo.runs[r_fail]["error_message"]
    assert calls[-1][1]["target"] == "failure"

    out = worker.process_one()
    assert out.run_id == r_anom and repo.runs[r_anom]["status"] == "Failed"
    assert "kaboom" in repo.runs[r_anom]["error_message"] and out.exit_code == 1

    out = worker.process_one()
    assert out.run_id == r_score and repo.runs[r_score]["status"] == "Failed"
    assert repo.runs[r_score]["error_message"] == "no active model"

    assert worker.process_one() is None
    assert repo.runs[r_import]["status"] == "Queued"  # not an ML job: left for the API
    assert all(r["trigger"] == "Api" for r in repo.runs.values())
    assert len(repo.runs) == 5  # worker updates queued rows, never inserts new ones


def test_worker_once_with_empty_queue(settings):
    assert Worker(make_services(FakeRepository(), settings), handlers={}).run(once=True) == 0


def test_worker_passes_parameters(settings):
    repo = FakeRepository()
    repo.queue_run("DatasetAudit", {"foo": 1})
    calls = []
    Worker(make_services(repo, settings), handlers=_handlers(calls)).run(once=True)
    assert calls[0][1] == {"foo": 1}


def test_normalise_parameters():
    assert normalise_parameters("TrainAnomaly", None) == {"target": "anomaly"}
    assert normalise_parameters("Score", {"full": True}) == {"full": True}


def test_cli_trigger_creates_run(settings):
    repo = FakeRepository(telemetry=make_telemetry(n_aircraft=1, days=0.5))
    out = execute_job(make_services(repo, settings), "DatasetAudit", {})
    run = repo.runs[out.run_id]
    assert run["trigger"] == "Cli" and run["status"] == "Succeeded" and run["job_type"] == "DatasetAudit"
    assert run["result_summary"]["failure_prediction_readiness"]["status"] == "Blocked"


def test_real_worker_dispatch_audit(settings):
    repo = FakeRepository(telemetry=make_telemetry(n_aircraft=1, days=0.5))
    run_id = repo.queue_run("DatasetAudit", {})
    assert Worker(make_services(repo, settings)).run(once=True) == 0
    assert repo.runs[run_id]["status"] == "Succeeded"
    assert "telemetry" in repo.runs[run_id]["result_summary"]


def test_status_summary(settings):
    from afp_ml.status import collect_status, format_status_text

    repo = FakeRepository(telemetry=make_telemetry(n_aircraft=1, days=0.5))
    svc = make_services(repo, settings)
    execute_job(svc, "DatasetAudit", {})
    repo.queue_run("Score", {})
    s = collect_status(repo)
    assert s["failure_prediction"]["status"] == "Blocked"
    assert s["queued_or_running"][0]["job_type"] == "Score"
    text = format_status_text(s)
    assert "Failure prediction: Blocked" in text and "Active models:" in text
