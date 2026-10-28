"""Configuration loaded from environment variables (optionally seeded from a ``.env`` file).

Credentials are never hard-coded.  Database DSNs come from ``AFP_ML_READER_DSN`` /
``AFP_ML_WRITER_DSN`` (highest precedence) or, when those are unset, are composed from the shared
``DB_HOST`` / ``DB_PORT`` / ``DB_NAME`` and the role credentials ``ML_READER_USER`` /
``ML_READER_PASSWORD`` (reader) and ``ETL_WRITER_USER`` / ``ETL_WRITER_PASSWORD`` (writer).  Every DSN
must point at the configured ``DB_NAME``; protected databases (``aircraft_app`` and
``DB_FORBIDDEN_NAMES``) are refused.  A tiny built-in ``.env`` parser is provided so that
python-dotenv is not required; real environment variables always win over values in the files.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field, fields
from pathlib import Path
from typing import Iterable, Mapping, MutableMapping
from urllib.parse import quote

DEFAULT_DB_HOST = "localhost"
DEFAULT_DB_PORT = 5432
DEFAULT_DB_NAME = "aircraft_predict_db"
ENVIRONMENT_OPERATIONAL = "operational"
ENVIRONMENT_DEVELOPMENT = "development"
# Databases the ML subsystem must never connect to (extended with DB_FORBIDDEN_NAMES).
PROTECTED_DATABASES = frozenset({"aircraft_app"})


class ConfigError(Exception):
    """Raised for missing or invalid configuration (reported to the user without a traceback)."""


def parse_env_file(path: str | os.PathLike[str]) -> dict[str, str]:
    """Parse a minimal ``KEY=VALUE`` file.

    Supports blank lines, ``#`` comments, an optional ``export`` prefix, single/double quoted
    values and trailing `` # comment`` on unquoted values.  No variable interpolation.
    """
    result: dict[str, str] = {}
    text = Path(path).read_text(encoding="utf-8-sig")
    for line_no, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[len("export "):].lstrip()
        if "=" not in line:
            raise ConfigError(f"{path}:{line_no}: expected KEY=VALUE")
        key, value = line.split("=", 1)
        key = key.strip()
        if not key.replace("_", "").isalnum():
            raise ConfigError(f"{path}:{line_no}: invalid variable name {key!r}")
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        else:
            hash_pos = value.find(" #")
            if hash_pos >= 0:
                value = value[:hash_pos].rstrip()
        result[key] = value
    return result


def load_env_file(path: str | os.PathLike[str], environ: MutableMapping[str, str] | None = None) -> int:
    """Load ``path`` into ``environ`` without overriding existing variables. Returns count set."""
    environ = os.environ if environ is None else environ
    count = 0
    for key, value in parse_env_file(path).items():
        if key not in environ:
            environ[key] = value
            count += 1
    return count


def default_env_file() -> Path | None:
    """``AFP_ML_ENV_FILE`` if set, else ``ml/.env`` next to the package, else ``./.env``."""
    explicit = os.environ.get("AFP_ML_ENV_FILE")
    if explicit:
        return Path(explicit)
    for candidate in (Path(__file__).resolve().parent.parent / ".env", Path.cwd() / ".env"):
        if candidate.is_file():
            return candidate
    return None


def root_env_file() -> Path | None:
    """The repository-root ``.env`` (parent of ``ml/``) if it exists; loaded after the ML file."""
    candidate = Path(__file__).resolve().parent.parent.parent / ".env"
    return candidate if candidate.is_file() else None


def load_env_files(paths: Iterable[str | os.PathLike[str] | None],
                   environ: MutableMapping[str, str] | None = None) -> list[Path]:
    """Load several env files in priority order (earlier files win; nothing overrides ``environ``)."""
    loaded: list[Path] = []
    seen: set[Path] = set()
    for path in paths:
        if not path:
            continue
        resolved = Path(path).resolve()
        if resolved in seen:
            continue
        seen.add(resolved)
        load_env_file(path, environ)
        loaded.append(Path(path))
    return loaded


def default_model_dir(environ: Mapping[str, str] | None = None) -> Path:
    environ = os.environ if environ is None else environ
    local = environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "AircraftFailurePrediction" / "models"
    return Path.home() / ".local" / "share" / "AircraftFailurePrediction" / "models"


def _get(environ: Mapping[str, str], name: str) -> str | None:
    value = environ.get(name)
    if value is None:
        return None
    value = value.strip()
    return value or None


def _int(environ: Mapping[str, str], name: str, default: int, minimum: int | None = None) -> int:
    raw = _get(environ, name)
    if raw is None:
        return default
    try:
        value = int(raw)
    except ValueError as exc:
        raise ConfigError(f"{name} must be an integer (got {raw!r})") from exc
    if minimum is not None and value < minimum:
        raise ConfigError(f"{name} must be >= {minimum} (got {value})")
    return value


def _float(environ: Mapping[str, str], name: str, default: float, lo: float | None = None,
           hi: float | None = None) -> float:
    raw = _get(environ, name)
    if raw is None:
        return default
    try:
        value = float(raw)
    except ValueError as exc:
        raise ConfigError(f"{name} must be a number (got {raw!r})") from exc
    if (lo is not None and value < lo) or (hi is not None and value > hi):
        raise ConfigError(f"{name} must be within [{lo}, {hi}] (got {value})")
    return value


def _list(environ: Mapping[str, str], name: str) -> tuple[str, ...]:
    """Comma-separated list -> tuple of non-empty, stripped items (empty tuple when unset)."""
    raw = _get(environ, name)
    if raw is None:
        return ()
    return tuple(item.strip() for item in raw.split(",") if item.strip())


def _compose_dsn(environ: Mapping[str, str], user_var: str, password_var: str) -> str | None:
    """``postgresql://`` DSN from DB_HOST/DB_PORT/DB_NAME and a role's user/password (None without user)."""
    user = _get(environ, user_var)
    if user is None:
        return None
    host = _get(environ, "DB_HOST") or DEFAULT_DB_HOST
    port = _int(environ, "DB_PORT", DEFAULT_DB_PORT, 1)
    dbname = _get(environ, "DB_NAME") or DEFAULT_DB_NAME
    password = environ.get(password_var)
    auth = quote(user, safe="") + (":" + quote(password, safe="") if password else "")
    return f"postgresql://{auth}@{host}:{port}/{quote(dbname, safe='')}"


