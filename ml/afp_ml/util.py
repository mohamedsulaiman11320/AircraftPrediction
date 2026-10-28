"""Small shared helpers: UTC clock, JSON-safe conversion."""

from __future__ import annotations

import datetime as dt
import math
import socket
import uuid
from decimal import Decimal
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd


def utc_now() -> dt.datetime:
    return dt.datetime.now(dt.UTC)


def new_uuid() -> str:
    return str(uuid.uuid4())


def worker_host() -> str:
    try:
        return socket.gethostname()[:200]
    except OSError:  # pragma: no cover - extremely unlikely
        return "unknown-host"


def version_string(now: dt.datetime) -> str:
    """Model version string ``yyyyMMddHHmmss`` (UTC)."""
    return now.astimezone(dt.UTC).strftime("%Y%m%d%H%M%S")


def iso(value: Any) -> str | None:
    if value is None:
        return None
    if isinstance(value, pd.Timestamp):
        if pd.isna(value):
            return None
        value = value.to_pydatetime()
    if isinstance(value, dt.datetime):
        if value.tzinfo is None:
            value = value.replace(tzinfo=dt.UTC)
        return value.astimezone(dt.UTC).isoformat().replace("+00:00", "Z")
    return str(value)


def to_jsonable(obj: Any) -> Any:
    """Recursively convert numpy/pandas/datetime/Decimal values; NaN/inf become ``None``."""
    if obj is None or isinstance(obj, (bool, str)):
        return obj
    if isinstance(obj, (np.bool_,)):
        return bool(obj)
    if isinstance(obj, (int, np.integer)):
        return int(obj)
    if isinstance(obj, (float, np.floating, Decimal)):
        f = float(obj)
        return f if math.isfinite(f) else None
    if isinstance(obj, (dt.datetime, pd.Timestamp)):
        return iso(obj)
    if isinstance(obj, dt.date):
        return obj.isoformat()
    if isinstance(obj, (dt.timedelta, pd.Timedelta)):
        return pd.Timedelta(obj).total_seconds()
    if isinstance(obj, (uuid.UUID, Path)):
        return str(obj)
    if isinstance(obj, dict):
        return {str(k): to_jsonable(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple, set, np.ndarray, pd.Series, pd.Index)):
        return [to_jsonable(v) for v in list(obj)]
    if obj is pd.NaT:
        return None
    try:
        if pd.isna(obj):
            return None
    except (TypeError, ValueError):
        pass
    return str(obj)


def ensure_utc_series(series: pd.Series) -> pd.Series:
    return pd.to_datetime(series, utc=True)


def get_param(params: dict[str, Any] | None, *names: str, default: Any = None) -> Any:
    """Read a job parameter accepting camelCase or snake_case spellings."""
    if not params:
        return default
    for name in names:
        if name in params and params[name] is not None:
            return params[name]
    return default


def as_bool(value: Any, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    return str(value).strip().lower() in ("1", "true", "yes", "on")
