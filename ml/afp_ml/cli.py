"""Command line interface: ``python -m afp_ml <command>``.

Exit codes: 0 = succeeded (or Blocked by the readiness gate), 1 = job failed / refused,
2 = configuration error (e.g. missing DSN), 3 = database unreachable.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
import uuid
from typing import Any, Sequence

from . import __version__
from .artifacts import ArtifactStore, make_artifact_store
from .config import ConfigError, Settings, default_env_file, load_env_files, root_env_file
from .db import DatabaseError
from .jobs import (JOB_DATASET_AUDIT, JOB_FORECAST_SENSORS, JOB_REGISTER, JOB_SCORE, JOB_SCORE_CANDIDATE, JOB_SIMULATE_TELEMETRY,
                   JOB_TRAIN_ANOMALY, JOB_TRAIN_FAILURE, JOB_TRAIN_SENSOR_FORECAST, RunOutcome, Services)
from .util import to_jsonable

log = logging.getLogger("afp_ml")

EXIT_OK, EXIT_FAILED, EXIT_CONFIG, EXIT_DB = 0, 1, 2, 3


def _uuid(value: str) -> str:
    try:
        return str(uuid.UUID(value))
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"not a UUID: {value!r}") from exc


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="python -m afp_ml",
        description="Aircraft Failure Prediction Platform - ML subsystem (dataset audit, Isolation Forest anomaly "
                    "detection, gated failure prediction, scoring and the pipeline_run worker).")
    p.add_argument("--version", action="version", version=f"afp_ml {__version__}")
    p.add_argument("--log-level", default=None, help="DEBUG, INFO (default, or AFP_LOG_LEVEL), WARNING, ERROR")
    p.add_argument("--env-file", default=None, help="Path to a .env file (default: AFP_ML_ENV_FILE or ml/.env); "
                                                    "the repository-root .env is also read without overriding it")
    sub = p.add_subparsers(dest="command", required=True, metavar="<command>")

    a = sub.add_parser("audit", help="Dataset audit + failure-prediction readiness (job type DatasetAudit)",
                       description="Audit telemetry quality/coverage and maintenance labels; compute the "
                                   "failure_prediction_readiness gate. Reads with AFP_ML_READER_DSN and records a "
                                   "pipeline_run row with AFP_ML_WRITER_DSN.")
    a.add_argument("--json", action="store_true", help="print the full result_summary as JSON")
    a.add_argument("--no-record", action="store_true",
                   help="do not write a pipeline_run row (only the reader DSN is needed)")

    t = sub.add_parser("train", help="Train a model (TrainAnomaly / TrainFailure); result status Trained",
                       description="--target anomaly trains an Isolation Forest on validated telemetry. "
                                   "--target failure runs the readiness gate first and records status Blocked "
                                   "(exit code 0, no model) when the data are insufficient.")
    t.add_argument("--target", choices=["anomaly", "failure", "forecast"], default="anomaly",
                   help="forecast = backtest the sensor forecaster and record a Trained candidate (TrainSensorForecast)")
    t.add_argument("--include-suspect", action="store_true",
                   help="anomaly only: also train on quality_status Suspect rows (default Valid only)")
    t.add_argument("--json", action="store_true")

    r = sub.add_parser("register", help="Move a model_version Trained -> Registered after verifying its artifact",
                       description="Verifies the artifact exists inside AFP_MODEL_DIR, its SHA-256 matches the "
                                   "database and metrics are present, then sets status Registered. Approval and "
                                   "activation are done by humans through the API/UI.")
    g = r.add_mutually_exclusive_group(required=True)
    g.add_argument("--model-version-id", type=_uuid)
    g.add_argument("--latest", action="store_true", help="the most recently created Trained model")
    r.add_argument("--model-type", choices=["AnomalyDetection", "FailurePrediction", "SensorForecast"], help="filter for --latest")
    r.add_argument("--json", action="store_true")

    s = sub.add_parser("score", help="Score new telemetry with the ACTIVE anomaly model; write risk indicators",
                       description="Loads the Active AnomalyDetection model (SHA-256 verified before loading), "
                                   "scores telemetry above the watermark, stores anomalous readings and per-aircraft "
                                   "risk indicators (PendingReview).")
    s.add_argument("--full", action="store_true", help="re-score all telemetry (existing results are kept)")
    s.add_argument("--json", action="store_true")

    sim = sub.add_parser("simulate", help="Daily telemetry simulator: extend the rolling raw file and publish via the ETL",
                         description="Appends every hourly reading up to today's local midnight (Asia/Kolkata), trims "
                                     "the rolling six-month window, writes atomically with backups, hands the new "
                                     "records to the existing ETL and succeeds only when PostgreSQL has them. "
                                     "Configuration: tools/simulator/telemetry_simulator.json.")
    sim.add_argument("--dry-run", action="store_true", help="report what would change; write nothing, record no run")
    sim.add_argument("--json", action="store_true")

    fc = sub.add_parser("forecast", help="Write sensor forecasts with the ACTIVE SensorForecast model (ForecastSensors)",
                        description="Forecasts each aircraft's validated horizons/metrics from its last recorded reading. "
                                    "Does nothing without an Active (approved) forecaster. Idempotent.")
    fc.add_argument("--json", action="store_true")

    sub.add_parser("sync-artifacts", help="Upload existing model artifacts to this environment's MinIO bucket",
                   description="For every model_version without artifact_uri: verify the local file against its "
                               "recorded SHA-256, upload it with its metadata (never overwriting different content) "
                               "and record the URI. Idempotent.")

    sc = sub.add_parser("score-candidate", help="DEVELOPMENT ONLY: score every aircraft with a chosen failure candidate",
                        description="Scores all aircraft as of the latest processed telemetry with an explicitly "
                                    "selected FailurePrediction model of any status. Refused unless "
                                    "AFP_ENVIRONMENT=development, which is never allowed on the operational database.")
    sc.add_argument("--model-version-id", type=_uuid, required=True)
    sc.add_argument("--json", action="store_true")

    hx = sub.add_parser("horizon-experiment",
                        help="DEVELOPMENT ONLY: compare the 30-day failure horizon with a 7-day one (read-only)",
                        description="Rolling-origin comparison of two failure horizons on the development data with the "
                                    "existing labels, features, censoring and calibration gates. Connects read-only, "
                                    "records no run, registers nothing; writes its report under "
                                    "AFP_MODEL_DIR/experiments/7-day-experiment/. Refused unless "
                                    "AFP_ENVIRONMENT=development.")
    hx.add_argument("--json", action="store_true", help="print the full report")

    st = sub.add_parser("status", help="Show models, last runs, queued jobs and readiness (read-only)")
    st.add_argument("--json", action="store_true")

    w = sub.add_parser("worker", help="Execute pipeline_run jobs queued by the API",
                       description="Polls pipeline_run for Queued DatasetAudit/TrainAnomaly/TrainFailure/Score/"
                                   "RegisterModel jobs (FOR UPDATE SKIP LOCKED, oldest first). Ctrl+C stops after "
                                   "the current job.")
    w.add_argument("--once", action="store_true", help="process at most one job and exit")
    return p


def _setup_logging(level: str) -> None:
    logging.basicConfig(level=getattr(logging, level.upper(), logging.INFO), stream=sys.stderr,
                        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s")


def _services(settings: Settings) -> Services:
    from .repositories import PostgresRepository

    repo = PostgresRepository(settings.reader_dsn, settings.writer_dsn, settings.db_connect_timeout,
                              expected_db_name=settings.db_name)
    return Services(settings=settings, repo=repo, store=make_artifact_store(settings))


def _print_outcome(outcome: RunOutcome, as_json: bool, text: str | None = None) -> None:
    if as_json:
        payload = {"pipeline_run_id": outcome.run_id, "job_type": outcome.job_type, "status": outcome.status,
                   "error": outcome.error,
                   "result_summary": outcome.result.summary if outcome.result else None,
                   "message": outcome.result.message if outcome.result else None}
        print(json.dumps(to_jsonable(payload), indent=2))
        return
    if text:
        print(text)
    if outcome.result and outcome.result.message:
        print(outcome.result.message)
    if outcome.error:
        print(f"FAILED: {outcome.error}", file=sys.stderr)
    print(f"{outcome.job_type}: {outcome.status}" + (f" (pipeline_run {outcome.run_id})" if outcome.run_id else ""))


def run(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        # Precedence: real environment > --env-file / AFP_ML_ENV_FILE / ml/.env > repository-root .env.
        load_env_files([args.env_file or default_env_file(), root_env_file()])
        settings = Settings.from_env()
    except (ConfigError, OSError) as exc:
        _setup_logging("INFO")
        print(f"Configuration error: {exc}", file=sys.stderr)
        return EXIT_CONFIG
    _setup_logging(args.log_level or settings.log_level)

    try:
        return _dispatch(args, settings)
    except ConfigError as exc:
        print(f"Configuration error: {exc}", file=sys.stderr)
        return EXIT_CONFIG
    except DatabaseError as exc:
        print(f"Database error: {exc}", file=sys.stderr)
        return EXIT_DB


def _dispatch(args: argparse.Namespace, settings: Settings) -> int:
    from .jobs import execute_job

    cmd = args.command
    if cmd == "horizon-experiment":
        return _horizon_experiment(args, settings)
    # Validate configuration BEFORE connecting anywhere.
    if cmd == "status" or (cmd == "audit" and args.no_record):
        settings.require_reader_dsn()
    else:
        settings.require_writer_dsn()
    svc = _services(settings)
    try:
        if cmd == "status":
            from .status import collect_status, format_status_text

            data = collect_status(svc.repo)
            print(json.dumps(data, indent=2) if args.json else format_status_text(data))
            return EXIT_OK
        if cmd == "sync-artifacts":
            from .artifact_sync import sync_artifacts

            from .artifacts import ArtifactError

            try:
                result = sync_artifacts(svc.repo, svc.store)
            except ArtifactError as exc:
                print(f"Configuration error: {exc}", file=sys.stderr)
                return EXIT_CONFIG
            print(json.dumps(result, indent=2))
            return EXIT_OK if not result["failed"] else 1
        if cmd == "worker":
            from .worker import Worker

            return Worker(svc).run(once=args.once)
        params: dict[str, Any]
        if cmd == "audit":
            outcome = execute_job(svc, JOB_DATASET_AUDIT, {}, record=not args.no_record)
            text = None
            if outcome.result and not args.json:
                from .audit import format_audit_text

                text = format_audit_text(outcome.result.summary)
            _print_outcome(outcome, args.json, text)
            return outcome.exit_code
        if cmd == "train":
            if args.target == "failure":
                outcome = execute_job(svc, JOB_TRAIN_FAILURE, {"target": "failure"})
            elif args.target == "forecast":
                outcome = execute_job(svc, JOB_TRAIN_SENSOR_FORECAST, {"target": "forecast"})
            else:
                params = {"target": "anomaly", "include_suspect": bool(args.include_suspect)}
                outcome = execute_job(svc, JOB_TRAIN_ANOMALY, params)
        elif cmd == "register":
            params = {"model_version_id": args.model_version_id, "latest": bool(args.latest),
                      "model_type": args.model_type}
            outcome = execute_job(svc, JOB_REGISTER, params)
        elif cmd == "score":
            outcome = execute_job(svc, JOB_SCORE, {"full": bool(args.full)})
        elif cmd == "simulate":
            outcome = execute_job(svc, JOB_SIMULATE_TELEMETRY, {"dry_run": bool(args.dry_run)},
                                  record=not args.dry_run)
        elif cmd == "forecast":
            outcome = execute_job(svc, JOB_FORECAST_SENSORS, {})
        elif cmd == "score-candidate":
            outcome = execute_job(svc, JOB_SCORE_CANDIDATE, {"model_version_id": args.model_version_id})
        else:  # pragma: no cover - argparse prevents this
            raise ConfigError(f"unknown command {cmd}")
        _print_outcome(outcome, args.json)
        return outcome.exit_code
    finally:
        close = getattr(svc.repo, "close", None)
        if close:
            close()


def _horizon_experiment(args: argparse.Namespace, settings: Settings) -> int:
    from .config import ENVIRONMENT_DEVELOPMENT
    from .horizon_experiment import run_horizon_experiment
    from .jobs import JobRefused
    from .repositories import PostgresRepository

    if settings.environment != ENVIRONMENT_DEVELOPMENT:   # refused before connecting anywhere
        print("Refused: the horizon experiment runs only with AFP_ENVIRONMENT=development (synthetic data).",
              file=sys.stderr)
        return EXIT_CONFIG
    settings.require_reader_dsn()
    # Read-only by construction: no writer DSN, so the experiment cannot write to the database at all.
    repo = PostgresRepository(settings.reader_dsn, None, settings.db_connect_timeout, expected_db_name=settings.db_name)
    try:
        report = run_horizon_experiment(Services(settings=settings, repo=repo, store=make_artifact_store(settings)))
    except JobRefused as exc:
        print(f"Refused: {exc}", file=sys.stderr)
        return EXIT_CONFIG
    finally:
        repo.close()
    if args.json:
        print(json.dumps(to_jsonable(report), indent=2))
    else:
        print(f"{report['experiment_id']} {report['run_stamp']}: {report['decision']['outcome']}")
        for reason in report["decision"]["reasons"]:
            print(f"  - {reason}")
        print(f"Report: {report['artifacts']['local_folder']}")
    return EXIT_OK


def main(argv: Sequence[str] | None = None) -> None:
    sys.exit(run(argv))
