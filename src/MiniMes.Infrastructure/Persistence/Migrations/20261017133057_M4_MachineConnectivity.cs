using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_MachineConnectivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AndonCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MachineId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    RaisedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AckBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    AckAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DowntimeReasonId = table.Column<int>(type: "int", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EscalationLevel = table.Column<int>(type: "int", nullable: false),
                    LastEscalatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AndonCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AndonCalls_DowntimeReasons_DowntimeReasonId",
                        column: x => x.DowntimeReasonId,
                        principalTable: "DowntimeReasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AndonCalls_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AndonEscalationRules",
                columns: table => new
                {
                    AndonType = table.Column<byte>(type: "tinyint", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    AfterMinutes = table.Column<int>(type: "int", nullable: false),
                    NotifyRoles = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AndonEscalationRules", x => new { x.AndonType, x.Level });
                });

            migrationBuilder.CreateTable(
                name: "MachineEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MsgId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    MachineTs = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: true),
                    Counter = table.Column<long>(type: "bigint", nullable: true),
                    Delta = table.Column<long>(type: "bigint", nullable: true),
                    IsReset = table.Column<bool>(type: "bit", nullable: false),
                    GoodDelta = table.Column<int>(type: "int", nullable: true),
                    NgDelta = table.Column<int>(type: "int", nullable: true),
                    AlarmCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MachineStateHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MachineId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    StartAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    EndAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineStateHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineStateHistory_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MachineTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MachineId = table.Column<int>(type: "int", nullable: false),
                    TagName = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LowerPlausible = table.Column<double>(type: "float", nullable: false),
                    UpperPlausible = table.Column<double>(type: "float", nullable: false),
                    SpcCharacteristicId = table.Column<int>(type: "int", nullable: true),
                    SpcSampleEverySec = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineTags", x => x.Id);
                    table.CheckConstraint("CK_MachineTags_Plausible", "[LowerPlausible] < [UpperPlausible]");
                    table.ForeignKey(
                        name: "FK_MachineTags_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MachineTagId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<double>(type: "float", nullable: false),
                    Quality = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceTs = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    MsgId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessValues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AndonCalls_DowntimeReasonId",
                table: "AndonCalls",
                column: "DowntimeReasonId");

            migrationBuilder.CreateIndex(
                name: "IX_AndonCalls_Status_RaisedAt",
                table: "AndonCalls",
                columns: new[] { "Status", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_AndonCalls_OpenPerMachineType",
                table: "AndonCalls",
                columns: new[] { "MachineId", "Type" },
                unique: true,
                filter: "[Status] <= 1");

            migrationBuilder.CreateIndex(
                name: "IX_MachineEvents_MachineId_MachineTs",
                table: "MachineEvents",
                columns: new[] { "MachineId", "MachineTs" })
                .Annotation("SqlServer:Include", new[] { "Kind", "Delta" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineEvents_MsgId",
                table: "MachineEvents",
                column: "MsgId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineStateHistory_MachineId_StartAt",
                table: "MachineStateHistory",
                columns: new[] { "MachineId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "UX_MachineStateHistory_OpenPerMachine",
                table: "MachineStateHistory",
                column: "MachineId",
                unique: true,
                filter: "[EndAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MachineTags_MachineId_TagName",
                table: "MachineTags",
                columns: new[] { "MachineId", "TagName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessValues_MachineTagId_SourceTs",
                table: "ProcessValues",
                columns: new[] { "MachineTagId", "SourceTs" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessValues_MsgId",
                table: "ProcessValues",
                column: "MsgId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AndonCalls");

            migrationBuilder.DropTable(
                name: "AndonEscalationRules");

            migrationBuilder.DropTable(
                name: "MachineEvents");

            migrationBuilder.DropTable(
                name: "MachineStateHistory");

            migrationBuilder.DropTable(
                name: "MachineTags");

            migrationBuilder.DropTable(
                name: "ProcessValues");
        }
    }
}
