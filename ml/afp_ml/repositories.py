"""Repository layer: the ONLY module that issues SQL.

Business logic depends on the :class:`Repository` protocol and on pandas DataFrames, so it can be
tested with in-memory fakes.  Every statement is parameterised.  Tables/columns follow
``docs/DATABASE.md``.  The ML roles may only:

* SELECT  aircraft, telemetry_record, maintenance_log, model_version, pipeline_run,
          aircraft_anomaly_result, aircraft_risk_prediction, risk_status
* INSERT  aircraft_anomaly_result, aircraft_risk_prediction, model_version, pipeline_run
* UPDATE  pipeline_run (run status fields) and model_version (status, registered_at_utc, metrics, notes)

Nothing here touches users, tokens, audits, work orders, inspection flags or aircraft status.
"""

from __future__ import annotations

import datetime as dt
import logging
from typing import Any, Iterable, Protocol, Sequence

import pandas as pd

from .config import DEFAULT_DB_NAME
from .features import BreachLimits
from .db import DatabaseError, connect
from .util import to_jsonable

log = logging.getLogger(__name__)

TELEMETRY_COLUMNS = [
    "id", "aircraft_id", "sensor_id", "recorded_at_utc", "temperature_c", "pressure_kpa", "vibration_index",
    "quality_status", "quality_flags", "data_source",
]
MAINTENANCE_COLUMNS = [
    "id", "aircraft_id", "component_id", "event_type", "occurred_at_utc", "actual_failure_confirmed",
    "is_verified", "source",
]
MODEL_VERSION_COLUMNS = [
    "id", "model_name", "version", "model_type", "algorithm", "status", "artifact_path", "artifact_sha256",
    "artifact_uri", "feature_names", "hyperparameters", "metrics", "training_data_from_utc", "training_data_to_utc",
    "training_row_count", "prediction_horizon_days", "limitations", "notes", "created_at_utc",
    "registered_at_utc", "approved_at_utc", "activated_at_utc", "retired_at_utc", "training_run_id",
]
PIPELINE_RUN_COLUMNS = [
    "id", "job_type", "status", "trigger", "requested_at_utc", "started_at_utc", "completed_at_utc",
    "parameters", "result_summary", "error_message", "model_version_id", "records_processed", "worker_host",
]


def empty_telemetry_frame() -> pd.DataFrame:
    return normalise_telemetry(pd.DataFrame(columns=TELEMETRY_COLUMNS))


def normalise_telemetry(df: pd.DataFrame) -> pd.DataFrame:
    """Coerce telemetry columns to the dtypes the business logic expects."""
    out = df.copy()
    for col in TELEMETRY_COLUMNS:
        if col not in out.columns:
            out[col] = None
    out["id"] = pd.to_numeric(out["id"]).astype("int64")
    out["aircraft_id"] = out["aircraft_id"].astype(object).map(lambda v: None if v is None else str(v))
    out["sensor_id"] = out["sensor_id"].astype(object).where(out["sensor_id"].notna(), None)
    out["recorded_at_utc"] = pd.to_datetime(out["recorded_at_utc"], utc=True)
    out["temperature_c"] = pd.to_numeric(out["temperature_c"], errors="coerce").astype("float64")
    out["pressure_kpa"] = pd.to_numeric(out["pressure_kpa"], errors="coerce").astype("float64")
    out["vibration_index"] = pd.to_numeric(out["vibration_index"], errors="coerce").astype("float64")
    return out[TELEMETRY_COLUMNS]


def normalise_maintenance(df: pd.DataFrame) -> pd.DataFrame:
    out = df.copy()
    for col in MAINTENANCE_COLUMNS:
        if col not in out.columns:
            out[col] = None
    out["occurred_at_utc"] = pd.to_datetime(out["occurred_at_utc"], utc=True)
    out["aircraft_id"] = out["aircraft_id"].astype(object).map(lambda v: None if v is None else str(v))
    return out[MAINTENANCE_COLUMNS]


