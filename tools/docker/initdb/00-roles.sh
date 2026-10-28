#!/bin/sh
# First-start only (postgres docker-entrypoint-initdb.d, runs before 01_schema.sql / 02_seed.sql).
# Creates the dedicated least-privilege roles with tools/db/provision.sql - the same script provision-database.ps1 runs.
# The database itself was already created by the image (POSTGRES_DB); it is never created or dropped here.
set -eu

psql -v ON_ERROR_STOP=1 -X -q --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v db_name="$POSTGRES_DB" -v create_db=false -v reset_pw=false \
  -v owner_role="$POSTGRES_USER" -v owner_pw="$POSTGRES_PASSWORD" \
  -v api_role="$AFP_API_ROLE" -v api_pw="$AFP_API_PASSWORD" \
  -v ml_reader_role="$AFP_ML_READER_ROLE" -v ml_reader_pw="$AFP_ML_READER_PASSWORD" \
  -v etl_writer_role="$AFP_ETL_WRITER_ROLE" -v etl_writer_pw="$AFP_ETL_WRITER_PASSWORD" \
  -v ingest_role="$AFP_INGEST_ROLE" -v ingest_pw="$AFP_INGEST_PASSWORD" \
  -f /afp-db/provision.sql
