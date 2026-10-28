"""Shared fixtures: synthetic in-memory data and a fake repository (no PostgreSQL needed)."""

from __future__ import annotations

import datetime as dt
import uuid
from dataclasses import replace
from typing import Any, Sequence

import numpy as np
import pandas as pd
import pytest

from afp_ml.artifacts import ArtifactStore
from afp_ml.config import Settings
from afp_ml.features import BreachLimits
from afp_ml.jobs import Services
from afp_ml.repositories import empty_telemetry_frame, normalise_maintenance, normalise_telemetry

T0 = pd.Timestamp("2026-01-01T00:00:00Z")


def aircraft_ids(n: int) -> list[str]:
    return [str(uuid.UUID(int=i + 1)) for i in range(n)]


def make_telemetry(n_aircraft: int = 3, sensors: int = 2, days: float = 3, interval_minutes: int = 10,
                   seed: int = 0, start: pd.Timestamp = T0, first_id: int = 1) -> pd.DataFrame:
    rng = np.random.default_rng(seed)
    rows = []
    n = int(days * 24 * 60 / interval_minutes)
    times = start + pd.to_timedelta(np.arange(n) * interval_minutes, unit="min")
    for a in aircraft_ids(n_aircraft):
        for s in range(sensors):
            base_t = 80 + 5 * s + rng.normal(0, 1)
            base_p = 300 + 10 * s
            phase = rng.uniform(0, 6.28)
            t = base_t + 3 * np.sin(np.arange(n) / 144 * 6.28 + phase) + rng.normal(0, 0.3, n)
            p = base_p + 2 * np.cos(np.arange(n) / 144 * 6.28 + phase) + rng.normal(0, 0.3, n)
            for i in range(n):
                rows.append((a, f"S{s}", times[i], t[i], p[i]))
    df = pd.DataFrame(rows, columns=["aircraft_id", "sensor_id", "recorded_at_utc", "temperature_c", "pressure_kpa"])
    df = df.sort_values(["recorded_at_utc", "aircraft_id", "sensor_id"]).reset_index(drop=True)
    df["id"] = np.arange(first_id, first_id + len(df))
    df["quality_status"] = "Valid"
    df["quality_flags"] = None
    df["data_source"] = "TEST"
    return normalise_telemetry(df)


def make_aircraft(n: int) -> pd.DataFrame:
    return pd.DataFrame({"aircraft_id": aircraft_ids(n), "registration_number": [f"T-{i:02d}" for i in range(n)]})


def make_mlog(events: list[tuple[str, pd.Timestamp]], event_type: str = "Unscheduled", verified: bool = True,
              confirmed: Any = True) -> pd.DataFrame:
    rows = [{"id": str(uuid.uuid4()), "aircraft_id": a, "component_id": None, "event_type": event_type,
             "occurred_at_utc": t, "actual_failure_confirmed": confirmed, "is_verified": verified, "source": "Manual"}
            for a, t in events]
    if not rows:
        return normalise_maintenance(pd.DataFrame(columns=["id"]))
    return normalise_maintenance(pd.DataFrame(rows))


