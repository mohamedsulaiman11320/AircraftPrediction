-- Full operational-data reset before reloading the 6-month raw dataset (2026-10-23, user-approved scope).
--
-- DELETES:
--   telemetry_rejected_row, telemetry_record, telemetry_import
--   maintenance_log, maintenance_work_order, inspection_flag
--   aircraft_anomaly_result, aircraft_risk_prediction
--   aircraft
--   model_version      (its training data is being removed, so its provenance is no longer valid)
--   pipeline_run       (execution history of the removed data)
--   action_audit, auth_audit   (explicitly requested: completely clean slate)
--
-- PRESERVES (and why):
--   app_user, user_role, role, status   - accounts and their roles (requested)
--   __ef_migrations_history             - EF migration state; clearing it makes EF treat the database
--                                         as unmigrated and try to recreate tables
--   health_threshold                    - seeded BR-01/BR-03/BR-04 engine-health business rules
--   risk_status                         - seeded risk reference; FK target for predictions
--   engineering_threshold               - ships empty; part of the documented safety design
--   refresh_token, revoked_token, password_reset_token
--                                       - session state belonging to the preserved accounts
--
-- Schema is untouched: no table is dropped and no migration is reverted.
-- Apply with tools/db/reset-for-dataset-reload.ps1 (backs up and verifies first).

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
UNION ALL SELECT 'maintenance_log', count(*) FROM maintenance_log
UNION ALL SELECT 'model_version', count(*) FROM model_version
UNION ALL SELECT 'pipeline_run', count(*) FROM pipeline_run
UNION ALL SELECT 'action_audit', count(*) FROM action_audit
UNION ALL SELECT 'auth_audit', count(*) FROM auth_audit
ORDER BY 1;

-- Foreign-key order: dependants first.
DELETE FROM inspection_flag;
DELETE FROM maintenance_log;
DELETE FROM maintenance_work_order;
DELETE FROM aircraft_risk_prediction;
DELETE FROM aircraft_anomaly_result;
DELETE FROM telemetry_rejected_row;
DELETE FROM telemetry_record;
DELETE FROM telemetry_import;
DELETE FROM aircraft;
-- model_version.training_run_id references pipeline_run, so models go first.
DELETE FROM model_version;
DELETE FROM pipeline_run;
DELETE FROM action_audit;
DELETE FROM auth_audit;

\echo 'Row counts AFTER reset (all must be 0):'
SELECT 'aircraft' AS table_name, count(*) FROM aircraft
UNION ALL SELECT 'telemetry_record', count(*) FROM telemetry_record
UNION ALL SELECT 'telemetry_rejected_row', count(*) FROM telemetry_rejected_row
UNION ALL SELECT 'telemetry_import', count(*) FROM telemetry_import
UNION ALL SELECT 'maintenance_log', count(*) FROM maintenance_log
UNION ALL SELECT 'maintenance_work_order', count(*) FROM maintenance_work_order
UNION ALL SELECT 'inspection_flag', count(*) FROM inspection_flag
UNION ALL SELECT 'aircraft_anomaly_result', count(*) FROM aircraft_anomaly_result
UNION ALL SELECT 'aircraft_risk_prediction', count(*) FROM aircraft_risk_prediction
UNION ALL SELECT 'model_version', count(*) FROM model_version
UNION ALL SELECT 'pipeline_run', count(*) FROM pipeline_run
UNION ALL SELECT 'action_audit', count(*) FROM action_audit
UNION ALL SELECT 'auth_audit', count(*) FROM auth_audit
ORDER BY 1;

\echo 'Preserved (must stay populated):'
SELECT 'app_user' AS table_name, count(*) FROM app_user
UNION ALL SELECT 'user_role', count(*) FROM user_role
UNION ALL SELECT 'role', count(*) FROM role
UNION ALL SELECT 'status', count(*) FROM status
UNION ALL SELECT 'risk_status', count(*) FROM risk_status
UNION ALL SELECT 'health_threshold', count(*) FROM health_threshold
UNION ALL SELECT '__ef_migrations_history', count(*) FROM __ef_migrations_history
ORDER BY 1;

COMMIT;
