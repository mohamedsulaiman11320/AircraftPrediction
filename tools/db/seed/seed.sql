-- GENERATED FILE - DO NOT EDIT. Source: tools/db/seed/csv/*.csv described by tools/db/seed/datasets.json
-- Regenerate: dotnet run --project Backend/tools/AFP.SeedSql -- generate
-- Apply explicitly: tools/db/apply-seed.ps1. Idempotent upserts; rows removed from a CSV are NOT deleted.
BEGIN;

DO $guard$
BEGIN
    IF current_database() <> 'aircraft_predict_db' AND current_database() NOT LIKE 'aircraft\_predict\_db\_test\_%' THEN
        RAISE EXCEPTION 'Reference seed must target aircraft_predict_db (connected to %)', current_database();
    END IF;
END
$guard$;

-- status: 3 row(s) from csv/status.csv
INSERT INTO status (status_id, name, description) VALUES
    (0, 'Inactive', 'Record retained but not in current use.'),
    (1, 'Active', 'Record in normal use (default for new rows).'),
    (2, 'Deleted', 'Soft-deleted record; retained for history and audit. Distinct from physical deletion.')
ON CONFLICT (status_id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description;

-- role: 3 row(s) from csv/role.csv
INSERT INTO role (role_id, role_name) VALUES
    (1, 'FlightDispatcher'),
    (2, 'MaintenanceTechnician'),
    (3, 'DataEngineer')
ON CONFLICT (role_id) DO UPDATE SET role_name = EXCLUDED.role_name;

-- health_threshold: 8 row(s) from csv/health_threshold.csv
INSERT INTO health_threshold (code, rule_reference, measurement, comparison, threshold_value, unit, tier, recommended_inspection, source_reference, is_validated, description, display_order) VALUES
    ('EngineTemperatureHighC', 'BR-01', 'EngineTemperature', 'GreaterThan', 700, 'C', NULL, 'Immediate thermal shroud and turbine inspection.', 'The Hub BRD v0.4 (21 October 2026) section 8 BR-01/BR-04; approved by the business owner 2026-10-27', TRUE, 'Engine temperature exceeds 700 C within the evaluation window.', 2),
    ('HighRiskWindowHours', 'BR-01', 'None', 'None', 24, 'hours', NULL, NULL, 'The Hub BRD v0.4 (21 October 2026) section 8 BR-01; approved by the business owner 2026-10-27', TRUE, 'Length of the latest period the high-risk thresholds are evaluated over.', 3),
    ('OilPressureLowPsi', 'BR-01', 'OilPressure', 'LessThan', 35, 'psi', NULL, 'Inspect the oil pressure regulator.', 'The Hub BRD v0.4 (21 October 2026) section 8 BR-01/BR-04; approved by the business owner 2026-10-27', TRUE, 'Oil pressure falls below 35 PSI within the evaluation window.', 1),
    ('RiskTierCritical', 'BR-03', 'FailureRiskScore', 'GreaterThan', 0.70, 'score', 'Critical', NULL, 'The Hub BRD v0.4 (21 October 2026) section 8 BR-03', FALSE, 'A 30-day failure-risk score above 0.70 is Critical.', 5),
    ('RiskTierLow', 'BR-03', 'FailureRiskScore', 'None', NULL, 'score', 'Low', NULL, 'The Hub BRD v0.4 (21 October 2026) section 8 BR-03', FALSE, 'A 30-day failure-risk score of 0.30 or below is Low.', 7),
    ('RiskTierMedium', 'BR-03', 'FailureRiskScore', 'GreaterThan', 0.30, 'score', 'Medium', NULL, 'The Hub BRD v0.4 (21 October 2026) section 8 BR-03', FALSE, 'A 30-day failure-risk score above 0.30 up to and including 0.70 is Medium.', 6),
    ('RoutineOperationalCheck', 'BR-04', 'None', 'None', NULL, NULL, NULL, 'Routine operational check.', 'The Hub BRD v0.4 (21 October 2026) section 8 BR-04', FALSE, 'Recommended inspection when no high-risk threshold is breached.', 4),
    ('TelemetryStaleAfterHours', 'OPS', 'None', 'None', 48, 'hours', NULL, NULL, 'Daily telemetry simulator schedule (00:00 IST batch; tools/simulator/telemetry_simulator.json)', FALSE, 'Telemetry whose latest reading is older than this is shown as stale (one missed daily batch plus margin).', 8)
ON CONFLICT (code) DO UPDATE SET rule_reference = EXCLUDED.rule_reference, measurement = EXCLUDED.measurement, comparison = EXCLUDED.comparison, threshold_value = EXCLUDED.threshold_value, unit = EXCLUDED.unit, tier = EXCLUDED.tier, recommended_inspection = EXCLUDED.recommended_inspection, source_reference = EXCLUDED.source_reference, is_validated = EXCLUDED.is_validated, description = EXCLUDED.description, display_order = EXCLUDED.display_order;

-- risk_status: 6 row(s) from csv/risk_status.csv
INSERT INTO risk_status (code, description, display_order) VALUES
    ('Critical', 'The 30-day sensor forecast crosses one approved operating limit; inspection planned.', 5),
    ('Elevated', 'Increased anomaly activity; engineering review recommended.', 2),
    ('High', 'Significant anomaly activity or model-indicated risk; prompt engineering review required.', 3),
    ('HighCritical', 'The 30-day sensor forecast crosses the engine-temperature and oil-pressure limits; inspection planned.', 6),
    ('Low', 'No notable recent anomaly activity in the evaluated window.', 1),
    ('Undetermined', 'Insufficient recent data to assess.', 4)
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description, display_order = EXCLUDED.display_order;

-- maintenance_action_code: 2 row(s) from csv/maintenance_action_code.csv
INSERT INTO maintenance_action_code (code, description, display_order) VALUES
    ('ENG_INSP', 'Engine Inspection', 1),
    ('OIL_REPL', 'Oil Filter Replacement', 2)
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description, display_order = EXCLUDED.display_order;

COMMIT;
