"""PostgreSQL connection helpers (psycopg 3).

The reader connection is put in a read-only session; the writer connection runs in autocommit
mode and multi-statement units use explicit ``with conn.transaction()`` blocks.  DSNs are never
logged because they may contain passwords; only ``user@host:port/db`` is logged.

Defence in depth: after connecting, ``current_database()`` must equal the configured ``DB_NAME``
and must not be a protected database, otherwise the connection is closed and refused.
"""

from __future__ import annotations

import logging
from typing import Any

from .config import PROTECTED_DATABASES

log = logging.getLogger(__name__)


class DatabaseError(Exception):
    """Raised when the database cannot be reached or a statement fails."""


def connection_target(conn: Any) -> str:
    """``user@host:port/db`` of an open connection (never includes the password)."""
    info = conn.info
    return f"{info.user}@{info.host}:{info.port}/{info.dbname}"


def verify_database(conn: Any, expected_db_name: str) -> str:
    """Return ``current_database()`` if it is the expected, non-protected database; else raise."""
    with conn.cursor() as cur:
        cur.execute("SELECT current_database()")
        row = cur.fetchone()
    actual = str(row[0]) if row else ""
    if actual.lower() in {n.lower() for n in PROTECTED_DATABASES}:
        raise DatabaseError(f"Connected to the protected database {actual!r}; refusing to use it.")
    if actual != expected_db_name:
        raise DatabaseError(f"Connected to database {actual!r} but DB_NAME is {expected_db_name!r}; refusing to use it.")
    return actual


def connect(dsn: str, *, read_only: bool, expected_db_name: str, connect_timeout: int = 10) -> Any:
    try:
        import psycopg
    except ImportError as exc:  # pragma: no cover - dependency is required
        raise DatabaseError("psycopg is not installed; run pip install -r requirements.txt") from exc
    role = "reader" if read_only else "writer"
    try:
        conn = psycopg.connect(
            dsn,
            autocommit=True,
            connect_timeout=connect_timeout,
            application_name="afp_ml_reader" if read_only else "afp_ml_writer",
        )
    except psycopg.Error as exc:
        # The psycopg message does not include the password; keep it but never echo the DSN.
        raise DatabaseError(f"Could not connect to PostgreSQL ({role} DSN): {exc}") from exc
    try:
        verify_database(conn, expected_db_name)
        with conn.cursor() as cur:
            cur.execute("SET TIME ZONE 'UTC'")
            if read_only:
                cur.execute("SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY")
    except BaseException:
        conn.close()
        raise
    log.info("Connected to PostgreSQL as %s (%s)", connection_target(conn), "read-only" if read_only else role)
    return conn
