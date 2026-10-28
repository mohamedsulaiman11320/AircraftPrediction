using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderReportsAndReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dispatcher_review_notes",
                table: "maintenance_work_order",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_order_report",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    technician_user_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_findings = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    corrective_action = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    resolution_notes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    actual_failure_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_order_report", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_order_report_app_user_technician_user_id",
                        column: x => x.technician_user_id,
                        principalTable: "app_user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_order_report_maintenance_work_order_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "maintenance_work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_order_report_status_status_id",
                        column: x => x.status_id,
                        principalTable: "status",
                        principalColumn: "status_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_report_status_id",
                table: "work_order_report",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_order_report_technician_user_id",
                table: "work_order_report",
                column: "technician_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_order_report_work_order_id_sequence",
                table: "work_order_report",
                columns: new[] { "work_order_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_report");

            migrationBuilder.DropColumn(
                name: "dispatcher_review_notes",
                table: "maintenance_work_order");
        }
    }
}
