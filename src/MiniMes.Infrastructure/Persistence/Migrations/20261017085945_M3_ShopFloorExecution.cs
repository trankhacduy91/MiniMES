using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3_ShopFloorExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Serials_WorkOrderId_Status",
                table: "Serials");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Serials",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "RepairLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SerialId = table.Column<long>(type: "bigint", nullable: false),
                    SerialNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    DefectCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ComponentReplaced = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReturnToSeq = table.Column<int>(type: "int", nullable: true),
                    RepairedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    RepairedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairLogs_Serials_SerialId",
                        column: x => x.SerialId,
                        principalTable: "Serials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScanLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SerialNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SerialId = table.Column<long>(type: "bigint", nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    DefectCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    OperatorCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StationSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    OperatorCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StationSessions_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StationSessions_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Serials_WorkOrderId_Status",
                table: "Serials",
                columns: new[] { "WorkOrderId", "Status" })
                .Annotation("SqlServer:Include", new[] { "CurrentSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairLogs_SerialId",
                table: "RepairLogs",
                column: "SerialId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairLogs_SerialNo_RepairedAt",
                table: "RepairLogs",
                columns: new[] { "SerialNo", "RepairedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanLogs_SerialNo_ScannedAt",
                table: "ScanLogs",
                columns: new[] { "SerialNo", "ScannedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanLogs_WorkOrderId",
                table: "ScanLogs",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_StationSessions_WorkOrderId",
                table: "StationSessions",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "UX_StationSessions_OneOpenPerStation",
                table: "StationSessions",
                column: "StationId",
                unique: true,
                filter: "[ClosedAt] IS NULL");

            // Scan and repair logs are evidence: insert-only, like AuditLog (ADR-004). A mistake is corrected
            // with a new row. Even sa cannot UPDATE/DELETE without first dropping the trigger.
            foreach (var table in new[] { "ScanLogs", "RepairLogs" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER dbo.TR_{table}_BlockModify ON dbo.{table}
                    INSTEAD OF UPDATE, DELETE
                    AS
                    BEGIN
                        SET NOCOUNT ON;
                        THROW 51000, '{table} is append-only. UPDATE/DELETE is not allowed.', 1;
                    END;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_ScanLogs_BlockModify;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_RepairLogs_BlockModify;");

            migrationBuilder.DropTable(
                name: "RepairLogs");

            migrationBuilder.DropTable(
                name: "ScanLogs");

            migrationBuilder.DropTable(
                name: "StationSessions");

            migrationBuilder.DropIndex(
                name: "IX_Serials_WorkOrderId_Status",
                table: "Serials");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Serials");

            migrationBuilder.CreateIndex(
                name: "IX_Serials_WorkOrderId_Status",
                table: "Serials",
                columns: new[] { "WorkOrderId", "Status" });
        }
    }
}
