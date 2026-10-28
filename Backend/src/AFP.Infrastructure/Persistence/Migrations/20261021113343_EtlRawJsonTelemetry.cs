using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EtlRawJsonTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_record_id",
                table: "telemetry_record",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_status_flag",
                table: "telemetry_record",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "vibration_index",
                table: "telemetry_record",
                type: "double precision",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "requested_by_user_id",
                table: "telemetry_import",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "telemetry_import",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Every import created before this migration came through the CSV path.
                defaultValue: "Csv");

            migrationBuilder.CreateIndex(
                name: "ux_telemetry_record_source_record",
                table: "telemetry_record",
                columns: new[] { "data_source", "source_record_id" },
                unique: true,
                filter: "source_record_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_telemetry_record_source_record",
                table: "telemetry_record");

            migrationBuilder.DropColumn(
                name: "source_record_id",
                table: "telemetry_record");

            migrationBuilder.DropColumn(
                name: "source_status_flag",
                table: "telemetry_record");

            migrationBuilder.DropColumn(
                name: "vibration_index",
                table: "telemetry_record");

            migrationBuilder.DropColumn(
                name: "source",
                table: "telemetry_import");

            migrationBuilder.AlterColumn<long>(
                name: "requested_by_user_id",
                table: "telemetry_import",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
