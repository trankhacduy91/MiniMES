using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_OeeSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OeeSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MachineId = table.Column<int>(type: "int", nullable: false),
                    ShiftDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ShiftId = table.Column<int>(type: "int", nullable: false),
                    Hour = table.Column<byte>(type: "tinyint", nullable: true),
                    WindowStartUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    PlannedSeconds = table.Column<int>(type: "int", nullable: false),
                    RunSeconds = table.Column<int>(type: "int", nullable: false),
                    TotalCount = table.Column<long>(type: "bigint", nullable: false),
                    GoodCount = table.Column<long>(type: "bigint", nullable: false),
                    IdealSeconds = table.Column<double>(type: "float", nullable: false),
                    ScanCount = table.Column<long>(type: "bigint", nullable: false),
                    Availability = table.Column<double>(type: "float", nullable: true),
                    Performance = table.Column<double>(type: "float", nullable: true),
                    Quality = table.Column<double>(type: "float", nullable: true),
                    Oee = table.Column<double>(type: "float", nullable: true),
                    Warnings = table.Column<int>(type: "int", nullable: false),
                    IsFinal = table.Column<bool>(type: "bit", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OeeSnapshots_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OeeSnapshots_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OeeSnapshots_IsFinal",
                table: "OeeSnapshots",
                column: "IsFinal",
                filter: "[IsFinal] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_OeeSnapshots_ShiftId",
                table: "OeeSnapshots",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "UX_OeeSnapshots_MachineShiftHour",
                table: "OeeSnapshots",
                columns: new[] { "MachineId", "ShiftDate", "ShiftId", "Hour" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OeeSnapshots");
        }
    }
}
