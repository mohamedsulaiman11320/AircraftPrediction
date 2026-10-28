-- Least-privilege grants. Run against the target database AFTER every migration, as the schema owner.
-- Normally invoked by tools\db\apply-migrations.ps1, which passes the role names from the root .env:
--   api_role, ml_reader_role, etl_writer_role, ingest_role
-- Re-runnable. Never drops data.

-- Defence in depth: refuse the protected legacy database.
DO $guard$ BEGIN
    IF current_database() = 'aircraft_app' THEN RAISE EXCEPTION 'grant-privileges.sql must not run on aircraft_app'; END IF;
END $guard$;

-- ---------- API role: data access only, no DDL, cannot alter migration history ----------
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', :'api_role') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', :'api_role') \gexec
SELECT format('REVOKE INSERT, UPDATE, DELETE ON __ef_migrations_history FROM %I', :'api_role') \gexec
-- Reference data is maintained only by the explicit seed step (schema owner), never by the API.
SELECT format('REVOKE INSERT, UPDATE, DELETE ON status, risk_status, role FROM %I', :'api_role') \gexec

-- ---------- ML reader: telemetry, fleet, labels, reference data and ML metadata only ----------
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', :'ml_reader_role') \gexec
SELECT format('GRANT SELECT ON aircraft, telemetry_record, maintenance_log, model_version, pipeline_run, '
              'aircraft_anomaly_result, aircraft_risk_prediction, risk_status, status, health_threshold, sensor_forecast TO %I',
              :'ml_reader_role') \gexec

-- ---------- ETL/ML writer: reader (membership) + inserts/limited updates on ML output tables ----------
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', :'etl_writer_role') \gexec
SELECT format('GRANT INSERT ON aircraft_anomaly_result, aircraft_risk_prediction, model_version, pipeline_run, sensor_forecast TO %I', :'etl_writer_role') \gexec
SELECT format('GRANT UPDATE (status, started_at_utc, completed_at_utc, result_summary, error_message, model_version_id, '
              'records_processed, worker_host) ON pipeline_run TO %I', :'etl_writer_role') \gexec
-- Registration only (Trained -> Registered); approval/activation columns are deliberately not granted.
SELECT format('GRANT UPDATE (status, registered_at_utc, metrics, notes) ON model_version TO %I', :'etl_writer_role') \gexec
-- Backfill of the durable MinIO copy of an existing artifact (path and hash stay immutable).
SELECT format('GRANT UPDATE (artifact_uri) ON model_version TO %I', :'etl_writer_role') \gexec
SELECT format('GRANT USAGE ON SEQUENCE aircraft_anomaly_result_id_seq TO %I', :'etl_writer_role') \gexec

-- ---------- ETL ingest role (Spark raw JSON -> telemetry): only what loading needs ----------
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', :'ingest_role') \gexec
SELECT format('GRANT SELECT ON aircraft, status, telemetry_import TO %I', :'ingest_role') \gexec
-- Auto-registration of unknown tail numbers (placeholders) and last-telemetry bookkeeping.
SELECT format('GRANT INSERT ON aircraft TO %I', :'ingest_role') \gexec
SELECT format('GRANT UPDATE (last_telemetry_at_utc, updated_at_utc) ON aircraft TO %I', :'ingest_role') \gexec
SELECT format('GRANT INSERT ON telemetry_record, telemetry_rejected_row, action_audit TO %I', :'ingest_role') \gexec
-- Re-processing an import replaces its previous rejected-row report.
SELECT format('GRANT SELECT (import_id), DELETE ON telemetry_rejected_row TO %I', :'ingest_role') \gexec
SELECT format('GRANT INSERT ON telemetry_import TO %I', :'ingest_role') \gexec
SELECT format('GRANT UPDATE (status, total_rows, accepted_rows, rejected_rows, duplicate_rows, warning_count, warnings, '
              'error_message, started_at_utc, completed_at_utc, lake_batch_id) ON telemetry_import TO %I', :'ingest_role') \gexec
SELECT format('GRANT SELECT (id), INSERT ON pipeline_run TO %I', :'ingest_role') \gexec
SELECT format('GRANT UPDATE (status, started_at_utc, completed_at_utc, result_summary, error_message, records_processed, worker_host) '
              'ON pipeline_run TO %I', :'ingest_role') \gexec
SELECT format('GRANT USAGE ON SEQUENCE telemetry_record_id_seq, telemetry_rejected_row_id_seq, action_audit_id_seq TO %I', :'ingest_role') \gexec
-- Maintenance-log ingestion (bronze/maintenance CSV -> maintenance_log). These rows are the source of
-- the supervised failure labels, so the ETL may create and correct them, but never delete them.
SELECT format('GRANT SELECT, INSERT ON maintenance_log TO %I', :'ingest_role') \gexec
SELECT format('GRANT UPDATE (event_type, description, is_verified, actual_failure_confirmed, component_id) '
              'ON maintenance_log TO %I', :'ingest_role') \gexec
SELECT format('REVOKE ALL ON app_user, role, user_role, refresh_token, password_reset_token, revoked_token, auth_audit, '
              'maintenance_work_order, inspection_flag, model_version, aircraft_anomaly_result, '
              'aircraft_risk_prediction, engineering_threshold FROM %I', :'ingest_role') \gexec

-- Explicitly no access for ML roles to security, business-audit and workflow tables.
SELECT format('REVOKE ALL ON app_user, role, user_role, refresh_token, password_reset_token, revoked_token, auth_audit, action_audit, maintenance_work_order, '
              'inspection_flag, telemetry_import, telemetry_rejected_row, engineering_threshold FROM %I, %I',
              :'ml_reader_role', :'etl_writer_role') \gexec

-- The ETL writer may only set model_version.status to Trained/Registered, even if column grants are widened by mistake.
-- Once a model has left Trained/Registered (approved by a human), the writer may not touch its evidence (metrics,
-- notes, registration time); the ONLY permitted change is backfilling artifact_uri, the location of its durable copy
-- (2026-10-27: the guard used to reject every update of such a row, including that backfill).
SELECT format($fn$
CREATE OR REPLACE FUNCTION afp_guard_model_status() RETURNS trigger LANGUAGE plpgsql AS $body$
BEGIN
    IF current_user = %L THEN
        IF TG_OP = 'INSERT' OR NEW.status IS DISTINCT FROM OLD.status THEN
            IF NEW.status NOT IN ('Trained', 'Registered') THEN
                RAISE EXCEPTION '%% may not set model_version.status to %%', current_user, NEW.status;
            END IF;
        ELSIF OLD.status NOT IN ('Trained', 'Registered')
              AND (NEW.metrics IS DISTINCT FROM OLD.metrics OR NEW.notes IS DISTINCT FROM OLD.notes
                   OR NEW.registered_at_utc IS DISTINCT FROM OLD.registered_at_utc) THEN
            RAISE EXCEPTION '%% may not modify a %% model (only artifact_uri may be backfilled)', current_user, OLD.status;
        END IF;
    END IF;
    RETURN NEW;
END $body$;$fn$, :'etl_writer_role') \gexec
DROP TRIGGER IF EXISTS trg_model_version_status_guard ON model_version;
CREATE TRIGGER trg_model_version_status_guard BEFORE INSERT OR UPDATE ON model_version
    FOR EACH ROW EXECUTE FUNCTION afp_guard_model_status();
