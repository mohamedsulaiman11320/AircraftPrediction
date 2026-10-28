SYNTHETIC AIRCRAFT TRAINING DATA — NOT REAL OPERATIONAL EVIDENCE
Generated: 2026-10-23
Source: derived statistically from the uploaded telemetry_may_oct_2026_6months JSON.

Files:
- telemetry_synthetic_12mo.json: 53,434 rows including intentional duplicate rows.
- maintenance_synthetic_12mo.json: 36 synthetic ENG_UNSCHED_REMOVAL events.

Coverage:
- Aircraft: 6 (N120UA, N237UA, N512UA, N668UA, N784UA, N903UA)
- Telemetry timestamps: 2025-10-23T00:00:00+00:00 through 2026-10-23T23:00:00+00:00
- Hourly records per aircraft before duplicates.
- Six synthetic failure events per aircraft (36 total).
- Event intervals are approximately 60 days; this is within the requested 60–90-day target.
- Each first event has at least 30 days of prior telemetry.
- Latest event is at least 30 days before the end of telemetry, leaving observable follow-up.
- Degradation signal injected during the 14 days before each synthetic event: temperature/vibration rise and pressure falls.
- Intentional duplicate rows and occasional missing sensor values are included for ETL/data-quality testing.

Important limitations:
- These records and all maintenance events are synthetic, not evidence of actual aircraft failures.
- Do not present model outputs trained on these files as validated real-world probabilities.
- Do not merge these files into production tables or the operational aircraft_app database.
- technician_id is null intentionally because no real technician identity was supplied. Before using the maintenance file with an ingestion schema requiring a technician FK/non-null ID, map it only to an approved test identity or stop and report the schema constraint. Never invent a real technician ID.
- status_flag is a descriptive/threshold-like field, not the supervised target. Exclude it from model features to prevent leakage.
- The supervised target must be derived from event_date/action_code and the 30-day horizon, not telemetry status flags.