def dsn_database_name(dsn: str) -> str | None:
    """Database name contained in a URL or key=value DSN (None when absent)."""
    from psycopg.conninfo import conninfo_to_dict

    try:
        return conninfo_to_dict(dsn).get("dbname") or None
    except Exception as exc:  # the message is not echoed: it could contain parts of the DSN
        raise ConfigError(f"a database DSN could not be parsed ({type(exc).__name__})") from None


def check_database_name(name: str | None, expected: str, forbidden: Iterable[str], source: str) -> None:
    """Refuse protected databases and any database other than the configured DB_NAME."""
    blocked = {n.lower() for n in PROTECTED_DATABASES} | {n.lower() for n in forbidden}
    if name is None:
        raise ConfigError(f"{source} does not name a database; it must target DB_NAME={expected!r}.")
    if name.lower() in blocked:
        raise ConfigError(f"{source} targets the protected database {name!r}; the ML subsystem must never use it "
                          f"(configured DB_NAME={expected!r}).")
    if name != expected:
        raise ConfigError(f"{source} targets database {name!r} but DB_NAME is {expected!r}; refusing to continue.")


def _choice(env: Mapping[str, str], name: str, allowed: tuple[str, ...], default: str) -> str:
    value = (_get(env, name) or default).strip().lower()
    if value not in allowed:
        raise ConfigError(f"{name} must be one of {allowed} (got {value!r})")
    return value


def _bool(environ: Mapping[str, str], name: str, default: bool) -> bool:
    raw = _get(environ, name)
    if raw is None:
        return default
    lowered = raw.lower()
    if lowered in ("1", "true", "yes", "on"):
        return True
    if lowered in ("0", "false", "no", "off"):
        return False
    raise ConfigError(f"{name} must be true/false (got {raw!r})")


