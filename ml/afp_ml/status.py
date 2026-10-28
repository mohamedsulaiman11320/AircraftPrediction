"""`status` command: read-only overview of models, runs, queue and readiness."""

from __future__ import annotations

from typing import Any

from .repositories import Repository
from .util import iso, to_jsonable


def _model_brief(m: dict[str, Any]) -> dict[str, Any]:
    metrics = m.get("metrics") or {}
    return {"id": m["id"], "model_name": m["model_name"], "version": m["version"], "model_type": m["model_type"],
            "algorithm": m["algorithm"], "status": m["status"], "created_at_utc": iso(m.get("created_at_utc")),
            "registered_at_utc": iso(m.get("registered_at_utc")), "activated_at_utc": iso(m.get("activated_at_utc")),
            "calibration_validated": metrics.get("calibration_validated")}


def _run_brief(r: dict[str, Any]) -> dict[str, Any]:
    return {"id": r["id"], "job_type": r["job_type"], "status": r["status"], "trigger": r["trigger"],
            "requested_at_utc": iso(r.get("requested_at_utc")), "started_at_utc": iso(r.get("started_at_utc")),
            "completed_at_utc": iso(r.get("completed_at_utc")), "records_processed": r.get("records_processed"),
            "error_message": r.get("error_message"), "worker_host": r.get("worker_host")}


def collect_status(repo: Repository) -> dict[str, Any]:
    active = repo.list_model_versions(None, ["Active"])
    registered = repo.list_model_versions(None, ["Registered", "Approved"])
    trained = repo.list_model_versions(None, ["Trained"])
    last_runs = repo.latest_runs_by_job_type()
    queue = repo.list_runs(["Queued", "Running"])
    audit = repo.latest_successful_run("DatasetAudit")
    readiness = None
    if audit and audit.get("result_summary"):
        readiness = (audit["result_summary"] or {}).get("failure_prediction_readiness")
    has_active_failure = any(m["model_type"] == "FailurePrediction" for m in active)
    return to_jsonable({
        "active_models": [_model_brief(m) for m in active],
        "registered_or_approved_models": [_model_brief(m) for m in registered],
        "trained_models": [_model_brief(m) for m in trained],
        "last_runs": [_run_brief(r) for r in last_runs],
        "queued_or_running": [_run_brief(r) for r in queue],
        "failure_prediction": {
            "status": "Active" if has_active_failure else (readiness or {}).get("status", "Unknown"),
            "readiness": readiness,
            "readiness_from_audit_run": audit["id"] if audit else None,
            "note": None if readiness else "No successful DatasetAudit run yet - run `python -m afp_ml audit`.",
        },
    })


def format_status_text(s: dict[str, Any]) -> str:
    lines = ["Active models:"]
    lines += [f"  {m['model_type']}: {m['model_name']} {m['version']} ({m['algorithm']})" for m in s["active_models"]] \
        or ["  (none)"]
    lines.append("Registered/approved models:")
    lines += [f"  {m['model_type']}: {m['model_name']} {m['version']} [{m['status']}] id={m['id']}"
              for m in s["registered_or_approved_models"]] or ["  (none)"]
    lines.append("Trained (unregistered) models:")
    lines += [f"  {m['model_type']}: {m['model_name']} {m['version']} id={m['id']}" for m in s["trained_models"]] \
        or ["  (none)"]
    lines.append("Last run per job type:")
    lines += [f"  {r['job_type']}: {r['status']} ({r['trigger']}) requested {r['requested_at_utc']}"
              + (f" - {r['error_message']}" if r.get("error_message") else "") for r in s["last_runs"]] or ["  (none)"]
    lines.append("Queued/running jobs:")
    lines += [f"  {r['job_type']} {r['status']} id={r['id']} requested {r['requested_at_utc']}"
              for r in s["queued_or_running"]] or ["  (none)"]
    fp = s["failure_prediction"]
    lines.append(f"Failure prediction: {fp['status']}")
    for reason in (fp.get("readiness") or {}).get("reasons", []):
        lines.append(f"  - {reason}")
    if fp.get("note"):
        lines.append(f"  {fp['note']}")
    return "\n".join(lines)
