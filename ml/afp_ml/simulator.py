"""Daily telemetry simulator: extends the rolling six-month raw telemetry file and hands the new day to the ETL.

Run at 00:00 Asia/Kolkata (Windows Task Scheduler, tools/simulator/register-daily-task.ps1) or manually with
``python -m afp_ml simulate`` (``--dry-run`` writes nothing). Each run is a ``SimulateTelemetry`` pipeline_run.

What one run does, in order (every step is safe to repeat):

1. Take an exclusive OS lock (a second concurrent run is refused; the lock dies with the process).
2. Recover: remove a half-written temp file; reconcile the outbox (a delta whose records never reached the
   source file is discarded - it will be regenerated identically).
3. Publish any delta still waiting in the outbox (a previous run that crashed or timed out).
4. Validate the source file, then generate every hourly reading from its last timestamp up to (excluding) today's
   local midnight - all strictly in the past, so nothing is future-dated. A missed day is caught up next run.
5. Before the first ever change, keep a verified copy of the original file; before every change keep the
   previous version (rotated). Write the delta to the outbox, then atomically replace the source (temp file,
   fsync, re-parse and re-hash, os.replace) with new records appended and records outside the rolling
   window removed.
6. Hand the delta to the existing ETL (the API's raw-upload service, via its CLI, as a real DataEngineer) and
   report success ONLY when the resulting telemetry_import is Completed. Otherwise the delta stays in the
   outbox and the run fails with the actual reason.

Retention trims the raw SOURCE FILE only; processed history in PostgreSQL is never deleted.
Generated values never include failures, maintenance or deliberate degradation (see the configuration).
"""

from __future__ import annotations

import contextlib
import datetime as dt
import hashlib
import json
import math
import os
import shutil
import subprocess
import time
import zoneinfo
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterator

import numpy as np

from .scenario import ScenarioConfig, metrics_for, roles_at
from .jobs import JobRefused, JobResult, Services

METRICS = ("engine_temp_c", "oil_pressure_psi", "vibration_index")
RECORD_KEYS = {"telemetry_id", "tail_number", "timestamp_epoch", "metrics", "status_flag"}
REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG = REPO_ROOT / "tools" / "simulator" / "telemetry_simulator.json"


# --------------------------------------------------------------------------------------------
# Configuration
# --------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class SimulatorConfig:
    source_file: Path
    timezone: str
    retention_months: int
    cadence_seconds: int
    id_prefix: str
    expected_aircraft: tuple[str, ...]
    history_days: int
    decimals: dict[str, int]
    flag_rule: dict[str, Any]
    api_dll: Path
    run_as: str
    wait_timeout_seconds: int
    poll_seconds: float
    keep_previous_versions: int
    data_source: str = "RAW_JSON_BRONZE"
    scenario: "ScenarioConfig | None" = None   # scripted operational scenario (afp_ml.scenario)

    @classmethod
    def load(cls, path: Path = DEFAULT_CONFIG, root: Path = REPO_ROOT) -> "SimulatorConfig":
        c = json.loads(Path(path).read_text(encoding="utf-8"))
        zoneinfo.ZoneInfo(c["timezone"])  # fail fast on an unknown zone
        return cls(
            source_file=(root / c["source_file"]).resolve(), timezone=c["timezone"],
            retention_months=int(c["retention_months"]), cadence_seconds=int(c["cadence_minutes"]) * 60,
            id_prefix=c["telemetry_id_prefix"], expected_aircraft=tuple(c["expected_aircraft"]),
            history_days=int(c["generation"]["history_days"]), decimals=dict(c["generation"]["decimals"]),
            flag_rule=dict(c["status_flag_rule"]), api_dll=(root / c["etl"]["api_dll"]).resolve(),
            run_as=c["etl"]["run_as"], wait_timeout_seconds=int(c["etl"]["wait_timeout_seconds"]),
            poll_seconds=float(c["etl"]["poll_seconds"]), keep_previous_versions=int(c["keep_previous_versions"]),
            data_source=c["etl"]["data_source"],
            scenario=ScenarioConfig.from_dict(c.get("scenario")),
        )


# --------------------------------------------------------------------------------------------
# Pure logic
# --------------------------------------------------------------------------------------------

