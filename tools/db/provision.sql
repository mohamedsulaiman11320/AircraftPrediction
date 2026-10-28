-- Creates the dedicated roles and (when allowed) the target database. Run by provision-database.ps1 as the
-- PostgreSQL superuser. All names and passwords arrive as psql variables from the root .env:
--   db_name, owner_role, api_role, ml_reader_role, etl_writer_role, ingest_role, *_pw, create_db (true/false), reset_pw (true/false)
-- Non-destructive: never drops or renames anything; existing roles keep their passwords unless reset_pw=true.

SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', r.name, r.pw)
FROM (VALUES (:'owner_role', :'owner_pw'), (:'api_role', :'api_pw'), (:'ml_reader_role', :'ml_reader_pw'), (:'etl_writer_role', :'etl_writer_pw'), (:'ingest_role', :'ingest_pw')) AS r(name, pw)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r.name) \gexec

SELECT format('ALTER ROLE %I PASSWORD %L', r.name, r.pw)
FROM (VALUES (:'owner_role', :'owner_pw'), (:'api_role', :'api_pw'), (:'ml_reader_role', :'ml_reader_pw'), (:'etl_writer_role', :'etl_writer_pw'), (:'ingest_role', :'ingest_pw')) AS r(name, pw)
WHERE :'reset_pw' = 'true' \gexec

-- The ETL/ML writer inherits the reader's SELECT grants.
SELECT format('GRANT %I TO %I', :'ml_reader_role', :'etl_writer_role') \gexec

SELECT format('CREATE DATABASE %I OWNER %I ENCODING %L TEMPLATE template0', :'db_name', :'owner_role', 'UTF8')
WHERE :'create_db' = 'true' AND NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name') \gexec

SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'db_name') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I, %I, %I, %I, %I', :'db_name', :'owner_role', :'api_role', :'ml_reader_role', :'etl_writer_role', :'ingest_role') \gexec

\connect :db_name
SELECT format('ALTER SCHEMA public OWNER TO %I', :'owner_role') \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE ON SCHEMA public TO %I, %I, %I, %I', :'api_role', :'ml_reader_role', :'etl_writer_role', :'ingest_role') \gexec
-- Tables created later by migrations (as the owner) become available to the API role automatically.
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', :'owner_role', :'api_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I', :'owner_role', :'api_role') \gexec
SELECT 'connected to ' || current_database() AS provisioned;
