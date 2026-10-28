"""Queue worker: executes ``pipeline_run`` rows queued by the API.

Claiming uses ``SELECT ... FOR UPDATE SKIP LOCKED`` (oldest first) so several workers can run
safely; the claimed row is set to ``Running`` with ``worker_host`` and ``started_at_utc``.
``parameters`` (jsonb) are passed to the job handler (camelCase or snake_case keys).
"""

from __future__ import annotations

import logging
import signal
import threading
import time
from typing import Any

from .jobs import (JOB_DATASET_AUDIT, JOB_REGISTER, JOB_SCORE, JOB_SCORE_CANDIDATE, JOB_TRAIN_ANOMALY, JOB_TRAIN_FAILURE,
                   Handler, RunOutcome, Services, execute_job)

log = logging.getLogger(__name__)

# ScoreCandidate is refused outside AFP_ENVIRONMENT=development (see scoring.score_candidate_job).
WORKER_JOB_TYPES = (JOB_DATASET_AUDIT, JOB_TRAIN_ANOMALY, JOB_TRAIN_FAILURE, JOB_SCORE, JOB_REGISTER, JOB_SCORE_CANDIDATE)


def normalise_parameters(job_type: str, parameters: Any) -> dict[str, Any]:
    params = dict(parameters) if isinstance(parameters, dict) else {}
    if job_type == JOB_TRAIN_FAILURE:
        params["target"] = "failure"
    elif job_type == JOB_TRAIN_ANOMALY:
        params["target"] = "anomaly"
    return params


class Worker:
    def __init__(self, services: Services, handlers: dict[str, Handler] | None = None,
                 job_types: tuple[str, ...] = WORKER_JOB_TYPES) -> None:
        self.services = services
        self.handlers = handlers
        self.job_types = job_types
        self.stop_event = threading.Event()

    def process_one(self) -> RunOutcome | None:
        """Claim and execute at most one queued job. Returns None when the queue is empty."""
        svc = self.services
        claimed = svc.repo.claim_next_queued_run(self.job_types, svc.host, svc.clock())
        if claimed is None:
            return None
        job_type = claimed["job_type"]
        log.info("Claimed pipeline_run %s (%s)", claimed["id"], job_type)
        params = normalise_parameters(job_type, claimed.get("parameters"))
        return execute_job(svc, job_type, params, trigger=claimed.get("trigger") or "Api", run_id=claimed["id"],
                           handlers=self.handlers)

    def run(self, once: bool = False) -> int:
        """Loop until stopped. With ``once`` process at most one job. Returns an exit code."""
        if once:
            outcome = self.process_one()
            if outcome is None:
                log.info("No queued jobs.")
                return 0
            return outcome.exit_code
        self._install_signal_handlers()
        poll = self.services.settings.worker_poll_seconds
        log.info("Worker started on %s (poll every %.1fs); press Ctrl+C to stop", self.services.host, poll)
        while not self.stop_event.is_set():
            try:
                outcome = self.process_one()
            except Exception:  # noqa: BLE001 - keep the worker alive on transient DB errors
                log.exception("Worker iteration failed; retrying after poll interval")
                outcome = None
            if outcome is None:
                sleep_interruptible(poll, self.stop_event)
        log.info("Worker stopped.")
        return 0

    def _install_signal_handlers(self) -> None:
        if threading.current_thread() is not threading.main_thread():
            return

        def handler(signum: int, _frame: Any) -> None:
            if self.stop_event.is_set():
                raise KeyboardInterrupt
            log.info("Stop requested (signal %s); finishing the current job. Press Ctrl+C again to abort.", signum)
            self.stop_event.set()

        signal.signal(signal.SIGINT, handler)
        if hasattr(signal, "SIGBREAK"):
            signal.signal(signal.SIGBREAK, handler)  # Ctrl+Break on Windows
        if hasattr(signal, "SIGTERM"):
            signal.signal(signal.SIGTERM, handler)


def sleep_interruptible(seconds: float, stop: threading.Event) -> None:
    """Sleep in short slices so Ctrl+C is handled promptly (also on Windows)."""
    end = time.monotonic() + seconds
    while not stop.is_set() and time.monotonic() < end:
        stop.wait(min(0.5, end - time.monotonic()))
