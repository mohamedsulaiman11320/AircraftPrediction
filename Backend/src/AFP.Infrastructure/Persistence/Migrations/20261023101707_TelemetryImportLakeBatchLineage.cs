using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryImportLakeBatchLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "lake_batch_id",
                table: "telemetry_import",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lake_batch_id",
                table: "telemetry_import");
        }
    }
}