def local_midnight_utc(now_utc: dt.datetime, timezone: str) -> dt.datetime:
    """Start of the current local day (e.g. 00:00 IST) expressed in UTC."""
    tz = zoneinfo.ZoneInfo(timezone)
    local = now_utc.astimezone(tz)
    return dt.datetime(local.year, local.month, local.day, tzinfo=tz).astimezone(dt.timezone.utc)


def next_local_midnight_utc(now_utc: dt.datetime, timezone: str) -> dt.datetime:
    tz = zoneinfo.ZoneInfo(timezone)
    local = now_utc.astimezone(tz) + dt.timedelta(days=1)
    return dt.datetime(local.year, local.month, local.day, tzinfo=tz).astimezone(dt.timezone.utc)


def hours_to_generate(last_epoch: int, cutoff_utc: dt.datetime, cadence_seconds: int) -> list[int]:
    """Every cadence step after the last reading and strictly before the cutoff (never a future reading)."""
    end = int(cutoff_utc.timestamp())
    return list(range(last_epoch + cadence_seconds, end, cadence_seconds))


def status_flag(metrics: dict[str, Any], rule: dict[str, Any]) -> str:
    labels = rule["labels"]
    if any(metrics.get(k) is None for k in rule["unknown_when_missing"]):
        return labels["unknown"]
    t, p = metrics["engine_temp_c"], metrics["oil_pressure_psi"]
    hot, low_p = t > rule["hot_above_c"], p <= rule["low_pressure_at_or_below_psi"]
    if hot and low_p:
        return labels["high_critical"]
    if hot or low_p or t < rule["cold_below_c"] or p >= rule["high_pressure_at_or_above_psi"]:
        return labels["critical"]
    return labels["normal"]


def add_months(d: dt.date, months: int) -> dt.date:
    y, m = divmod(d.month - 1 + months, 12)
    year, month = d.year + y, m + 1
    last_day = (dt.date(year + (month == 12), month % 12 + 1, 1) - dt.timedelta(days=1)).day
    return dt.date(year, month, min(d.day, last_day))


def retention_start(last_epoch: int, months: int) -> dt.date:
    """First UTC date kept: the dataset's own convention (04-23 .. 10-22 is six months ending 10-22)."""
    last_date = dt.datetime.fromtimestamp(last_epoch, dt.timezone.utc).date()
    return add_months(last_date, -months) + dt.timedelta(days=1)


def apply_retention(records: list[dict[str, Any]], months: int) -> tuple[list[dict[str, Any]], int]:
    if not records:
        return records, 0
    start = retention_start(max(r["timestamp_epoch"] for r in records), months)
    start_epoch = int(dt.datetime(start.year, start.month, start.day, tzinfo=dt.timezone.utc).timestamp())
    kept = [r for r in records if r["timestamp_epoch"] >= start_epoch]
    return kept, len(records) - len(kept)


def validate_records(records: list[Any], expected_aircraft: tuple[str, ...]) -> list[str]:
    errors: list[str] = []
    if not isinstance(records, list) or not records:
        return ["the file must be a non-empty JSON array"]
    for i, r in enumerate(records):
        if not isinstance(r, dict) or set(r) != RECORD_KEYS:
            errors.append(f"record {i}: keys must be exactly {sorted(RECORD_KEYS)}")
        elif not isinstance(r["metrics"], dict) or set(r["metrics"]) != set(METRICS):
            errors.append(f"record {i}: metrics must be exactly {list(METRICS)}")
        elif not isinstance(r["timestamp_epoch"], int):
            errors.append(f"record {i}: timestamp_epoch must be an integer")
        elif r["tail_number"] not in expected_aircraft:
            errors.append(f"record {i}: unexpected aircraft {r['tail_number']!r}")
        elif any(v is not None and not isinstance(v, (int, float)) for v in r["metrics"].values()):
            errors.append(f"record {i}: metric values must be numbers or null")
        if len(errors) >= 20:
            errors.append("... (truncated)")
            break
    return errors


def _seed(*parts: Any) -> int:
    return int.from_bytes(hashlib.sha256("|".join(map(str, parts)).encode()).digest()[:8], "big")


@dataclass(frozen=True)
class _MetricModel:
    mean: float
    std: float
    rho: float
    lo: float
    hi: float
    missing_rate: float
    last: float


