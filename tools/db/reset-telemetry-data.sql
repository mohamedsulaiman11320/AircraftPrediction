-- Scoped telemetry + fleet data reset for a clean ingestion cycle.
--
-- DELETES (business data only):
--   telemetry_rejected_row, telemetry_record, telemetry_import, aircraft
--   and the workflow rows that belong to that telemetry (anomalies, predictions,
--   inspection flags, work orders, maintenance logs).
--
-- PRESERVES:
--   identity and authorisation (app_user, role, user_role, refresh_token,
--   password_reset_token, revoked_token, auth_audit)
--   reference data (status, role, risk_status, health_threshold)
--   action_audit (business action history)
--   pipeline_run and model_version (pipeline observability and the model registry;
--   model_version.training_run_id still points at its training run)
--
-- Schema is untouched: no table is dropped, no migration is reverted.
-- Take a backup first:
--   pg_dump -Fc -d aircraft_predict_db -f aircraft_predict_db_pre_reset_<stamp>.dump
--
-- Apply with tools/db/reset-telemetry-data.ps1 (which enforces an explicit confirmation).

BEGIN;

DO $guard$
BEGIN
    IF current_database() NOT LIKE 'aircraft\_predict\_db%' THEN
        RAISE EXCEPTION 'Refusing to run against database %. Expected aircraft_predict_db.', current_database();
    END IF;
    IF current_database() = 'aircraft_app' THEN
        RAISE EXCEPTION 'Refusing to touch the legacy aircraft_app database.';
    END IF;
END
$guard$;

\echo 'Row counts BEFORE reset:'
SELECT 'aircraft' AS table_name, count(*) FROM aircraft
UNION ALL SELECT 'telemetry_record', count(*) FROM telemetry_record
UNION ALL SELECT 'telemetry_rejected_row', count(*) FROM telemetry_rejected_row
UNION ALL SELECT 'telemetry_import', count(*) FROM telemetry_import
UNION ALL SELECT 'aircraft_anomaly_result', count(*) FROM aircraft_anomaly_result
UNION ALL SELECT 'aircraft_risk_prediction', count(*) FROM aircraft_risk_prediction
UNION ALL SELECT 'inspection_flag', count(*) FROM inspection_flag
UNION ALL SELECT 'maintenance_work_order', count(*) FROM maintenance_work_order
UNION ALL SELECT 'maintenance_log', count(*) FROM maintenance_log
ORDER BY 1;

-- Deleted in foreign-key order. Each statement is scoped to telemetry-derived data.
DELETE FROM inspection_flag;
DELETE FROM maintenance_log;
DELETE FROM maintenance_work_order;
DELETE FROM aircraft_risk_prediction;
DELETE FROM aircraft_anomaly_result;
DELETE FROM telemetry_rejected_row;
DELETE FROM telemetry_record;
DELETE FROM telemetry_import;
-- Aircraft are auto-registered again by the ETL (AUTO_REGISTER_AIRCRAFT) on the next upload.
DELETE FROM aircraft;

\echo 'Row counts AFTER reset (business data must be 0):'
SELECT 'aircraft' AS table_name, count(*) FROM aircraft
UNION ALL SELECT 'telemetry_record', count(*) FROM telemetry_record
UNION ALL SELECT 'telemetry_rejected_row', count(*) FROM telemetry_rejected_row
UNION ALL SELECT 'telemetry_import', count(*) FROM telemetry_import
UNION ALL SELECT 'aircraft_anomaly_result', count(*) FROM aircraft_anomaly_result
UNION ALL SELECT 'aircraft_risk_prediction', count(*) FROM aircraft_risk_prediction
UNION ALL SELECT 'inspection_flag', count(*) FROM inspection_flag
UNION ALL SELECT 'maintenance_work_order', count(*) FROM maintenance_work_order
UNION ALL SELECT 'maintenance_log', count(*) FROM maintenance_log
ORDER BY 1;

\echo 'Preserved (must be non-zero where previously populated):'
SELECT 'app_user' AS table_name, count(*) FROM app_user
UNION ALL SELECT 'role', count(*) FROM role
UNION ALL SELECT 'user_role', count(*) FROM user_role
UNION ALL SELECT 'status', count(*) FROM status
UNION ALL SELECT 'risk_status', count(*) FROM risk_status
UNION ALL SELECT 'health_threshold', count(*) FROM health_threshold
UNION ALL SELECT 'action_audit', count(*) FROM action_audit
UNION ALL SELECT 'pipeline_run', count(*) FROM pipeline_run
UNION ALL SELECT 'model_version', count(*) FROM model_version
ORDER BY 1;

COMMIT;
