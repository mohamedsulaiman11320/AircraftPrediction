using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthThresholdReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_threshold",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    rule_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    measurement = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    comparison = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    threshold_value = table.Column<double>(type: "double precision", nullable: true),
                    unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    tier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    recommended_inspection = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    source_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_validated = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_threshold", x => x.code);
                    table.ForeignKey(
                        name: "fk_health_threshold_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_threshold_display_order",
                table: "health_threshold",
                column: "display_order",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_health_threshold_status_id",
                table: "health_threshold",
                column: "status_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_threshold");
        }
    }
}