def _fit(series: list[float | None], recent: list[float | None]) -> _MetricModel:
    full = [v for v in series if v is not None]
    rec = [v for v in recent if v is not None] or full
    mean = float(np.mean(rec))
    std = float(np.std(rec)) if len(rec) > 1 else 0.0
    pairs = [(a, b) for a, b in zip(recent, recent[1:]) if a is not None and b is not None]
    rho = 0.0
    if len(pairs) > 2:
        x, y = np.array(pairs).T
        if np.std(x) > 0 and np.std(y) > 0:
            rho = float(np.clip(np.corrcoef(x, y)[0, 1], 0.0, 0.99))
    return _MetricModel(mean, std, rho, min(full), max(full), sum(v is None for v in series) / len(series), rec[-1])


def generate_records(history: list[dict[str, Any]], hours: list[int], cfg: SimulatorConfig,
                     first_id: int | None = None) -> list[dict[str, Any]]:
    """New records for every expected aircraft at each hour; deterministic for a given history and hour.

    ``first_id`` must come from :meth:`TelemetrySimulator._allocate_ids` in a real run: ids have to be unique against
    everything ever ingested, not just this file (the ETL de-duplicates on source_record_id)."""
    if not hours:
        return []
    if cfg.scenario is not None and cfg.scenario.enabled:
        return _scenario_records(hours, cfg, first_id if first_id is not None else 1 + max_id_number_any(history, cfg))
    by_aircraft: dict[str, list[dict[str, Any]]] = {}
    for r in sorted(history, key=lambda r: r["timestamp_epoch"]):
        by_aircraft.setdefault(r["tail_number"], []).append(r)
    next_id = first_id if first_id is not None else 1 + max_id_number(history, cfg.id_prefix)
    window = cfg.history_days * 86400
    latent: dict[tuple[str, str], float] = {}
    models: dict[tuple[str, str], _MetricModel] = {}
    for tail in cfg.expected_aircraft:
        rows = by_aircraft.get(tail, [])
        if not rows:
            raise JobRefused(f"No history for aircraft {tail}; refusing to invent its telemetry.")
        cutoff = rows[-1]["timestamp_epoch"] - window
        recent = [r for r in rows if r["timestamp_epoch"] > cutoff]
        for m in METRICS:
            model = _fit([r["metrics"][m] for r in rows], [r["metrics"][m] for r in recent])
            models[(tail, m)] = model
            latent[(tail, m)] = model.last
    out = []
    for epoch in hours:
        for tail in cfg.expected_aircraft:
            rng = np.random.default_rng(_seed(tail, epoch))
            metrics: dict[str, float | None] = {}
            for m in METRICS:
                mod = models[(tail, m)]
                innovation = rng.normal(0.0, mod.std * math.sqrt(max(0.0, 1.0 - mod.rho ** 2)))
                value = mod.mean + mod.rho * (latent[(tail, m)] - mod.mean) + innovation
                value = min(mod.hi, max(mod.lo, value))
                latent[(tail, m)] = value
                missing = rng.random() < mod.missing_rate
                metrics[m] = None if missing else round(value, cfg.decimals[m])
            out.append({"telemetry_id": f"{cfg.id_prefix}{next_id}", "tail_number": tail, "timestamp_epoch": epoch,
                        "metrics": metrics, "status_flag": status_flag(metrics, cfg.flag_rule)})
            next_id += 1
    return out


def _scenario_records(hours: list[int], cfg: "SimulatorConfig", next_id: int) -> list[dict[str, Any]]:
    """Scripted readings (see afp_ml.scenario): labelled by the scenario id prefix, deterministic per aircraft-hour."""
    sc = cfg.scenario
    out = []
    for epoch in hours:
        roles = roles_at(sc, cfg.expected_aircraft, dt.datetime.fromtimestamp(epoch, dt.timezone.utc))
        for tail in cfg.expected_aircraft:
            metrics = metrics_for(sc, tail, roles[tail], epoch, cfg.decimals)
            out.append({"telemetry_id": f"{sc.id_prefix}{next_id}", "tail_number": tail, "timestamp_epoch": epoch,
                        "metrics": metrics, "status_flag": status_flag(metrics, cfg.flag_rule)})
            next_id += 1
    return out


def max_id_number_any(records: list[dict[str, Any]], cfg: "SimulatorConfig") -> int:
    """Highest id number under the regular OR the scenario prefix: both share one number space."""
    prefixes = [cfg.id_prefix] + ([cfg.scenario.id_prefix] if cfg.scenario is not None else [])
    return max(max_id_number(records, p) for p in prefixes)


