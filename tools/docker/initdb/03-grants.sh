#!/bin/sh
# First-start only, after 01_schema.sql and 02_seed.sql: least-privilege grants with tools/db/grant-privileges.sql -
# the same script apply-migrations.ps1 runs after every migration.
set -eu

psql -v ON_ERROR_STOP=1 -X -q --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v api_role="$AFP_API_ROLE" -v ml_reader_role="$AFP_ML_READER_ROLE" \
  -v etl_writer_role="$AFP_ETL_WRITER_ROLE" -v ingest_role="$AFP_INGEST_ROLE" \
  -f /afp-db/grant-privileges.sql