class Repository(Protocol):
    # ---- reads -----------------------------------------------------------------------------
    def fetch_aircraft(self) -> pd.DataFrame: ...
    def fetch_telemetry(self, *, min_id_exclusive: int | None = None, from_utc: dt.datetime | None = None,
                        to_utc: dt.datetime | None = None, aircraft_ids: Sequence[str] | None = None,
                        quality_statuses: Sequence[str] | None = None) -> pd.DataFrame: ...
    def fetch_maintenance_log(self) -> pd.DataFrame: ...
    def fetch_breach_limits(self) -> BreachLimits: ...
    def fetch_max_source_record_number(self, prefix: str) -> int | None: ...
    def fetch_source_records(self, ids: Sequence[str], data_source: str) -> list[dict[str, Any]]: ...
    def fetch_last_telemetry_per_aircraft(self) -> pd.DataFrame: ...
    def fetch_anomaly_counts(self, model_version_id: str, since_utc: dt.datetime) -> pd.DataFrame: ...
    def fetch_forecast_conditions(self) -> dict[str, set[str]]: ...
    def fetch_telemetry_counts(self, since_utc: dt.datetime, quality_statuses: Sequence[str]) -> pd.DataFrame: ...
    def get_model_version(self, model_version_id: str) -> dict[str, Any] | None: ...
    def list_model_versions(self, model_type: str | None = None,
                            statuses: Sequence[str] | None = None) -> list[dict[str, Any]]: ...
    def get_active_model(self, model_type: str) -> dict[str, Any] | None: ...
    def get_score_watermark(self, model_version_id: str) -> int | None: ...
    def latest_runs_by_job_type(self) -> list[dict[str, Any]]: ...
    def list_runs(self, statuses: Sequence[str]) -> list[dict[str, Any]]: ...
    def latest_successful_run(self, job_type: str) -> dict[str, Any] | None: ...

    # ---- writes ----------------------------------------------------------------------------
    def create_run(self, *, run_id: str, job_type: str, trigger: str, status: str, parameters: dict[str, Any],
                   requested_at: dt.datetime, started_at: dt.datetime | None, worker_host: str) -> None: ...
    def update_run(self, run_id: str, *, status: str, completed_at: dt.datetime | None,
                   result_summary: dict[str, Any] | None, error_message: str | None,
                   records_processed: int | None, model_version_id: str | None) -> None: ...
    def claim_next_queued_run(self, job_types: Sequence[str], worker_host: str,
                              now: dt.datetime) -> dict[str, Any] | None: ...
    def insert_model_version(self, row: dict[str, Any]) -> None: ...
    def mark_model_registered(self, model_version_id: str, registered_at: dt.datetime) -> bool: ...
    def set_artifact_uri(self, model_version_id: str, artifact_uri: str) -> None: ...
    def insert_anomaly_results(self, rows: list[dict[str, Any]]) -> int: ...
    def insert_risk_predictions(self, rows: list[dict[str, Any]]) -> int: ...
    def insert_sensor_forecasts(self, rows: list[dict[str, Any]]) -> int: ...


def _jsonb(value: Any) -> Any:
    from psycopg.types.json import Jsonb

    return None if value is None else Jsonb(to_jsonable(value))