def max_id_number(records: list[dict[str, Any]], prefix: str) -> int:
    nums = [int(r["telemetry_id"][len(prefix):]) for r in records
            if str(r["telemetry_id"]).startswith(prefix) and str(r["telemetry_id"])[len(prefix):].isdigit()]
    return max(nums) if nums else 0


def dumps(records: list[dict[str, Any]]) -> bytes:
    """Byte-for-byte the source file's style (indent=1, LF)."""
    return json.dumps(records, indent=1).encode("utf-8")


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------------------------------
# File-system safety
# --------------------------------------------------------------------------------------------

@contextlib.contextmanager
def exclusive_lock(path: Path) -> Iterator[None]:
    """OS-level non-blocking lock; released automatically if the process dies."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fh = open(path, "a+b")
    try:
        try:
            if os.name == "nt":
                import msvcrt
                fh.seek(0)
                msvcrt.locking(fh.fileno(), msvcrt.LK_NBLCK, 1)
            else:  # pragma: no cover - this deployment is Windows
                import fcntl
                fcntl.flock(fh.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise JobRefused("Another telemetry simulation is already running; this run was not started.") from exc
        yield
    finally:
        fh.close()


def atomic_write(target: Path, data: bytes) -> None:
    """Write next to the target, fsync, verify, then os.replace: a crash leaves the old file or the new one."""
    tmp = target.with_name(target.name + ".tmp")
    with open(tmp, "wb") as fh:
        fh.write(data)
        fh.flush()
        os.fsync(fh.fileno())
    written = tmp.read_bytes()
    if sha256_bytes(written) != sha256_bytes(data):
        tmp.unlink(missing_ok=True)
        raise JobRefused(f"verification of {tmp.name} failed; the source file was not replaced")
    json.loads(written)  # must parse before it may replace anything
    _replace_with_retry(tmp, target)


def _retry_on_lock(action: Callable[[], None], attempts: int = 20, delay: float = 0.1) -> None:
    """Retry briefly: on Windows an antivirus/indexer scan of a just-written file causes a transient WinError 32.
    A lock that persists is re-raised, never ignored."""
    for attempt in range(attempts):
        try:
            action()
            return
        except PermissionError:
            if attempt == attempts - 1:
                raise
            time.sleep(delay * (attempt + 1))


def _replace_with_retry(src: Path, dst: Path) -> None:
    """os.replace (atomic) with the transient-lock retry."""
    _retry_on_lock(lambda: os.replace(src, dst))


def _unlink_with_retry(path: Path) -> None:
    _retry_on_lock(path.unlink)


# --------------------------------------------------------------------------------------------
# The run
# --------------------------------------------------------------------------------------------

# Hands a delta to the ETL and waits; returns the final telemetry_import as a dict (importId, status, *Rows,
# errorMessage). The API side owns telemetry_import: the ML roles are deliberately denied it.
IngestFn = Callable[[Path], dict[str, Any]]


def default_state_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    return Path(base) / "AircraftFailurePrediction" / "simulator"


def cli_ingest(cfg: SimulatorConfig) -> IngestFn:
    """Queue a file through the API's own raw-upload service (CLI mode, no HTTP) and wait for the ETL there."""
    def ingest(path: Path) -> dict[str, Any]:
        if not cfg.api_dll.is_file():
            raise JobRefused(f"API build not found at {cfg.api_dll}; build the backend first (dotnet build).")
        proc = subprocess.run(["dotnet", str(cfg.api_dll), "ingest-raw-telemetry", "--file", str(path),
                               "--run-as", cfg.run_as, "--wait", str(cfg.wait_timeout_seconds),
                               "--poll", str(max(1, int(cfg.poll_seconds)))],
                              cwd=str(REPO_ROOT / "Backend"), capture_output=True, text=True,
                              timeout=cfg.wait_timeout_seconds + 300)
        lines = [line for line in proc.stdout.strip().splitlines() if line.startswith("{")]
        if proc.returncode not in (0, 4, 5) or not lines:
            raise JobRefused(f"ETL hand-off failed (exit {proc.returncode}): {(proc.stderr or proc.stdout).strip()[-800:]}")
        return json.loads(lines[-1])
    return ingest


