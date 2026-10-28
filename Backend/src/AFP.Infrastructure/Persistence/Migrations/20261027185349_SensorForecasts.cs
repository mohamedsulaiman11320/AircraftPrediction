using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SensorForecasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sensor_forecast",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    aircraft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    horizon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    generated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    data_as_of_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    pipeline_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    points = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensor_forecast", x => x.id);
                    table.CheckConstraint("ck_sensor_forecast_points_array", "jsonb_typeof(points) = 'array'");
                    table.ForeignKey(
                        name: "fk_sensor_forecast_aircraft_aircraft_id",
                        column: x => x.aircraft_id,
                        principalTable: "aircraft",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sensor_forecast_model_version_model_version_id",
                        column: x => x.model_version_id,
                        principalTable: "model_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sensor_forecast_pipeline_runs_pipeline_run_id",
                        column: x => x.pipeline_run_id,
                        principalTable: "pipeline_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_sensor_forecast_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sensor_forecast_aircraft_id_horizon_generated_at_utc",
                table: "sensor_forecast",
                columns: new[] { "aircraft_id", "horizon", "generated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_sensor_forecast_model_version_id",
                table: "sensor_forecast",
                column: "model_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_forecast_pipeline_run_id",
                table: "sensor_forecast",
                column: "pipeline_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensor_forecast_status_id",
                table: "sensor_forecast",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ux_sensor_forecast_idempotency",
                table: "sensor_forecast",
                columns: new[] { "aircraft_id", "model_version_id", "horizon", "data_as_of_utc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sensor_forecast");
        }
    }
}
