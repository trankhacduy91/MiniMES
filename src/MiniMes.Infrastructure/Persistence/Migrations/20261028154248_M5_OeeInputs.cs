using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_OeeInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineStateHistory_MachineId_StartAt",
                table: "MachineStateHistory");

            migrationBuilder.AddColumn<int>(
                name: "DowntimeReasonId",
                table: "MachineStateHistory",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScanLogs_StationId_ScannedAt",
                table: "ScanLogs",
                columns: new[] { "StationId", "ScannedAt" })
                .Annotation("SqlServer:Include", new[] { "Result", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineStateHistory_DowntimeReasonId",
                table: "MachineStateHistory",
                column: "DowntimeReasonId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineStateHistory_MachineId_StartAt",
                table: "MachineStateHistory",
                columns: new[] { "MachineId", "StartAt" })
                .Annotation("SqlServer:Include", new[] { "EndAt", "State", "DowntimeReasonId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MachineStateHistory_DowntimeReasons_DowntimeReasonId",
                table: "MachineStateHistory",
                column: "DowntimeReasonId",
                principalTable: "DowntimeReasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MachineStateHistory_DowntimeReasons_DowntimeReasonId",
                table: "MachineStateHistory");

            migrationBuilder.DropIndex(
                name: "IX_ScanLogs_StationId_ScannedAt",
                table: "ScanLogs");

            migrationBuilder.DropIndex(
                name: "IX_MachineStateHistory_DowntimeReasonId",
                table: "MachineStateHistory");

            migrationBuilder.DropIndex(
                name: "IX_MachineStateHistory_MachineId_StartAt",
                table: "MachineStateHistory");

            migrationBuilder.DropColumn(
                name: "DowntimeReasonId",
                table: "MachineStateHistory");

            migrationBuilder.CreateIndex(
                name: "IX_MachineStateHistory_MachineId_StartAt",
                table: "MachineStateHistory",
                columns: new[] { "MachineId", "StartAt" });
        }
    }
}
