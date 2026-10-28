"""Job execution: every command runs through :func:`execute_job`, which records a ``pipeline_run``.

* CLI runs insert a new row (trigger ``Cli``, status ``Running``) and update it at the end.
* Worker runs update the API-queued row (trigger ``Api``) that the worker already claimed.

Final status is ``Succeeded``, ``Blocked`` (e.g. failure prediction readiness gate) or ``Failed``.
"""

from __future__ import annotations

import datetime as dt
import logging
from dataclasses import dataclass, field
from typing import Any, Callable

from .artifacts import ArtifactStore
from .config import Settings
from .repositories import Repository
from .util import new_uuid, to_jsonable, utc_now, worker_host

log = logging.getLogger(__name__)

JOB_DATASET_AUDIT = "DatasetAudit"
JOB_TRAIN_ANOMALY = "TrainAnomaly"
JOB_TRAIN_FAILURE = "TrainFailure"
JOB_SCORE = "Score"
JOB_REGISTER = "RegisterModel"
JOB_SCORE_CANDIDATE = "ScoreCandidate"   # development environment only
JOB_SIMULATE_TELEMETRY = "SimulateTelemetry"   # daily raw telemetry simulator (scheduled / CLI)
JOB_TRAIN_SENSOR_FORECAST = "TrainSensorForecast"   # backtest + record a Trained forecaster
JOB_FORECAST_SENSORS = "ForecastSensors"   # write forecasts with the Active forecaster only

MAX_ERROR_MESSAGE = 4000


class JobRefused(Exception):
    """An expected, user-actionable refusal (e.g. no Active model). Recorded as Failed without traceback."""


@dataclass
class Services:
    settings: Settings
    repo: Repository
    store: ArtifactStore
    clock: Callable[[], dt.datetime] = utc_now
    host: str = field(default_factory=worker_host)


@dataclass
class JobResult:
    summary: dict[str, Any]
    status: str = "Succeeded"
    records_processed: int | None = None
    model_version_id: str | None = None
    message: str | None = None


@dataclass
class RunOutcome:
    run_id: str | None
    job_type: str
    status: str
    result: JobResult | None = None
    error: str | None = None

    @property
    def exit_code(self) -> int:
        return 0 if self.status in ("Succeeded", "Blocked") else 1


Handler = Callable[[Services, dict[str, Any], "str | None"], JobResult]


def _handlers() -> dict[str, Handler]:
    from .registry import register_job
    from .scoring import score_candidate_job, score_job
    from .simulator import simulate_job
    from .training import audit_job, train_anomaly_job, train_failure_job
    from .forecast_jobs import forecast_sensors_job, train_sensor_forecast_job

    return {
        JOB_DATASET_AUDIT: audit_job,
        JOB_TRAIN_ANOMALY: train_anomaly_job,
        JOB_TRAIN_FAILURE: train_failure_job,
        JOB_SCORE: score_job,
        JOB_REGISTER: register_job,
        JOB_SCORE_CANDIDATE: score_candidate_job,
        JOB_SIMULATE_TELEMETRY: simulate_job,
        JOB_TRAIN_SENSOR_FORECAST: train_sensor_forecast_job,
        JOB_FORECAST_SENSORS: forecast_sensors_job,
    }


SUPPORTED_JOB_TYPES = (JOB_DATASET_AUDIT, JOB_TRAIN_ANOMALY, JOB_TRAIN_FAILURE, JOB_SCORE, JOB_REGISTER,
                       JOB_SCORE_CANDIDATE, JOB_SIMULATE_TELEMETRY)


def execute_job(services: Services, job_type: str, parameters: dict[str, Any] | None = None, *,
                trigger: str = "Cli", run_id: str | None = None, record: bool = True,
                handlers: dict[str, Handler] | None = None) -> RunOutcome:
    params = dict(parameters or {})
    handlers = handlers if handlers is not None else _handlers()
    repo = services.repo
    now = services.clock()
    if record and run_id is None:
        run_id = new_uuid()
        repo.create_run(run_id=run_id, job_type=job_type, trigger=trigger, status="Running",
                        parameters=to_jsonable(params), requested_at=now, started_at=now, worker_host=services.host)
        log.info("pipeline_run %s created (%s, trigger %s)", run_id, job_type, trigger)
    handler = handlers.get(job_type)
    try:
        if handler is None:
            raise JobRefused(f"job type {job_type!r} is not handled by the ML worker")
        result = handler(services, params, run_id)
    except JobRefused as exc:
        return _fail(services, run_id, job_type, str(exc), record, log_traceback=False)
    except Exception as exc:  # noqa: BLE001 - every failure must be recorded on the run row
        return _fail(services, run_id, job_type, f"{type(exc).__name__}: {exc}", record, log_traceback=True)

    if record and run_id is not None:
        summary = dict(result.summary)
        if result.message:
            summary.setdefault("message", result.message)
        repo.update_run(run_id, status=result.status, completed_at=services.clock(),
                        result_summary=to_jsonable(summary),
                        error_message=result.message if result.status == "Blocked" else None,
                        records_processed=result.records_processed, model_version_id=result.model_version_id)
    log.info("%s finished with status %s%s", job_type, result.status, f": {result.message}" if result.message else "")
    return RunOutcome(run_id, job_type, result.status, result=result)


def _fail(services: Services, run_id: str | None, job_type: str, message: str, record: bool,
          log_traceback: bool) -> RunOutcome:
    if log_traceback:
        log.exception("%s failed: %s", job_type, message)
    else:
        log.error("%s refused: %s", job_type, message)
    if record and run_id is not None:
        try:
            services.repo.update_run(run_id, status="Failed", completed_at=services.clock(),
                                     result_summary={"error": message[:MAX_ERROR_MESSAGE]},
                                     error_message=message[:MAX_ERROR_MESSAGE], records_processed=None,
                                     model_version_id=None)
        except Exception:  # noqa: BLE001
            log.exception("could not record failure on pipeline_run %s", run_id)
    return RunOutcome(run_id, job_type, "Failed", error=message)