class TelemetrySimulator:
    def __init__(self, cfg: SimulatorConfig, svc: Services, ingest: IngestFn, state_dir: Path,
                 sleep: Callable[[float], None] = time.sleep) -> None:
        self.cfg, self.svc, self.ingest, self.sleep = cfg, svc, ingest, sleep
        self.state = state_dir
        self.outbox, self.published = state_dir / "outbox", state_dir / "published"
        self.versions, self.original = state_dir / "versions", state_dir / "original"
        self.verified, self.high_water = state_dir / "verified", state_dir / "id_high_water_mark.txt"

    # -- helpers ---------------------------------------------------------------------------
    def _read_source(self) -> tuple[list[dict[str, Any]], bytes]:
        data = self.cfg.source_file.read_bytes()
        try:
            records = json.loads(data)
        except json.JSONDecodeError as exc:
            raise JobRefused(f"{self.cfg.source_file.name} is not valid JSON ({exc}); nothing was changed.") from exc
        errors = validate_records(records, self.cfg.expected_aircraft)
        if errors:
            raise JobRefused(f"{self.cfg.source_file.name} failed validation; nothing was changed: " + "; ".join(errors))
        return records, data

    def _close_interrupted_runs(self, current_run_id: str | None) -> list[str]:
        """Holding the OS lock proves no other simulation is alive on this host, so any earlier Running
        SimulateTelemetry row from this host was interrupted (process killed / machine off) and is closed as Failed."""
        notes = []
        for row in self.svc.repo.list_runs(["Running"]):
            if (row["job_type"] != "SimulateTelemetry" or row["id"] == current_run_id
                    or row.get("worker_host") != self.svc.host):
                continue
            message = ("Interrupted: the simulator process ended before recording a result; "
                       "closed by the next run, which found no simulation in progress.")
            self.svc.repo.update_run(row["id"], status="Failed", completed_at=self.svc.clock(),
                                     result_summary={"error": message}, error_message=message,
                                     records_processed=None, model_version_id=None)
            notes.append(f"closed interrupted run {row['id']} as Failed")
        return notes

    def _recover(self) -> list[str]:
        notes = []
        tmp = self.cfg.source_file.with_name(self.cfg.source_file.name + ".tmp")
        if tmp.exists():
            _unlink_with_retry(tmp)
            notes.append(f"removed an interrupted temporary file ({tmp.name}); the source was intact")
        return notes

    def _reconcile_outbox(self, records: list[dict[str, Any]]) -> list[str]:
        """Discard deltas whose records never reached the source (crash between outbox and replace)."""
        present = {(r["telemetry_id"], r["timestamp_epoch"]) for r in records}
        notes = []
        for f in sorted(self.outbox.glob("*.json")) if self.outbox.exists() else []:
            delta = json.loads(f.read_bytes())
            if not all((r["telemetry_id"], r["timestamp_epoch"]) in present for r in delta):
                _unlink_with_retry(f)
                notes.append(f"discarded {f.name}: its records never reached the source (they are regenerated)")
        return notes

    def _ensure_original_backup(self, data: bytes) -> Path:
        target = self.original / self.cfg.source_file.name
        if not target.exists():
            self.original.mkdir(parents=True, exist_ok=True)
            atomic_write(target, data)
            (self.original / (target.name + ".sha256")).write_text(sha256_bytes(data) + "\n", encoding="utf-8")
        if sha256_bytes(target.read_bytes()) != (self.original / (target.name + ".sha256")).read_text().strip():
            raise JobRefused(f"the original backup {target} fails its checksum; refusing to modify the source")
        return target

    def _keep_previous_version(self, data: bytes, stamp: str) -> None:
        self.versions.mkdir(parents=True, exist_ok=True)
        atomic_write(self.versions / f"{self.cfg.source_file.stem}.{stamp}.json", data)
        old = sorted(self.versions.glob(f"{self.cfg.source_file.stem}.*.json"))
        for f in old[:-self.cfg.keep_previous_versions]:
            _unlink_with_retry(f)

    def _allocate_ids(self, records: list[dict[str, Any]], count: int) -> int:
        """First of `count` ids never used before - in this file, in PostgreSQL, or by an earlier run."""
        prefixes = [self.cfg.id_prefix] + ([self.cfg.scenario.id_prefix] if self.cfg.scenario is not None else [])
        stored = max((self.svc.repo.fetch_max_source_record_number(p) or 0) for p in prefixes)
        issued = int(self.high_water.read_text().strip()) if self.high_water.exists() else 0
        first = 1 + max(max_id_number_any(records, self.cfg), stored, issued)
        if count:
            self.state.mkdir(parents=True, exist_ok=True)
            atomic_write(self.high_water, str(first + count - 1).encode())
        return first

    def _verify_published(self, now: dt.datetime) -> tuple[list[dict[str, Any]], int]:
        """Every published record must be stored AS ITSELF (same id, aircraft and time).

        A collision - an id PostgreSQL already holds for a different reading, which the ETL silently skips as
        "already stored" - is re-keyed in the source and republished; anything else missing fails the run.
        Returns (verified deltas, records re-keyed)."""
        results, rekeyed = [], 0
        for delta_path in sorted(self.published.glob("*.json")) if self.published.exists() else []:
            delta = json.loads(delta_path.read_bytes())
            stored = {r["source_record_id"]: r for r in
                      self.svc.repo.fetch_source_records([r["telemetry_id"] for r in delta], self.cfg.data_source)}
            collided, missing = [], []
            for r in delta:
                row = stored.get(r["telemetry_id"])
                if row is None:
                    missing.append(r)
                elif (row["registration_number"] != r["tail_number"]
                      or int(_utc(row["recorded_at_utc"]).timestamp()) != r["timestamp_epoch"]):
                    collided.append(r)
            if missing:
                raise JobRefused(f"{len(missing)} record(s) of {delta_path.name} are not in PostgreSQL after the ETL "
                                 f"(e.g. {missing[0]['telemetry_id']}); not reporting success.")
            if collided:
                self._rekey(collided, now)
                rekeyed += len(collided)
            self.verified.mkdir(parents=True, exist_ok=True)
            suffix = ".superseded.json" if collided else ".json"
            _replace_with_retry(delta_path, self.verified / (delta_path.stem + suffix))
            results.append({"delta": delta_path.name, "records": len(delta), "verified": len(delta) - len(collided),
                            "rekeyed": len(collided)})
        return results, rekeyed

    def _rekey(self, collided: list[dict[str, Any]], now: dt.datetime) -> None:
        """Give collided records fresh ids in the source (values unchanged) and queue them as a corrective delta."""
        records, data = self._read_source()
        first = self._allocate_ids(records, len(collided))
        mapping = {(r["telemetry_id"], r["tail_number"], r["timestamp_epoch"]): f"{self.cfg.id_prefix}{first + i}"
                   for i, r in enumerate(collided)}
        corrected = []
        for r in records:
            new_id = mapping.get((r["telemetry_id"], r["tail_number"], r["timestamp_epoch"]))
            if new_id:
                r["telemetry_id"] = new_id
                corrected.append(r)
        if len(corrected) != len(collided):
            raise JobRefused("could not locate every collided record in the source; nothing was re-keyed")
        stamp = now.strftime("%Y%m%dT%H%M%SZ")
        self._keep_previous_version(data, stamp + "-rekey")
        self.outbox.mkdir(parents=True, exist_ok=True)
        atomic_write(self.outbox / f"telemetry_delta_rekey_{stamp}.json", dumps(corrected))
        atomic_write(self.cfg.source_file, dumps(records))

    def _publish(self, delta_path: Path) -> dict[str, Any]:
        """Hand one delta to the ETL and confirm PostgreSQL has it. Raises (the delta stays queued) otherwise."""
        imp = self.ingest(delta_path)
        status, import_id = imp.get("status"), imp.get("importId")
        if status == "Completed":
            self.published.mkdir(parents=True, exist_ok=True)
            _replace_with_retry(delta_path, self.published / delta_path.name)
            return {"delta": delta_path.name, "import_id": str(import_id), "reused_existing_import": imp.get("reused"),
                    "total": imp.get("totalRows"), "accepted": imp.get("acceptedRows"),
                    "rejected": imp.get("rejectedRows"), "already_stored": imp.get("duplicateRows")}
        if status == "Failed":
            raise JobRefused(f"ETL import {import_id} for {delta_path.name} FAILED: {imp.get('errorMessage')}. "
                             "Retry it from Data Model > imports; the next run confirms it.")
        raise JobRefused(f"ETL import {import_id} for {delta_path.name} is still {status or 'not visible'} after "
                         f"{self.cfg.wait_timeout_seconds}s (is Docker/Spark running?). The file update is kept; "
                         "the next run confirms publication.")

    # -- entry point -----------------------------------------------------------------------
    def run(self, *, dry_run: bool = False, run_id: str | None = None) -> JobResult:
        now = self.svc.clock()
        cutoff = local_midnight_utc(now, self.cfg.timezone)
        with exclusive_lock(self.state / "simulator.lock"):
            notes = [] if dry_run else self._close_interrupted_runs(run_id) + self._recover()
            records, data = self._read_source()
            if not dry_run:
                notes += self._reconcile_outbox(records)
            last = max(r["timestamp_epoch"] for r in records)
            hours = hours_to_generate(last, cutoff, self.cfg.cadence_seconds)
            first_id = None if dry_run else self._allocate_ids(records, len(hours) * len(self.cfg.expected_aircraft))
            new = generate_records(records, hours, self.cfg, first_id)
            combined, removed = apply_retention(records + new, self.cfg.retention_months)
            summary: dict[str, Any] = {
                "source_file": str(self.cfg.source_file), "timezone": self.cfg.timezone,
                "local_cutoff": cutoff.astimezone(zoneinfo.ZoneInfo(self.cfg.timezone)).isoformat(),
                "previous_last_reading_utc": _iso(last), "hours_appended": len(hours), "records_appended": len(new),
                "records_removed_by_retention": removed, "records_after": len(combined),
                "window_utc": [_iso(combined[0]["timestamp_epoch"]), _iso(combined[-1]["timestamp_epoch"])],
                "dry_run": dry_run, "recovery": notes,
            }
            if dry_run:
                return JobResult(summary=summary, records_processed=len(new),
                                 message=f"DRY RUN: would append {len(new)} and remove {removed} records; nothing written")
            if new:
                stamp = now.strftime("%Y%m%dT%H%M%SZ")
                self._ensure_original_backup(data)
                self._keep_previous_version(data, stamp)
                self.outbox.mkdir(parents=True, exist_ok=True)
                delta = self.outbox / f"telemetry_delta_{_compact(hours[0])}_{_compact(hours[-1])}.json"
                atomic_write(delta, dumps(new))                       # 1. outbox first ...
                atomic_write(self.cfg.source_file, dumps(combined))   # 2. ... then the source
                summary["source_sha256"] = sha256_bytes(dumps(combined))
            published, verified, rekeyed = [], [], 0
            for _ in range(3):  # publish -> verify; a re-key queues one corrective delta, verified on the next pass
                if self.outbox.exists():
                    published += [self._publish(f) for f in sorted(self.outbox.glob("*.json"))]
                checked, n = self._verify_published(now)
                verified += checked
                rekeyed += n
                if not n:
                    break
            else:
                raise JobRefused("records kept colliding with existing ids after re-keying; not reporting success")
            summary.update(published=published, verified=verified, records_rekeyed=rekeyed)
            if not new and not published:
                msg = "Up to date: no complete hour to add before local midnight; nothing changed"
            else:
                msg = (f"Appended {len(new)} records ({len(hours)} h), removed {removed} by retention; "
                       f"{len(published)} delta(s) confirmed in PostgreSQL"
                       + (f"; {rekeyed} record(s) re-keyed after id collisions" if rekeyed else ""))
            return JobResult(summary=summary, records_processed=len(new), message=msg)


def _utc(value: Any) -> dt.datetime:
    ts = value if isinstance(value, dt.datetime) else dt.datetime.fromisoformat(str(value))
    return ts.replace(tzinfo=dt.timezone.utc) if ts.tzinfo is None else ts.astimezone(dt.timezone.utc)


def _iso(epoch: int) -> str:
    return dt.datetime.fromtimestamp(epoch, dt.timezone.utc).isoformat()


def _compact(epoch: int) -> str:
    return dt.datetime.fromtimestamp(epoch, dt.timezone.utc).strftime("%Y%m%dT%H%MZ")


def simulate_job(svc: Services, params: dict[str, Any], run_id: str | None) -> JobResult:
    cfg = SimulatorConfig.load(Path(params["config"]) if params.get("config") else DEFAULT_CONFIG)
    state = Path(params["state_dir"]) if params.get("state_dir") else default_state_dir()
    return TelemetrySimulator(cfg, svc, cli_ingest(cfg), state).run(dry_run=bool(params.get("dry_run")),
                                                                        run_id=run_id)