@dataclass(frozen=True)
class Settings:
    reader_dsn: str | None = None
    writer_dsn: str | None = None
    # "operational" (default) or "development" (the isolated synthetic environment, AFP_ENVIRONMENT).
    environment: str = "operational"
    db_name: str = DEFAULT_DB_NAME
    db_forbidden_names: tuple[str, ...] = ()
    model_dir: Path = Path("models")
    # Durable model copies in MinIO (AFP_ARTIFACT_*); bucket None = local directory only.
    artifact_endpoint: str | None = None
    artifact_bucket: str | None = None
    artifact_prefix: str = "models"
    artifact_access_key: str | None = None
    artifact_secret_key: str | None = field(default=None, repr=False)
    artifact_forbidden_buckets: tuple[str, ...] = ()
    db_connect_timeout: int = 10

    prediction_horizon_days: int = 30
    min_positive_labels: int = 30
    min_aircraft_with_positives: int = 5
    min_label_months: int = 6
    min_negative_examples: int = 200
    min_split_positives: int = 5
    label_require_confirmed_failure: bool = False
    label_definition_confirmed: bool = False
    failure_codes: tuple[str, ...] = ()
    calibration_max_ece: float = 0.10
    calibration_min_test_positives: int = 10
    # Sensor-forecast validation (afp_ml.forecast): model MAE / best naive MAE must be <= this ratio, the out-of-sample
    # 80 % interval coverage must lie in [low, high], with at least this many evaluation origins per horizon.
    forecast_max_mae_ratio: float = 0.95
    forecast_coverage_low: float = 0.70
    forecast_coverage_high: float = 0.90
    forecast_min_eval_origins: int = 5
    # "holdout": one chronological train/validation/test split. "rolling_origin": expanding-window
    # cross-validation (AFP_FAILURE_EVALUATION); folds fixed in advance (AFP_FAILURE_CV_FOLDS, sklearn's default 5).
    failure_evaluation: str = "holdout"
    failure_cv_folds: int = 5

    if_contamination: float = 0.01
    if_n_estimators: int = 200
    feature_window: int = 6
    scoring_context_hours: int = 24
    random_seed: int = 42

    # 2026-10-28 (user decision): anomalies come from the 30-day forecast only, so Isolation Forest flags on recorded
    # telemetry are scored but not persisted unless this is switched on.
    persist_telemetry_anomalies: bool = False

    risk_window_days: int = 7
    risk_recency_hours: int = 48
    risk_elevated_anomaly_count: int = 3
    risk_high_anomaly_count: int = 10
    risk_elevated_rate_multiplier: float = 2.0
    risk_high_rate_multiplier: float = 5.0
    risk_min_readings: int = 36

    audit_gap_minutes: float = 60.0
    worker_poll_seconds: float = 15.0
    log_level: str = "INFO"

    @classmethod
    def from_env(cls, environ: Mapping[str, str] | None = None) -> "Settings":
        env = os.environ if environ is None else environ
        model_dir_raw = _get(env, "AFP_MODEL_DIR")
        model_dir = Path(os.path.expandvars(model_dir_raw)) if model_dir_raw else default_model_dir(env)
        db_name = _get(env, "DB_NAME") or DEFAULT_DB_NAME
        forbidden = tuple(n.lower() for n in _list(env, "DB_FORBIDDEN_NAMES"))
        check_database_name(db_name, db_name, forbidden, "DB_NAME")
        environment = (_get(env, "AFP_ENVIRONMENT") or ENVIRONMENT_OPERATIONAL).lower()
        if environment not in (ENVIRONMENT_OPERATIONAL, ENVIRONMENT_DEVELOPMENT):
            raise ConfigError(f"AFP_ENVIRONMENT must be {ENVIRONMENT_OPERATIONAL!r} or {ENVIRONMENT_DEVELOPMENT!r} "
                              f"(got {environment!r})")
        if environment == ENVIRONMENT_DEVELOPMENT and db_name.lower() == DEFAULT_DB_NAME:
            # Development jobs (e.g. scoring an unapproved candidate) must never run against operational data.
            raise ConfigError(f"AFP_ENVIRONMENT=development cannot target the operational database {db_name!r}.")
        artifact_bucket = _get(env, "AFP_ARTIFACT_BUCKET")
        artifact_forbidden = tuple(b.lower() for b in _list(env, "AFP_ARTIFACT_FORBIDDEN_BUCKETS"))
        artifact_keys = {k: _get(env, k) for k in ("AFP_ARTIFACT_ENDPOINT", "AFP_ARTIFACT_ACCESS_KEY",
                                                    "AFP_ARTIFACT_SECRET_KEY")}
        if artifact_bucket:
            if artifact_bucket.lower() in artifact_forbidden:
                # e.g. the development config inheriting the operational bucket from the root .env.
                raise ConfigError(f"AFP_ARTIFACT_BUCKET {artifact_bucket!r} is forbidden for this environment "
                                  f"(AFP_ARTIFACT_FORBIDDEN_BUCKETS).")
            missing = [k for k, v in artifact_keys.items() if not v]
            if missing:
                raise ConfigError(f"AFP_ARTIFACT_BUCKET is set but {', '.join(missing)} is not.")
        reader_dsn = _get(env, "AFP_ML_READER_DSN") or _compose_dsn(env, "ML_READER_USER", "ML_READER_PASSWORD")
        writer_dsn = _get(env, "AFP_ML_WRITER_DSN") or _compose_dsn(env, "ETL_WRITER_USER", "ETL_WRITER_PASSWORD")
        for label, dsn in (("The reader DSN", reader_dsn), ("The writer DSN", writer_dsn)):
            if dsn:
                check_database_name(dsn_database_name(dsn), db_name, forbidden, label)
        return cls(
            reader_dsn=reader_dsn,
            writer_dsn=writer_dsn,
            environment=environment,
            db_name=db_name,
            db_forbidden_names=forbidden,
            model_dir=model_dir,
            artifact_endpoint=artifact_keys["AFP_ARTIFACT_ENDPOINT"],
            artifact_bucket=artifact_bucket or None,
            artifact_prefix=_get(env, "AFP_ARTIFACT_PREFIX") or "models",
            artifact_access_key=artifact_keys["AFP_ARTIFACT_ACCESS_KEY"],
            artifact_secret_key=artifact_keys["AFP_ARTIFACT_SECRET_KEY"],
            artifact_forbidden_buckets=artifact_forbidden,
            db_connect_timeout=_int(env, "AFP_DB_CONNECT_TIMEOUT", 10, 1),
            prediction_horizon_days=_int(env, "AFP_PREDICTION_HORIZON_DAYS", 30, 1),
            min_positive_labels=_int(env, "AFP_READINESS_MIN_POSITIVE_LABELS", 30, 1),
            min_aircraft_with_positives=_int(env, "AFP_READINESS_MIN_AIRCRAFT_WITH_POSITIVES", 5, 1),
            min_label_months=_int(env, "AFP_READINESS_MIN_LABEL_MONTHS", 6, 1),
            min_negative_examples=_int(env, "AFP_READINESS_MIN_NEGATIVE_EXAMPLES", 200, 1),
            min_split_positives=_int(env, "AFP_READINESS_MIN_SPLIT_POSITIVES", 5, 1),
            label_require_confirmed_failure=_bool(env, "AFP_LABEL_REQUIRE_CONFIRMED_FAILURE", False),
            label_definition_confirmed=_bool(env, "ML_LABEL_DEFINITION_CONFIRMED", False),
            failure_codes=_list(env, "ML_FAILURE_CODES"),
            calibration_max_ece=_float(env, "AFP_CALIBRATION_MAX_ECE", 0.10, 0.0, 1.0),
            calibration_min_test_positives=_int(env, "AFP_CALIBRATION_MIN_TEST_POSITIVES", 10, 1),
            forecast_max_mae_ratio=_float(env, "AFP_FORECAST_MAX_MAE_RATIO", 0.95, 0.0, 1.0),
            forecast_coverage_low=_float(env, "AFP_FORECAST_COVERAGE_LOW", 0.70, 0.0, 1.0),
            forecast_coverage_high=_float(env, "AFP_FORECAST_COVERAGE_HIGH", 0.90, 0.0, 1.0),
            forecast_min_eval_origins=_int(env, "AFP_FORECAST_MIN_EVAL_ORIGINS", 5, 1),
            failure_evaluation=_choice(env, "AFP_FAILURE_EVALUATION", ("holdout", "rolling_origin"), "holdout"),
            failure_cv_folds=_int(env, "AFP_FAILURE_CV_FOLDS", 5, 2),
            if_contamination=_float(env, "AFP_IF_CONTAMINATION", 0.01, 0.0001, 0.5),
            if_n_estimators=_int(env, "AFP_IF_N_ESTIMATORS", 200, 10),
            feature_window=_int(env, "AFP_FEATURE_WINDOW", 6, 2),
            scoring_context_hours=_int(env, "AFP_SCORING_CONTEXT_HOURS", 24, 1),
            random_seed=_int(env, "AFP_RANDOM_SEED", 42, 0),
            persist_telemetry_anomalies=_bool(env, "AFP_PERSIST_TELEMETRY_ANOMALIES", False),
            risk_window_days=_int(env, "AFP_RISK_WINDOW_DAYS", 7, 1),
            risk_recency_hours=_int(env, "AFP_RISK_RECENCY_HOURS", 48, 1),
            risk_elevated_anomaly_count=_int(env, "AFP_RISK_ELEVATED_ANOMALY_COUNT", 3, 1),
            risk_high_anomaly_count=_int(env, "AFP_RISK_HIGH_ANOMALY_COUNT", 10, 1),
            risk_elevated_rate_multiplier=_float(env, "AFP_RISK_ELEVATED_RATE_MULTIPLIER", 2.0, 1.0),
            risk_high_rate_multiplier=_float(env, "AFP_RISK_HIGH_RATE_MULTIPLIER", 5.0, 1.0),
            risk_min_readings=_int(env, "AFP_RISK_MIN_READINGS", 36, 1),
            audit_gap_minutes=_float(env, "AFP_AUDIT_GAP_MINUTES", 60.0, 1.0),
            worker_poll_seconds=_float(env, "AFP_WORKER_POLL_SECONDS", 15.0, 0.5),
            log_level=(_get(env, "AFP_LOG_LEVEL") or "INFO").upper(),
        )

    def require_reader_dsn(self) -> str:
        """Reader DSN; falls back to the writer DSN (the writer role is a member of the reader role)."""
        dsn = self.reader_dsn or self.writer_dsn
        if not dsn:
            raise ConfigError(
                "AFP_ML_READER_DSN is not set and ML_READER_USER is not set to compose one. Set ML_READER_USER/"
                "ML_READER_PASSWORD (with DB_HOST/DB_PORT/DB_NAME) or AFP_ML_READER_DSN (e.g. postgresql://"
                "<reader>:<password>@localhost:5432/aircraft_predict_db) in the environment, ml/.env or the "
                "repository .env - see ml/.env.example."
            )
        return dsn

    def require_writer_dsn(self) -> str:
        if not self.writer_dsn:
            raise ConfigError(
                "AFP_ML_WRITER_DSN is not set and ETL_WRITER_USER is not set to compose one. This command records a "
                "pipeline_run row and needs the writer role: set ETL_WRITER_USER/ETL_WRITER_PASSWORD (with DB_HOST/"
                "DB_PORT/DB_NAME) or AFP_ML_WRITER_DSN (e.g. postgresql://<writer>:<password>@localhost:5432/"
                "aircraft_predict_db) - see ml/.env.example."
            )
        return self.writer_dsn

    def public_dict(self) -> dict[str, object]:
        """Settings safe to store/print (DSNs and the artifact secret are reduced to presence flags)."""
        out: dict[str, object] = {}
        for f in fields(self):
            value = getattr(self, f.name)
            if f.name.endswith("_dsn"):
                out[f.name + "_configured"] = bool(value)
            elif f.name == "artifact_secret_key":
                out["artifact_credentials_configured"] = bool(value)
            elif isinstance(value, Path):
                out[f.name] = str(value)
            else:
                out[f.name] = value
        return out

    def readiness_thresholds(self) -> dict[str, object]:
        return {
            "min_positive_labels": self.min_positive_labels,
            "min_aircraft_with_positives": self.min_aircraft_with_positives,
            "min_label_months": self.min_label_months,
            "min_negative_examples": self.min_negative_examples,
            "min_split_positives": self.min_split_positives,
            "label_require_confirmed_failure": self.label_require_confirmed_failure,
            "label_definition_confirmed": self.label_definition_confirmed,
        }
