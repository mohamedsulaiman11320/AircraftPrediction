using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryImportFileSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "file_size_bytes",
                table: "telemetry_import",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "file_size_bytes",
                table: "telemetry_import");
        }
    }
}
