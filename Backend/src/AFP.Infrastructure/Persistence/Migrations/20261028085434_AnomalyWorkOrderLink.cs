using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnomalyWorkOrderLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "work_order_id",
                table: "aircraft_anomaly_result",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_aircraft_anomaly_result_work_order_id",
                table: "aircraft_anomaly_result",
                column: "work_order_id");

            migrationBuilder.AddForeignKey(
                name: "fk_aircraft_anomaly_result_maintenance_work_order_work_order_id",
                table: "aircraft_anomaly_result",
                column: "work_order_id",
                principalTable: "maintenance_work_order",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_aircraft_anomaly_result_maintenance_work_order_work_order_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropIndex(
                name: "ix_aircraft_anomaly_result_work_order_id",
                table: "aircraft_anomaly_result");

            migrationBuilder.DropColumn(
                name: "work_order_id",
                table: "aircraft_anomaly_result");
        }
    }
}
