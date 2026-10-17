using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_WorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "SerialSeq");

            migrationBuilder.CreateSequence(
                name: "WoNoSeq");

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WoNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    PlannedQty = table.Column<int>(type: "int", nullable: false),
                    PlannedStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlannedEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RequireFai = table.Column<bool>(type: "bit", nullable: false),
                    FaiStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FaiSerialNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    BomVersion = table.Column<int>(type: "int", nullable: true),
                    RoutingVersion = table.Column<int>(type: "int", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HoldReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CompleteReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExternalRef = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.Id);
                    table.CheckConstraint("CK_WorkOrders_PlannedQty", "[PlannedQty] > 0");
                    table.CheckConstraint("CK_WorkOrders_PlannedWindow", "[PlannedEnd] > [PlannedStart]");
                    table.CheckConstraint("CK_WorkOrders_Priority", "[Priority] BETWEEN 1 AND 9");
                    table.ForeignKey(
                        name: "FK_WorkOrders_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Lines_LineId",
                        column: x => x.LineId,
                        principalTable: "Lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Serials",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SerialNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    CurrentSeq = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScrapReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Serials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Serials_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Serials_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderMaterials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    ComponentItemId = table.Column<int>(type: "int", nullable: false),
                    OperationSeq = table.Column<int>(type: "int", nullable: false),
                    QtyPerUnit = table.Column<decimal>(type: "decimal(28,9)", precision: 28, scale: 9, nullable: false),
                    RequiredQty = table.Column<decimal>(type: "decimal(28,9)", precision: 28, scale: 9, nullable: false),
                    IssuedQty = table.Column<decimal>(type: "decimal(28,9)", precision: 28, scale: 9, nullable: false),
                    ConsumedQty = table.Column<decimal>(type: "decimal(28,9)", precision: 28, scale: 9, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderMaterials", x => x.Id);
                    table.CheckConstraint("CK_WorkOrderMaterials_QtyPerUnit", "[QtyPerUnit] > 0");
                    table.ForeignKey(
                        name: "FK_WorkOrderMaterials_Items_ComponentItemId",
                        column: x => x.ComponentItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderMaterials_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    OperationCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    WorkCenterId = table.Column<int>(type: "int", nullable: false),
                    IdealCycleTimeSec = table.Column<decimal>(type: "decimal(9,3)", precision: 9, scale: 3, nullable: false),
                    IsInspection = table.Column<bool>(type: "bit", nullable: false),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false),
                    SopUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    QtyGood = table.Column<int>(type: "int", nullable: false),
                    QtyNg = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderOperations", x => x.Id);
                    table.CheckConstraint("CK_WorkOrderOperations_Counters", "[QtyGood] >= 0 AND [QtyNg] >= 0");
                    table.CheckConstraint("CK_WorkOrderOperations_IdealCycleTime", "[IdealCycleTimeSec] > 0");
                    table.ForeignKey(
                        name: "FK_WorkOrderOperations_WorkCenters_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderOperations_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Serials_ItemId",
                table: "Serials",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Serials_SerialNo",
                table: "Serials",
                column: "SerialNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Serials_WorkOrderId_Status",
                table: "Serials",
                columns: new[] { "WorkOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderMaterials_ComponentItemId",
                table: "WorkOrderMaterials",
                column: "ComponentItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderMaterials_WorkOrderId_ComponentItemId_OperationSeq",
                table: "WorkOrderMaterials",
                columns: new[] { "WorkOrderId", "ComponentItemId", "OperationSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderOperations_WorkCenterId",
                table: "WorkOrderOperations",
                column: "WorkCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderOperations_WorkOrderId_Seq",
                table: "WorkOrderOperations",
                columns: new[] { "WorkOrderId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_ItemId",
                table: "WorkOrders",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_LineId_Status_Priority_PlannedStart",
                table: "WorkOrders",
                columns: new[] { "LineId", "Status", "Priority", "PlannedStart" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_WoNo",
                table: "WorkOrders",
                column: "WoNo",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Serials");

            migrationBuilder.DropTable(
                name: "WorkOrderMaterials");

            migrationBuilder.DropTable(
                name: "WorkOrderOperations");

            migrationBuilder.DropTable(
                name: "WorkOrders");

            migrationBuilder.DropSequence(
                name: "SerialSeq");

            migrationBuilder.DropSequence(
                name: "WoNoSeq");
        }
    }
}
