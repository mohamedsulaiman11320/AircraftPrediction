using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnomalySourcesDiagnosisActionCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_maintenance_work_order_anomaly_id",
                table: "maintenance_work_order");

            migrationBuilder.AddColumn<string>(
                name: "action_code",
                table: "work_order_report",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_error",
                table: "maintenance_work_order",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_evidence",
                table: "maintenance_work_order",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "diagnosis_generated_at_utc",
                table: "maintenance_work_order",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_method",
                table: "maintenance_work_order",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_rule_version",
                table: "maintenance_work_order",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_status",
                table: "maintenance_work_order",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NotGenerated");

            migrationBuilder.AddColumn<string>(
                name: "diagnosis_text",
                table: "maintenance_work_order",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "maintenance_work_order",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AlterColumn<Guid>(
                name: "model_version_id",
                table: "aircraft_anomaly_result",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<double>(
                name: "anomaly_score",
                table: "aircraft_anomaly_result",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<string>(
                name: "dedup_key",
                table: "aircraft_anomaly_result",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence",
                table: "aircraft_anomaly_result",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_observed_at_utc",
                table: "aircraft_anomaly_result",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rule_codes",
                table: "aircraft_anomaly_result",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "aircraft_anomaly_result",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "IsolationForest");

            migrationBuilder.CreateTable(
                name: "maintenance_action_code",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_action_code", x => x.code);
                    table.ForeignKey(
                        name: "fk_maintenance_action_code_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_report_action_code",
                table: "work_order_report",
                column: "action_code");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_anomaly_id",
                table: "maintenance_work_order",
                column: "anomaly_id",
                unique: true,
                filter: "anomaly_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_dedup_key",
                table: "aircraft_anomaly_result",
                column: "dedup_key",
                unique: true,
                filter: "dedup_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_source_aircraft_id_investigation_st",
                table: "aircraft_anomaly_result",
                columns: new[] { "source", "aircraft_id", "investigation_status" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_action_code_display_order",
                table: "maintenance_action_code",
                column: "display_order",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_action_code_status_id",
                table: "maintenance_action_code",
                column: "status_id");

            migrationBuilder.AddForeignKey(
                name: "fk_work_order_report_maintenance_action_code_action_code",
                table: "work_order_report",
                column: "action_code",
                principalTable: "maintenance_action_code",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_work_order_report_maintenance_action_code_action_code",
                table: "work_order_report");

            migrationBuilder.DropTable(
                name: "maintenance_action_code");

            migrationBuilder.DropIndex(
                name: "ix_work_order_report_action_code",
                table: "work_order_report");

            migrationBuilder.DropIndex(
                name: "ix_maintenance_work_order_anomaly_id",
                table: "maintenance_work_order");

            migrationBuilder.DropIndex(
                name: "ix_aircraft_anomaly_result_dedup_key",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropIndex(
                name: "ix_aircraft_anomaly_result_source_aircraft_id_investigation_st",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropColumn(
                name: "action_code",
                table: "work_order_report");

            migrationBuilder.DropColumn(
                name: "diagnosis_error",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "diagnosis_evidence",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "diagnosis_generated_at_utc",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "diagnosis_method",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "diagnosis_rule_version",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "diagnosis_status",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "diagnosis_text",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "source",
                table: "maintenance_work_order");

            migrationBuilder.DropColumn(
                name: "dedup_key",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropColumn(
                name: "evidence",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropColumn(
                name: "last_observed_at_utc",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropColumn(
                name: "rule_codes",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropColumn(
                name: "source",
                table: "aircraft_anomaly_result");

            migrationBuilder.AlterColumn<Guid>(
                name: "model_version_id",
                table: "aircraft_anomaly_result",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "anomaly_score",
                table: "aircraft_anomaly_result",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_work_order_anomaly_id",
                table: "maintenance_work_order",
                column: "anomaly_id");
        }
    }
}