def make_failure_dataset(n_aircraft: int = 8, days: int = 400, per_day: int = 6, seed: int = 1,
                         start: pd.Timestamp = T0) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Synthetic labelled data for TESTS ONLY: temperature drifts upward in the 10 days before each failure."""
    rng = np.random.default_rng(seed)
    ids = aircraft_ids(n_aircraft)
    tel_rows, events = [], []
    n = days * per_day
    times = start + pd.to_timedelta(np.arange(n) * (24 / per_day), unit="h")
    for a in ids:
        t_fail = []
        day = rng.integers(15, 40)
        while day < days:
            t_fail.append(start + pd.Timedelta(days=int(day), hours=int(rng.integers(1, 23))))
            day += rng.integers(30, 60)
        events += [(a, t) for t in t_fail]
        temp = 80 + rng.normal(0, 1.0, n)
        pres = 300 + rng.normal(0, 1.0, n)
        for tf in t_fail:
            dt_days = (tf - times).total_seconds().to_numpy() / 86400.0
            ramp = (dt_days > 0) & (dt_days <= 10)
            temp = temp + np.where(ramp, 12 * (1 - dt_days / 10), 0.0)
            pres = pres - np.where(ramp, 6 * (1 - dt_days / 10), 0.0)
        for i in range(n):
            tel_rows.append((a, "S0", times[i], temp[i], pres[i]))
    df = pd.DataFrame(tel_rows, columns=["aircraft_id", "sensor_id", "recorded_at_utc", "temperature_c", "pressure_kpa"])
    df["id"] = np.arange(1, len(df) + 1)
    df["quality_status"] = "Valid"
    df["quality_flags"] = None
    df["data_source"] = "TEST"
    return normalise_telemetry(df), make_mlog(events)


class FakeRepository:
    """In-memory implementation of afp_ml.repositories.Repository."""

    def __init__(self, telemetry: pd.DataFrame | None = None, mlog: pd.DataFrame | None = None,
                 aircraft: pd.DataFrame | None = None) -> None:
        self.telemetry = telemetry if telemetry is not None else empty_telemetry_frame()
        self.mlog = mlog if mlog is not None else make_mlog([])
        if aircraft is None:
            ids = sorted(self.telemetry["aircraft_id"].unique()) if len(self.telemetry) else []
            aircraft = pd.DataFrame({"aircraft_id": ids, "registration_number": [f"T-{i:02d}" for i in range(len(ids))]})
        self.aircraft = aircraft
        # Same values as the seeded health_threshold BR-01 rows.
        self.breach_limits = BreachLimits(pressure_low_psi=35.0, temperature_high_c=700.0)
        self.models: list[dict[str, Any]] = []
        self.stored_telemetry: dict[str, tuple[str, int]] = {}
        self.runs: dict[str, dict[str, Any]] = {}
        self.anomalies: list[dict[str, Any]] = []
        self.predictions: list[dict[str, Any]] = []
        self._seq = 0

    # reads
    def fetch_aircraft(self) -> pd.DataFrame:
        return self.aircraft.copy()

    def fetch_telemetry(self, *, min_id_exclusive: int | None = None, from_utc: Any = None, to_utc: Any = None,
                        aircraft_ids: Sequence[str] | None = None,
                        quality_statuses: Sequence[str] | None = None) -> pd.DataFrame:
        df = self.telemetry
        if min_id_exclusive is not None:
            df = df[df["id"] > min_id_exclusive]
        if from_utc is not None:
            df = df[df["recorded_at_utc"] >= pd.Timestamp(from_utc)]
        if to_utc is not None:
            df = df[df["recorded_at_utc"] <= pd.Timestamp(to_utc)]
        if aircraft_ids is not None:
            df = df[df["aircraft_id"].isin(list(aircraft_ids))]
        if quality_statuses is not None:
            df = df[df["quality_status"].isin(list(quality_statuses))]
        return df.reset_index(drop=True).copy()

    def fetch_maintenance_log(self) -> pd.DataFrame:
        return self.mlog.copy()

    def fetch_breach_limits(self) -> BreachLimits:
        return self.breach_limits

    # The telemetry the ETL has stored, keyed by source_record_id -> (registration, epoch). Tests' fake ETL writes it.
    def fetch_max_source_record_number(self, prefix: str) -> int | None:
        nums = [int(i[len(prefix):]) for i in self.stored_telemetry if i.startswith(prefix) and i[len(prefix):].isdigit()]
        return max(nums) if nums else None

    def fetch_source_records(self, ids: Sequence[str], data_source: str) -> list[dict[str, Any]]:
        out = []
        for i in ids:
            if i in self.stored_telemetry:
                reg, epoch = self.stored_telemetry[i]
                out.append({"source_record_id": i, "registration_number": reg,
                            "recorded_at_utc": dt.datetime.fromtimestamp(epoch, dt.UTC)})
        return out


    def fetch_last_telemetry_per_aircraft(self) -> pd.DataFrame:
        last = self.telemetry.groupby("aircraft_id")["recorded_at_utc"].max() if len(self.telemetry) else pd.Series(dtype=object)
        df = self.aircraft.copy()
        df["last_telemetry_at_utc"] = pd.to_datetime(df["aircraft_id"].map(last), utc=True)
        return df

    def fetch_forecast_conditions(self) -> dict[str, set[str]]:
        return getattr(self, "forecast_conditions", {})

    def fetch_anomaly_counts(self, model_version_id: str, since_utc: Any) -> pd.DataFrame:
        counts: dict[tuple[str, str], int] = {}
        for r in self.anomalies:
            if r["model_version_id"] == model_version_id and pd.Timestamp(r["observed_at_utc"]) >= pd.Timestamp(since_utc) \
                    and r.get("investigation_status") != "Dismissed":
                k = (r["aircraft_id"], r["severity"])
                counts[k] = counts.get(k, 0) + 1
        return pd.DataFrame([(a, s, c) for (a, s), c in counts.items()], columns=["aircraft_id", "severity", "count"])

    def fetch_telemetry_counts(self, since_utc: Any, quality_statuses: Sequence[str]) -> pd.DataFrame:
        df = self.telemetry
        df = df[(df["recorded_at_utc"] >= pd.Timestamp(since_utc)) & df["quality_status"].isin(list(quality_statuses))]
        c = df.groupby("aircraft_id").size()
        return pd.DataFrame({"aircraft_id": c.index.astype(str), "readings": c.values})

    def get_model_version(self, model_version_id: str) -> dict[str, Any] | None:
        return next((m for m in self.models if m["id"] == model_version_id), None)

    def list_model_versions(self, model_type: str | None = None, statuses: Sequence[str] | None = None):
        rows = [m for m in self.models if (model_type is None or m["model_type"] == model_type)
                and (statuses is None or m["status"] in statuses)]
        return sorted(rows, key=lambda m: m["created_at_utc"], reverse=True)

    def set_artifact_uri(self, model_version_id: str, artifact_uri: str) -> None:
        m = self.get_model_version(model_version_id)
        if m is not None and not m.get("artifact_uri"):
            m["artifact_uri"] = artifact_uri

    def get_active_model(self, model_type: str) -> dict[str, Any] | None:
        rows = self.list_model_versions(model_type, ["Active"])
        return rows[0] if rows else None

    def get_score_watermark(self, model_version_id: str) -> int | None:
        vals = [r["result_summary"]["max_telemetry_record_id"] for r in self.runs.values()
                if r["job_type"] == "Score" and r["status"] == "Succeeded" and r.get("result_summary")
                and (r.get("model_version_id") == model_version_id
                     or r["result_summary"].get("model_version_id") == model_version_id)]
        return max(vals) if vals else None

    def latest_runs_by_job_type(self):
        out: dict[str, dict[str, Any]] = {}
        for r in sorted(self.runs.values(), key=lambda r: r["requested_at_utc"]):
            out[r["job_type"]] = r
        return list(out.values())

    def list_runs(self, statuses):
        return [r for r in self.runs.values() if r["status"] in statuses]

    def latest_successful_run(self, job_type: str):
        rows = [r for r in self.runs.values() if r["job_type"] == job_type and r["status"] == "Succeeded"]
        return rows[-1] if rows else None

    # writes
    def create_run(self, *, run_id, job_type, trigger, status, parameters, requested_at, started_at, worker_host):
        self._seq += 1
        self.runs[run_id] = {"id": run_id, "job_type": job_type, "trigger": trigger, "status": status,
                             "parameters": parameters, "requested_at_utc": requested_at, "started_at_utc": started_at,
                             "completed_at_utc": None, "result_summary": None, "error_message": None,
                             "records_processed": None, "model_version_id": None, "worker_host": worker_host,
                             "_seq": self._seq}

    def queue_run(self, job_type: str, parameters: dict[str, Any] | None = None, requested_at: Any = None) -> str:
        run_id = str(uuid.uuid4())
        self.create_run(run_id=run_id, job_type=job_type, trigger="Api", status="Queued", parameters=parameters or {},
                        requested_at=requested_at or pd.Timestamp.now(tz="UTC"), started_at=None, worker_host=None)
        return run_id

    def update_run(self, run_id, *, status, completed_at, result_summary, error_message, records_processed,
                   model_version_id):
        r = self.runs[run_id]
        r.update(status=status, completed_at_utc=completed_at, result_summary=result_summary,
                 error_message=error_message, records_processed=records_processed)
        if model_version_id:
            r["model_version_id"] = model_version_id

    def claim_next_queued_run(self, job_types, worker_host, now):
        queued = sorted((r for r in self.runs.values() if r["status"] == "Queued" and r["job_type"] in job_types),
                        key=lambda r: (r["requested_at_utc"], r["_seq"]))
        if not queued:
            return None
        r = queued[0]
        r.update(status="Running", started_at_utc=now, worker_host=worker_host)
        return {"id": r["id"], "job_type": r["job_type"], "parameters": r["parameters"], "trigger": r["trigger"]}

    def insert_model_version(self, row):
        self.models.append(dict(row))

    def insert_sensor_forecasts(self, rows):
        store = self.__dict__.setdefault('sensor_forecasts', {})
        before = len(store)
        for r in rows:
            store.setdefault((r['aircraft_id'], r['model_version_id'], r['horizon'], str(r['data_as_of_utc'])), dict(r))
        return len(store) - before

    def mark_model_registered(self, model_version_id, registered_at):
        m = self.get_model_version(model_version_id)
        if m is None or m["status"] != "Trained":
            return False
        m.update(status="Registered", registered_at_utc=registered_at)
        return True

    def insert_anomaly_results(self, rows):
        existing = {(r["telemetry_record_id"], r["model_version_id"]) for r in self.anomalies}
        n = 0
        for r in rows:
            key = (r["telemetry_record_id"], r["model_version_id"])
            if key not in existing:
                existing.add(key)
                self.anomalies.append(dict(r))
                n += 1
        return n

    RISK_KEY = ("aircraft_id", "basis", "model_version_id", "feature_cutoff_utc", "horizon_days")

    def insert_risk_predictions(self, rows):
        """Mirrors the unique index + ON CONFLICT DO NOTHING; returns the number actually inserted."""
        existing = {tuple(p[k] for k in self.RISK_KEY) for p in self.predictions}
        n = 0
        for r in rows:
            assert r["probability"] is None or r["probability_validated"], "CHECK constraint violated"
            assert r["prediction_state"] in ("Scored", "InsufficientTelemetry", "MissingFeatures"), "CHECK violated"
            assert r["feature_cutoff_utc"] is not None, "NOT NULL violated"
            key = tuple(r[k] for k in self.RISK_KEY)
            if key not in existing:
                existing.add(key)
                self.predictions.append(dict(r))
                n += 1
        return n

    # test helper
    def activate(self, model_id: str) -> None:
        for m in self.models:
            if m["id"] == model_id:
                m["status"] = "Active"


DB_ENV_VARS = ("AFP_ML_READER_DSN", "AFP_ML_WRITER_DSN", "DB_HOST", "DB_PORT", "DB_NAME", "DB_FORBIDDEN_NAMES",
               "ML_READER_USER", "ML_READER_PASSWORD", "ETL_WRITER_USER", "ETL_WRITER_PASSWORD")


@pytest.fixture(autouse=True)
def _no_real_database_config(monkeypatch):
    """Tests never connect to PostgreSQL: ignore the repository-root .env and any DB variables."""
    import afp_ml.cli

    monkeypatch.setattr(afp_ml.cli, "root_env_file", lambda: None)
    for name in DB_ENV_VARS:
        monkeypatch.delenv(name, raising=False)


@pytest.fixture
def settings(tmp_path) -> Settings:
    return Settings(model_dir=tmp_path / "models", if_n_estimators=50, persist_telemetry_anomalies=True)


def make_services(repo: FakeRepository, settings: Settings, now: dt.datetime | None = None) -> Services:
    clock_value = now or dt.datetime(2026, 1, 4, tzinfo=dt.UTC)
    return Services(settings=settings, repo=repo, store=ArtifactStore(settings.model_dir), clock=lambda: clock_value,
                    host="test-host")


def failure_settings(base: Settings) -> Settings:
    return replace(base, prediction_horizon_days=14, min_positive_labels=10, min_aircraft_with_positives=3,
                   min_label_months=3, min_negative_examples=50, min_split_positives=3,
                   calibration_min_test_positives=5, label_definition_confirmed=True)
