using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M7_MeasurementRequestPerPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Measurements_ClientRequestId",
                table: "Measurements");

            migrationBuilder.CreateIndex(
                name: "UX_Measurements_ClientRequestPlan",
                table: "Measurements",
                columns: new[] { "ClientRequestId", "InspectionPlanId" },
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Measurements_ClientRequestPlan",
                table: "Measurements");

            migrationBuilder.CreateIndex(
                name: "UX_Measurements_ClientRequestId",
                table: "Measurements",
                column: "ClientRequestId",
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");
        }
    }
}