class PostgresRepository:
    """psycopg 3 implementation. Reads use the reader DSN; writes use the writer DSN."""

    def __init__(self, reader_dsn: str | None, writer_dsn: str | None, connect_timeout: int = 10, *,
                 expected_db_name: str = DEFAULT_DB_NAME) -> None:
        self._reader_dsn = reader_dsn or writer_dsn
        self._writer_dsn = writer_dsn
        self._timeout = connect_timeout
        self._expected_db = expected_db_name
        self._reader: Any = None
        self._writer: Any = None

    # ---- connections -----------------------------------------------------------------------
    def _r(self) -> Any:
        if self._reader is None:
            if not self._reader_dsn:
                raise DatabaseError("No reader DSN configured (AFP_ML_READER_DSN).")
            self._reader = connect(self._reader_dsn, read_only=True, expected_db_name=self._expected_db,
                                   connect_timeout=self._timeout)
        return self._reader

    def _w(self) -> Any:
        if self._writer is None:
            if not self._writer_dsn:
                raise DatabaseError("No writer DSN configured (AFP_ML_WRITER_DSN).")
            self._writer = connect(self._writer_dsn, read_only=False, expected_db_name=self._expected_db,
                                   connect_timeout=self._timeout)
        return self._writer

    def close(self) -> None:
        for conn in (self._reader, self._writer):
            if conn is not None:
                try:
                    conn.close()
                except Exception:  # pragma: no cover
                    pass
        self._reader = self._writer = None

    def _query(self, sql: str, params: Any = None, *, writer: bool = False) -> list[dict[str, Any]]:
        from psycopg.rows import dict_row

        conn = self._w() if writer else self._r()
        with conn.cursor(row_factory=dict_row) as cur:
            cur.execute(sql, params)
            return list(cur.fetchall())

    # ---- reads -----------------------------------------------------------------------------
    def fetch_aircraft(self) -> pd.DataFrame:
        rows = self._query("SELECT id::text AS aircraft_id, registration_number FROM aircraft ORDER BY registration_number")
        return pd.DataFrame(rows, columns=["aircraft_id", "registration_number"])

    def fetch_telemetry(self, *, min_id_exclusive: int | None = None, from_utc: dt.datetime | None = None,
                        to_utc: dt.datetime | None = None, aircraft_ids: Sequence[str] | None = None,
                        quality_statuses: Sequence[str] | None = None) -> pd.DataFrame:
        sql = """
            SELECT t.id, t.aircraft_id::text AS aircraft_id, t.sensor_id, t.recorded_at_utc,
                   t.temperature_c::double precision AS temperature_c,
                   t.pressure_kpa::double precision AS pressure_kpa,
                   t.vibration_index::double precision AS vibration_index,
                   t.quality_status, t.quality_flags, t.data_source
              FROM telemetry_record t
             WHERE (%(min_id)s::bigint IS NULL OR t.id > %(min_id)s::bigint)
               AND (%(from_utc)s::timestamptz IS NULL OR t.recorded_at_utc >= %(from_utc)s::timestamptz)
               AND (%(to_utc)s::timestamptz IS NULL OR t.recorded_at_utc <= %(to_utc)s::timestamptz)
               AND (%(aircraft_ids)s::uuid[] IS NULL OR t.aircraft_id = ANY(%(aircraft_ids)s::uuid[]))
               AND (%(statuses)s::text[] IS NULL OR t.quality_status = ANY(%(statuses)s::text[]))
             ORDER BY t.aircraft_id, t.sensor_id, t.recorded_at_utc, t.id
        """
        params = {
            "min_id": min_id_exclusive,
            "from_utc": from_utc,
            "to_utc": to_utc,
            "aircraft_ids": list(aircraft_ids) if aircraft_ids is not None else None,
            "statuses": list(quality_statuses) if quality_statuses is not None else None,
        }
        rows = self._query(sql, params)
        if not rows:
            return empty_telemetry_frame()
        return normalise_telemetry(pd.DataFrame(rows, columns=TELEMETRY_COLUMNS))

    def fetch_maintenance_log(self) -> pd.DataFrame:
        rows = self._query(
            """SELECT id::text AS id, aircraft_id::text AS aircraft_id, component_id::text AS component_id,
                      event_type, occurred_at_utc, actual_failure_confirmed, is_verified, source
                 FROM maintenance_log ORDER BY occurred_at_utc"""
        )
        return normalise_maintenance(pd.DataFrame(rows, columns=MAINTENANCE_COLUMNS))

    def fetch_breach_limits(self) -> BreachLimits:
        """BR-01 limits from the seeded ``health_threshold`` reference table (active rows only)."""
        rows = self._query(
            """SELECT code, threshold_value::double precision AS threshold_value
                 FROM health_threshold
                WHERE status_id = 1 AND code IN ('OilPressureLowPsi', 'EngineTemperatureHighC')"""
        )
        by_code = {r["code"]: r["threshold_value"] for r in rows}
        return BreachLimits(pressure_low_psi=by_code.get("OilPressureLowPsi"),
                            temperature_high_c=by_code.get("EngineTemperatureHighC"))

    def fetch_max_source_record_number(self, prefix: str) -> int | None:
        """Highest numeric suffix of any stored source_record_id with this prefix (e.g. TEL-126397 -> 126397)."""
        rows = self._query(
            """SELECT max(substring(source_record_id FROM char_length(%(p)s) + 1)::bigint) AS n
                 FROM telemetry_record
                WHERE source_record_id LIKE %(p)s || '%%'
                  AND substring(source_record_id FROM char_length(%(p)s) + 1) ~ '^[0-9]+$'""",
            {"p": prefix},
        )
        return int(rows[0]["n"]) if rows and rows[0]["n"] is not None else None

    def fetch_source_records(self, ids: Sequence[str], data_source: str) -> list[dict[str, Any]]:
        """How each source_record_id is actually stored: aircraft registration and reading time."""
        if not ids:
            return []
        return self._query(
            """SELECT t.source_record_id, a.registration_number, t.recorded_at_utc
                 FROM telemetry_record t JOIN aircraft a ON a.id = t.aircraft_id
                WHERE t.data_source = %s AND t.source_record_id = ANY(%s)""",
            (data_source, list(ids)),
        )

    def fetch_last_telemetry_per_aircraft(self) -> pd.DataFrame:
        rows = self._query(
            """SELECT a.id::text AS aircraft_id, a.registration_number,
                      (SELECT max(t.recorded_at_utc) FROM telemetry_record t WHERE t.aircraft_id = a.id)
                          AS last_telemetry_at_utc
                 FROM aircraft a ORDER BY a.registration_number"""
        )
        df = pd.DataFrame(rows, columns=["aircraft_id", "registration_number", "last_telemetry_at_utc"])
        df["last_telemetry_at_utc"] = pd.to_datetime(df["last_telemetry_at_utc"], utc=True)
        return df

    def fetch_anomaly_counts(self, model_version_id: str, since_utc: dt.datetime) -> pd.DataFrame:
        rows = self._query(
            """SELECT aircraft_id::text AS aircraft_id, severity, count(*)::bigint AS count
                 FROM aircraft_anomaly_result
                WHERE model_version_id = %s::uuid AND observed_at_utc >= %s
                  AND investigation_status <> 'Dismissed'
                GROUP BY aircraft_id, severity""",
            (model_version_id, since_utc),
        )
        return pd.DataFrame(rows, columns=["aircraft_id", "severity", "count"])

    def fetch_forecast_conditions(self) -> dict[str, set[str]]:
        """Open forecast incidents: aircraft_id -> measurements of the limits their rule codes name."""
        rows = self._query(
            """SELECT a.aircraft_id::text AS aircraft_id, t.measurement
                 FROM aircraft_anomaly_result a
                 CROSS JOIN LATERAL unnest(string_to_array(a.rule_codes, ',')) AS c(code)
                 JOIN health_threshold t ON t.code = btrim(c.code)
                WHERE a.source = 'ForecastModel'
                  AND a.investigation_status IN ('New', 'Acknowledged', 'Investigating')""",
            (),
        )
        out: dict[str, set[str]] = {}
        for r in rows:
            out.setdefault(str(r["aircraft_id"]), set()).add(str(r["measurement"]))
        return out

    def fetch_telemetry_counts(self, since_utc: dt.datetime, quality_statuses: Sequence[str]) -> pd.DataFrame:
        rows = self._query(
            """SELECT aircraft_id::text AS aircraft_id, count(*)::bigint AS readings
                 FROM telemetry_record
                WHERE recorded_at_utc >= %s AND quality_status = ANY(%s::text[])
                GROUP BY aircraft_id""",
            (since_utc, list(quality_statuses)),
        )
        return pd.DataFrame(rows, columns=["aircraft_id", "readings"])

    _MV_SELECT = (
        "SELECT id::text AS id, model_name, version, model_type, algorithm, status, artifact_path, artifact_sha256, "
        "feature_names, hyperparameters, metrics, training_data_from_utc, training_data_to_utc, training_row_count, "
        "prediction_horizon_days, limitations, notes, created_at_utc, registered_at_utc, approved_at_utc, "
        "activated_at_utc, retired_at_utc, training_run_id::text AS training_run_id FROM model_version"
    )

    def get_model_version(self, model_version_id: str) -> dict[str, Any] | None:
        rows = self._query(self._MV_SELECT + " WHERE id = %s::uuid", (model_version_id,))
        return rows[0] if rows else None

    def list_model_versions(self, model_type: str | None = None,
                            statuses: Sequence[str] | None = None) -> list[dict[str, Any]]:
        return self._query(
            self._MV_SELECT
            + " WHERE (%(mt)s::text IS NULL OR model_type = %(mt)s::text)"
              " AND (%(st)s::text[] IS NULL OR status = ANY(%(st)s::text[]))"
              " ORDER BY created_at_utc DESC",
            {"mt": model_type, "st": list(statuses) if statuses is not None else None},
        )

    def get_active_model(self, model_type: str) -> dict[str, Any] | None:
        rows = self.list_model_versions(model_type, ["Active"])
        return rows[0] if rows else None

    def get_score_watermark(self, model_version_id: str) -> int | None:
        rows = self._query(
            """SELECT max((result_summary->>'max_telemetry_record_id')::bigint) AS watermark
                 FROM pipeline_run
                WHERE job_type = 'Score' AND status = 'Succeeded'
                  AND (model_version_id = %(m)s::uuid OR result_summary->>'model_version_id' = %(m)s::text)""",
            {"m": model_version_id},
        )
        value = rows[0]["watermark"] if rows else None
        return int(value) if value is not None else None

    _RUN_SELECT = (
        "SELECT id::text AS id, job_type, status, trigger, requested_at_utc, started_at_utc, completed_at_utc, "
        "parameters, result_summary, error_message, model_version_id::text AS model_version_id, records_processed, "
        "worker_host FROM pipeline_run"
    )

    def latest_runs_by_job_type(self) -> list[dict[str, Any]]:
        return self._query(
            "SELECT DISTINCT ON (job_type) * FROM (" + self._RUN_SELECT + ") r "
            "ORDER BY job_type, requested_at_utc DESC"
        )

    def list_runs(self, statuses: Sequence[str]) -> list[dict[str, Any]]:
        return self._query(self._RUN_SELECT + " WHERE status = ANY(%s::text[]) ORDER BY requested_at_utc",
                           (list(statuses),))

    def latest_successful_run(self, job_type: str) -> dict[str, Any] | None:
        rows = self._query(
            self._RUN_SELECT + " WHERE job_type = %s AND status = 'Succeeded' "
                               "ORDER BY completed_at_utc DESC NULLS LAST LIMIT 1",
            (job_type,),
        )
        return rows[0] if rows else None

    # ---- writes ----------------------------------------------------------------------------
    def create_run(self, *, run_id: str, job_type: str, trigger: str, status: str, parameters: dict[str, Any],
                   requested_at: dt.datetime, started_at: dt.datetime | None, worker_host: str) -> None:
        with self._w().cursor() as cur:
            cur.execute(
                """INSERT INTO pipeline_run (id, job_type, status, trigger, requested_by_user_id, requested_at_utc,
                                             started_at_utc, parameters, worker_host)
                   VALUES (%s::uuid, %s, %s, %s, NULL, %s, %s, %s, %s)""",
                (run_id, job_type, status, trigger, requested_at, started_at, _jsonb(parameters or {}), worker_host),
            )

    def update_run(self, run_id: str, *, status: str, completed_at: dt.datetime | None,
                   result_summary: dict[str, Any] | None, error_message: str | None,
                   records_processed: int | None, model_version_id: str | None) -> None:
        with self._w().cursor() as cur:
            cur.execute(
                """UPDATE pipeline_run
                      SET status = %s, completed_at_utc = %s, result_summary = %s, error_message = %s,
                          records_processed = %s, model_version_id = COALESCE(%s::uuid, model_version_id)
                    WHERE id = %s::uuid""",
                (status, completed_at, _jsonb(result_summary), error_message, records_processed,
                 model_version_id, run_id),
            )

    def claim_next_queued_run(self, job_types: Sequence[str], worker_host: str,
                              now: dt.datetime) -> dict[str, Any] | None:
        from psycopg.rows import dict_row

        conn = self._w()
        with conn.transaction():
            with conn.cursor(row_factory=dict_row) as cur:
                cur.execute(
                    """SELECT id::text AS id, job_type, parameters, trigger, requested_at_utc
                         FROM pipeline_run
                        WHERE status = 'Queued' AND job_type = ANY(%s::text[])
                        ORDER BY requested_at_utc, id
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED""",
                    (list(job_types),),
                )
                row = cur.fetchone()
                if row is None:
                    return None
                cur.execute(
                    """UPDATE pipeline_run SET status = 'Running', started_at_utc = %s, worker_host = %s
                        WHERE id = %s::uuid AND status = 'Queued'""",
                    (now, worker_host, row["id"]),
                )
        return dict(row)

    def set_artifact_uri(self, model_version_id: str, artifact_uri: str) -> None:
        """Record the durable copy of an existing artifact (backfill). Never changes path or hash."""
        with self._w().cursor() as cur:
            cur.execute("UPDATE model_version SET artifact_uri = %s WHERE id = %s::uuid AND artifact_uri IS NULL",
                        (artifact_uri, model_version_id))

    def insert_model_version(self, row: dict[str, Any]) -> None:
        with self._w().cursor() as cur:
            cur.execute(
                """INSERT INTO model_version (id, model_name, version, model_type, algorithm, status, artifact_path,
                       artifact_sha256, artifact_uri, feature_names, hyperparameters, metrics, training_data_from_utc,
                       training_data_to_utc, training_row_count, prediction_horizon_days, limitations, notes,
                       created_at_utc, training_run_id)
                   VALUES (%(id)s::uuid, %(model_name)s, %(version)s, %(model_type)s, %(algorithm)s, %(status)s,
                       %(artifact_path)s, %(artifact_sha256)s, %(artifact_uri)s, %(feature_names)s, %(hyperparameters)s, %(metrics)s,
                       %(training_data_from_utc)s, %(training_data_to_utc)s, %(training_row_count)s,
                       %(prediction_horizon_days)s, %(limitations)s, %(notes)s, %(created_at_utc)s,
                       %(training_run_id)s::uuid)""",
                {
                    **row,
                    "artifact_uri": row.get("artifact_uri"),
                    "feature_names": _jsonb(row["feature_names"]),
                    "hyperparameters": _jsonb(row["hyperparameters"]),
                    "metrics": _jsonb(row["metrics"]),
                },
            )

    def insert_sensor_forecasts(self, rows: list[dict[str, Any]]) -> int:
        """One row per (aircraft, model, horizon, data_as_of); re-running for the same data inserts nothing."""
        inserted = 0
        with self._w().cursor() as cur:
            for r in rows:
                cur.execute(
                    """INSERT INTO sensor_forecast (id, status_id, aircraft_id, model_version_id, horizon, generated_at_utc,
                           data_as_of_utc, pipeline_run_id, points)
                       VALUES (%(id)s::uuid, 1, %(aircraft_id)s::uuid, %(model_version_id)s::uuid, %(horizon)s,
                           %(generated_at_utc)s, %(data_as_of_utc)s, %(pipeline_run_id)s::uuid, %(points)s)
                       ON CONFLICT (aircraft_id, model_version_id, horizon, data_as_of_utc) DO NOTHING""",
                    {**r, "points": _jsonb(r["points"])},
                )
                inserted += cur.rowcount
        return inserted

    def mark_model_registered(self, model_version_id: str, registered_at: dt.datetime) -> bool:
        with self._w().cursor() as cur:
            cur.execute(
                """UPDATE model_version SET status = 'Registered', registered_at_utc = %s
                    WHERE id = %s::uuid AND status = 'Trained'""",
                (registered_at, model_version_id),
            )
            return cur.rowcount == 1

    def insert_anomaly_results(self, rows: list[dict[str, Any]]) -> int:
        if not rows:
            return 0
        sql = """INSERT INTO aircraft_anomaly_result (aircraft_id, telemetry_record_id, sensor_id, observed_at_utc,
                     anomaly_score, is_anomaly, severity, model_version_id, pipeline_run_id, scored_at_utc,
                     feature_values, investigation_status, updated_at_utc)
                 VALUES (%(aircraft_id)s::uuid, %(telemetry_record_id)s, %(sensor_id)s, %(observed_at_utc)s,
                     %(anomaly_score)s, %(is_anomaly)s, %(severity)s, %(model_version_id)s::uuid,
                     %(pipeline_run_id)s::uuid, %(scored_at_utc)s, %(feature_values)s, %(investigation_status)s,
                     %(scored_at_utc)s)
                 ON CONFLICT (telemetry_record_id, model_version_id) DO NOTHING"""
        inserted = 0
        conn = self._w()
        with conn.transaction():
            with conn.cursor() as cur:
                for chunk in _chunks(rows, 1000):
                    payload = [{**r, "feature_values": _jsonb(r.get("feature_values"))} for r in chunk]
                    cur.executemany(sql, payload)
                    inserted += max(cur.rowcount, 0)
        return inserted

    def insert_risk_predictions(self, rows: list[dict[str, Any]]) -> int:
        """Insert risk rows; an existing (aircraft, basis, model, feature cutoff, horizon) key is skipped.

        Returns the number of rows actually inserted.  ``status_id`` is omitted (DB default 1 = Active).
        """
        if not rows:
            return 0
        sql = """INSERT INTO aircraft_risk_prediction (id, aircraft_id, component_id, prediction_timestamp_utc,
                     horizon_days, risk_category, basis, probability, probability_validated, feature_snapshot,
                     model_version_id, pipeline_run_id, approval_status, actual_outcome, prediction_state,
                     feature_cutoff_utc)
                 VALUES (%(id)s::uuid, %(aircraft_id)s::uuid, %(component_id)s, %(prediction_timestamp_utc)s,
                     %(horizon_days)s, %(risk_category)s, %(basis)s, %(probability)s, %(probability_validated)s,
                     %(feature_snapshot)s, %(model_version_id)s::uuid, %(pipeline_run_id)s::uuid,
                     %(approval_status)s, %(actual_outcome)s, %(prediction_state)s, %(feature_cutoff_utc)s)
                 ON CONFLICT (aircraft_id, basis, model_version_id, feature_cutoff_utc, horizon_days) DO NOTHING"""
        inserted = 0
        conn = self._w()
        with conn.transaction():
            with conn.cursor() as cur:
                for chunk in _chunks(rows, 1000):
                    payload = [{**r, "feature_snapshot": _jsonb(r.get("feature_snapshot"))} for r in chunk]
                    cur.executemany(sql, payload)
                    inserted += max(cur.rowcount, 0)
        return inserted


def _chunks(items: list[Any], size: int) -> Iterable[list[Any]]:
    for i in range(0, len(items), size):
        yield items[i:i + size]


__all__ = [
    "Repository", "PostgresRepository", "DatabaseError", "TELEMETRY_COLUMNS", "MAINTENANCE_COLUMNS",
    "normalise_telemetry", "normalise_maintenance", "empty_telemetry_frame",
]
