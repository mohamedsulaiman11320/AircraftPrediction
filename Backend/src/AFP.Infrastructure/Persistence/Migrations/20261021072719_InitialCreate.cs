using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "status",
                columns: table => new
                {
                    status_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status", x => x.status_id);
                });

            migrationBuilder.CreateTable(
                name: "aircraft",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    registration_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fleet_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    operational_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status_changed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_telemetry_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_inspection_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aircraft", x => x.id);
                    table.ForeignKey(
                        name: "fk_aircraft_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    last_login_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_app_user_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "risk_status",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_status", x => x.code);
                    table.ForeignKey(
                        name: "fk_risk_status_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role",
                columns: table => new
                {
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    role_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role", x => x.role_id);
                    table.ForeignKey(
                        name: "fk_role_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "action_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_action_audit_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_action_audit_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "auth_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_audit_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_auth_audit_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "engineering_threshold",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    component_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    measurement = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    min_value = table.Column<double>(type: "double precision", nullable: true),
                    max_value = table.Column<double>(type: "double precision", nullable: true),
                    unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    specification_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    approved_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engineering_threshold", x => x.id);
                    table.CheckConstraint("ck_engineering_threshold_specification", "length(trim(specification_reference)) > 0");
                    table.ForeignKey(
                        name: "fk_engineering_threshold_app_user_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_engineering_threshold_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_token",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    requested_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_reset_token_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_password_reset_token_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    job_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    result_summary = table.Column<string>(type: "jsonb", nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    model_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    records_processed = table.Column<int>(type: "integer", nullable: true),
                    worker_host = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_run_app_user_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_pipeline_run_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_token",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_token_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_refresh_token_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "revoked_token",
                columns: table => new
                {
                    jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revoked_token", x => x.jti);
                    table.ForeignKey(
                        name: "fk_revoked_token_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_revoked_token_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    stored_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    accepted_rows = table.Column<int>(type: "integer", nullable: false),
                    rejected_rows = table.Column<int>(type: "integer", nullable: false),
                    duplicate_rows = table.Column<int>(type: "integer", nullable: false),
                    warning_count = table.Column<int>(type: "integer", nullable: false),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    requested_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_import", x => x.id);
                    table.ForeignKey(
                        name: "fk_telemetry_import_app_user_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_telemetry_import_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_role",
                columns: table => new
                {
                    user_role_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    assigned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_role", x => x.user_role_id);
                    table.ForeignKey(
                        name: "fk_user_role_app_user_assigned_by_user_id",
                        column: x => x.assigned_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_app_user_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_role_role_id",
                        column: x => x.role_id,
                        principalTable: "role",
                        principalColumn: "role_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    model_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    artifact_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    artifact_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    feature_names = table.Column<string>(type: "jsonb", nullable: false),
                    hyperparameters = table.Column<string>(type: "jsonb", nullable: false),
                    metrics = table.Column<string>(type: "jsonb", nullable: false),
                    training_data_from_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    training_data_to_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    training_row_count = table.Column<int>(type: "integer", nullable: false),
                    prediction_horizon_days = table.Column<int>(type: "integer", nullable: true),
                    limitations = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    registered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    activated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    training_run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_version", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_version_app_user_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_model_version_pipeline_runs_training_run_id",
                        column: x => x.training_run_id,
                        principalTable: "pipeline_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_model_version_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_record",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sensor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    temperature_value = table.Column<double>(type: "double precision", nullable: true),
                    temperature_unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    temperature_c = table.Column<double>(type: "double precision", nullable: true),
                    pressure_value = table.Column<double>(type: "double precision", nullable: true),
                    pressure_unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    pressure_kpa = table.Column<double>(type: "double precision", nullable: true),
                    data_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quality_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quality_flags = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ingested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_row_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_record", x => x.id);
                    table.ForeignKey(
                        name: "fk_telemetry_record_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_telemetry_record_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_telemetry_record_telemetry_import_import_id",
                        column: x => x.import_id,
                        principalTable: "telemetry_import",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_rejected_row",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    raw_line = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    reasons = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_rejected_row", x => x.id);
                    table.ForeignKey(
                        name: "fk_telemetry_rejected_row_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_telemetry_rejected_row_telemetry_import_import_id",
                        column: x => x.import_id,
                        principalTable: "telemetry_import",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "aircraft_anomaly_result",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    telemetry_record_id = table.Column<long>(type: "bigint", nullable: true),
                    sensor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    observed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    anomaly_score = table.Column<double>(type: "double precision", nullable: false),
                    is_anomaly = table.Column<bool>(type: "boolean", nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scored_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    feature_values = table.Column<string>(type: "jsonb", nullable: false),
                    investigation_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    acknowledged_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    investigation_notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aircraft_anomaly_result", x => x.id);
                    table.ForeignKey(
                        name: "fk_aircraft_anomaly_result_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_anomaly_result_app_user_acknowledged_by_user_id",
                        column: x => x.acknowledged_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_anomaly_result_model_version_model_version_id",
                        column: x => x.model_version_id,
                        principalTable: "model_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_anomaly_result_pipeline_runs_pipeline_run_id",
                        column: x => x.pipeline_run_id,
                        principalTable: "pipeline_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_aircraft_anomaly_result_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_anomaly_result_telemetry_record_telemetry_record_id",
                        column: x => x.telemetry_record_id,
                        principalTable: "telemetry_record",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "aircraft_risk_prediction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prediction_timestamp_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    horizon_days = table.Column<int>(type: "integer", nullable: false),
                    risk_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    basis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prediction_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    feature_cutoff_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    probability = table.Column<double>(type: "double precision", nullable: true),
                    probability_validated = table.Column<bool>(type: "boolean", nullable: false),
                    feature_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    model_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pipeline_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reviewed_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    actual_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outcome_recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    outcome_work_order_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aircraft_risk_prediction", x => x.id);
                    table.CheckConstraint("ck_aircraft_risk_prediction_probability_validated", "probability IS NULL OR probability_validated");
                    table.ForeignKey(
                        name: "fk_aircraft_risk_prediction_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_risk_prediction_app_user_reviewed_by_user_id",
                        column: x => x.reviewed_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_risk_prediction_model_version_model_version_id",
                        column: x => x.model_version_id,
                        principalTable: "model_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_risk_prediction_pipeline_runs_pipeline_run_id",
                        column: x => x.pipeline_run_id,
                        principalTable: "pipeline_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_aircraft_risk_prediction_risk_status_risk_category",
                        column: x => x.risk_category,
                        principalTable: "risk_status",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_aircraft_risk_prediction_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_work_order",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    work_order_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    anomaly_id = table.Column<long>(type: "bigint", nullable: true),
                    prediction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assigned_technician_id = table.Column<long>(type: "bigint", nullable: true),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    inspection_findings = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    corrective_action = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    actual_failure_confirmed = table.Column<bool>(type: "boolean", nullable: true),
                    resolution_notes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_work_order", x => x.id);
                    table.ForeignKey(
                        name: "fk_maintenance_work_order_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_work_order_anomaly_results_anomaly_id",
                        column: x => x.anomaly_id,
                        principalTable: "aircraft_anomaly_result",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_maintenance_work_order_app_user_assigned_technician_id",
                        column: x => x.assigned_technician_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_work_order_app_user_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_work_order_risk_predictions_prediction_id",
                        column: x => x.prediction_id,
                        principalTable: "aircraft_risk_prediction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_maintenance_work_order_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inspection_flag",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_id = table.Column<long>(type: "bigint", nullable: true),
                    prediction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    raised_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    raised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    cleared_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    cleared_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    clearance_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inspection_flag", x => x.id);
                    table.ForeignKey(
                        name: "fk_inspection_flag_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inspection_flag_anomaly_results_anomaly_id",
                        column: x => x.anomaly_id,
                        principalTable: "aircraft_anomaly_result",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_inspection_flag_app_user_cleared_by_user_id",
                        column: x => x.cleared_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inspection_flag_app_user_raised_by_user_id",
                        column: x => x.raised_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inspection_flag_risk_predictions_prediction_id",
                        column: x => x.prediction_id,
                        principalTable: "aircraft_risk_prediction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_inspection_flag_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inspection_flag_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "maintenance_work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    actual_failure_confirmed = table.Column<bool>(type: "boolean", nullable: true),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_maintenance_log_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_log_app_user_recorded_by_user_id",
                        column: x => x.recorded_by_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_maintenance_log_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_log_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "maintenance_work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_action_audit_entity_type_entity_id",
                table: "action_audit",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_action_audit_occurred_at_utc",
                table: "action_audit",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_action_audit_status_id",
                table: "action_audit",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_action_audit_user_id",
                table: "action_audit",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_fleet_group",
                table: "aircraft",
                column: "fleet_group");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_operational_status",
                table: "aircraft",
                column: "operational_status");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_registration_number",
                table: "aircraft",
                column: "registration_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_status_id",
                table: "aircraft",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_acknowledged_by_user_id",
                table: "aircraft_anomaly_result",
                column: "acknowledged_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_aircraft_id_observed_at_utc",
                table: "aircraft_anomaly_result",
                columns: new[] { "aircraft_id", "observed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_investigation_status",
                table: "aircraft_anomaly_result",
                column: "investigation_status");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_model_version_id",
                table: "aircraft_anomaly_result",
                column: "model_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_observed_at_utc",
                table: "aircraft_anomaly_result",
                column: "observed_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_pipeline_run_id",
                table: "aircraft_anomaly_result",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_status_id",
                table: "aircraft_anomaly_result",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_telemetry_record_id_model_version_id",
                table: "aircraft_anomaly_result",
                columns: new[] { "telemetry_record_id", "model_version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_aircraft_id_prediction_timestamp_u",
                table: "aircraft_risk_prediction",
                columns: new[] { "aircraft_id", "prediction_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_approval_status",
                table: "aircraft_risk_prediction",
                column: "approval_status");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_model_version_id",
                table: "aircraft_risk_prediction",
                column: "model_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_outcome_work_order_id",
                table: "aircraft_risk_prediction",
                column: "outcome_work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_pipeline_run_id",
                table: "aircraft_risk_prediction",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_reviewed_by_user_id",
                table: "aircraft_risk_prediction",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_risk_category",
                table: "aircraft_risk_prediction",
                column: "risk_category");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_risk_prediction_status_id",
                table: "aircraft_risk_prediction",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ux_aircraft_risk_prediction_idempotency",
                table: "aircraft_risk_prediction",
                columns: new[] { "aircraft_id", "basis", "model_version_id", "feature_cutoff_utc", "horizon_days" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_app_user_email",
                table: "app_user",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_user_status",
                table: "app_user",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_status_id",
                table: "app_user",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_username",
                table: "app_user",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_email",
                table: "auth_audit",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_occurred_at_utc",
                table: "auth_audit",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_status_id",
                table: "auth_audit",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_user_id",
                table: "auth_audit",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_engineering_threshold_approved_by_user_id",
                table: "engineering_threshold",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_engineering_threshold_status_id",
                table: "engineering_threshold",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_aircraft_id_is_active",
                table: "inspection_flag",
                columns: new[] { "aircraft_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_anomaly_id",
                table: "inspection_flag",
                column: "anomaly_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_cleared_by_user_id",
                table: "inspection_flag",
                column: "cleared_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_prediction_id",
                table: "inspection_flag",
                column: "prediction_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_raised_by_user_id",
                table: "inspection_flag",
                column: "raised_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_status_id",
                table: "inspection_flag",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspection_flag_work_order_id",
                table: "inspection_flag",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_log_aircraft_id_occurred_at_utc",
                table: "maintenance_log",
                columns: new[] { "aircraft_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_log_event_type",
                table: "maintenance_log",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_log_recorded_by_user_id",
                table: "maintenance_log",
                column: "recorded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_log_status_id",
                table: "maintenance_log",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_log_work_order_id",
                table: "maintenance_log",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_aircraft_id_status",
                table: "maintenance_work_order",
                columns: new[] { "aircraft_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_anomaly_id",
                table: "maintenance_work_order",
                column: "anomaly_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_assigned_technician_id",
                table: "maintenance_work_order",
                column: "assigned_technician_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_created_by_user_id",
                table: "maintenance_work_order",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_prediction_id",
                table: "maintenance_work_order",
                column: "prediction_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_status",
                table: "maintenance_work_order",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_status_id",
                table: "maintenance_work_order",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_work_order_number",
                table: "maintenance_work_order",
                column: "work_order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_model_version_approved_by_user_id",
                table: "model_version",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_version_model_name_version",
                table: "model_version",
                columns: new[] { "model_name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_model_version_status_id",
                table: "model_version",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_model_version_training_run_id",
                table: "model_version",
                column: "training_run_id");

            migrationBuilder.CreateIndex(
                name: "ux_model_version_one_active_per_type",
                table: "model_version",
                column: "model_type",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_status_id",
                table: "password_reset_token",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_token_hash",
                table: "password_reset_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_user_id",
                table: "password_reset_token",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_run_job_type",
                table: "pipeline_run",
                column: "job_type");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_run_requested_by_user_id",
                table: "pipeline_run",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_run_status_id",
                table: "pipeline_run",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_run_status_requested_at_utc",
                table: "pipeline_run",
                columns: new[] { "status", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_family_id",
                table: "refresh_token",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_status_id",
                table: "refresh_token",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_token_hash",
                table: "refresh_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_user_id_expires_at_utc",
                table: "refresh_token",
                columns: new[] { "user_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_revoked_token_expires_at_utc",
                table: "revoked_token",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_revoked_token_status_id",
                table: "revoked_token",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_revoked_token_user_id",
                table: "revoked_token",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_risk_status_status_id",
                table: "risk_status",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_role_name",
                table: "role",
                column: "role_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_status_id",
                table: "role",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_status_name",
                table: "status",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_import_file_sha256",
                table: "telemetry_import",
                column: "file_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_import_requested_at_utc",
                table: "telemetry_import",
                column: "requested_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_import_requested_by_user_id",
                table: "telemetry_import",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_import_status_id",
                table: "telemetry_import",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_record_aircraft_id_recorded_at_utc",
                table: "telemetry_record",
                columns: new[] { "aircraft_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_record_aircraft_id_sensor_id_recorded_at_utc",
                table: "telemetry_record",
                columns: new[] { "aircraft_id", "sensor_id", "recorded_at_utc" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_record_import_id",
                table: "telemetry_record",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_record_recorded_at_utc",
                table: "telemetry_record",
                column: "recorded_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_record_status_id",
                table: "telemetry_record",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_rejected_row_import_id_row_number",
                table: "telemetry_rejected_row",
                columns: new[] { "import_id", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_rejected_row_status_id",
                table: "telemetry_rejected_row",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_assigned_by_user_id",
                table: "user_role",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_revoked_by_user_id",
                table: "user_role",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_role_id",
                table: "user_role",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_status_id",
                table: "user_role",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_role_active",
                table: "user_role",
                columns: new[] { "user_id", "role_id" },
                unique: true,
                filter: "status_id = 1");

            migrationBuilder.AddForeignKey(
                name: "fk_aircraft_risk_prediction_maintenance_work_order_outcome_wor",
                table: "aircraft_risk_prediction",
                column: "outcome_work_order_id",
                principalTable: "maintenance_work_order",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_app_user_acknowledged_by_user_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_risk_prediction_app_user_reviewed_by_user_id",
                table: "aircraft_risk_prediction");

            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_work_order_app_user_assigned_technician_id",
                table: "maintenance_work_order");

            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_work_order_app_user_created_by_user_id",
                table: "maintenance_work_order");

            migrationBuilder.DropForeignKey(
                name: "fk_model_version_app_user_approved_by_user_id",
                table: "model_version");

            migrationBuilder.DropForeignKey(
                name: "fk_pipeline_run_app_user_requested_by_user_id",
                table: "pipeline_run");

            migrationBuilder.DropForeignKey(
                name: "fk_telemetry_import_app_user_requested_by_user_id",
                table: "telemetry_import");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_status_status_id",
                table: "aircraft");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_status_status_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_risk_prediction_status_status_id",
                table: "aircraft_risk_prediction");

            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_work_order_status_status_id",
                table: "maintenance_work_order");

            migrationBuilder.DropForeignKey(
                name: "fk_model_version_status_status_id",
                table: "model_version");

            migrationBuilder.DropForeignKey(
                name: "fk_pipeline_run_status_status_id",
                table: "pipeline_run");

            migrationBuilder.DropForeignKey(
                name: "fk_risk_status_status_status_id",
                table: "risk_status");

            migrationBuilder.DropForeignKey(
                name: "fk_telemetry_import_status_status_id",
                table: "telemetry_import");

            migrationBuilder.DropForeignKey(
                name: "fk_telemetry_record_status_status_id",
                table: "telemetry_record");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_aircraft_aircraft_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_risk_prediction_aircraft_aircraft_id",
                table: "aircraft_risk_prediction");

            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_work_order_aircraft_aircraft_id",
                table: "maintenance_work_order");

            migrationBuilder.DropForeignKey(
                name: "fk_telemetry_record_aircraft_aircraft_id",
                table: "telemetry_record");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_model_version_model_version_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_risk_prediction_model_version_model_version_id",
                table: "aircraft_risk_prediction");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_pipeline_runs_pipeline_run_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_risk_prediction_pipeline_runs_pipeline_run_id",
                table: "aircraft_risk_prediction");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_telemetry_record_telemetry_record_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_risk_prediction_maintenance_work_order_outcome_wor",
                table: "aircraft_risk_prediction");

            migrationBuilder.DropTable(
                name: "action_audit");

            migrationBuilder.DropTable(
                name: "auth_audit");

            migrationBuilder.DropTable(
                name: "engineering_threshold");

            migrationBuilder.DropTable(
                name: "inspection_flag");

            migrationBuilder.DropTable(
                name: "maintenance_log");

            migrationBuilder.DropTable(
                name: "password_reset_token");

            migrationBuilder.DropTable(
                name: "refresh_token");

            migrationBuilder.DropTable(
                name: "revoked_token");

            migrationBuilder.DropTable(
                name: "telemetry_rejected_row");

            migrationBuilder.DropTable(
                name: "user_role");

            migrationBuilder.DropTable(
                name: "role");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropTable(
                name: "status");

            migrationBuilder.DropTable(
                name: "aircraft");

            migrationBuilder.DropTable(
                name: "model_version");

            migrationBuilder.DropTable(
                name: "pipeline_run");

            migrationBuilder.DropTable(
                name: "telemetry_record");

            migrationBuilder.DropTable(
                name: "telemetry_import");

            migrationBuilder.DropTable(
                name: "maintenance_work_order");

            migrationBuilder.DropTable(
                name: "aircraft_anomaly_result");

            migrationBuilder.DropTable(
                name: "aircraft_risk_prediction");

            migrationBuilder.DropTable(
                name: "risk_status");
        }
    }
}
