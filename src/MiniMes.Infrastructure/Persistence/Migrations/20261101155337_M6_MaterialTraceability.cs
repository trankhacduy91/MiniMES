using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_MaterialTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IqcRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    SupplierLot = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    LotCount = table.Column<int>(type: "int", nullable: false),
                    SampleQty = table.Column<int>(type: "int", nullable: false),
                    DefectQty = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    DefectCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InspectedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    InspectedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IqcRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IqcRecords_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialLots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RemainingQty = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    SupplierLot = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialLots", x => x.Id);
                    table.CheckConstraint("CK_MaterialLots_Qty", "[Qty] > 0 AND [RemainingQty] >= 0 AND [RemainingQty] <= [Qty]");
                    table.ForeignKey(
                        name: "FK_MaterialLots_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialVerificationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    ScannedLotNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: true),
                    ItemId = table.Column<int>(type: "int", nullable: true),
                    WorkOrderId = table.Column<int>(type: "int", nullable: true),
                    Outcome = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SuggestedLotNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    By = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OverrideBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    OverrideReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialVerificationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LotGenealogy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentLotId = table.Column<int>(type: "int", nullable: false),
                    ChildLotId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotGenealogy", x => x.Id);
                    table.CheckConstraint("CK_LotGenealogy_NotSelf", "[ParentLotId] <> [ChildLotId]");
                    table.ForeignKey(
                        name: "FK_LotGenealogy_MaterialLots_ChildLotId",
                        column: x => x.ChildLotId,
                        principalTable: "MaterialLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LotGenealogy_MaterialLots_ParentLotId",
                        column: x => x.ParentLotId,
                        principalTable: "MaterialLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialLoads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    SlotNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    LoadedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    LoadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FifoOverrideBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    FifoOverrideReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UnloadedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnloadedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialLoads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialLoads_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialLoads_MaterialLots_LotId",
                        column: x => x.LotId,
                        principalTable: "MaterialLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialLoads_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialLoads_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SerialComponents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SerialId = table.Column<long>(type: "bigint", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    ScanLogId = table.Column<long>(type: "bigint", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerialComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SerialComponents_MaterialLots_LotId",
                        column: x => x.LotId,
                        principalTable: "MaterialLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IqcRecords_ItemId_SupplierLot",
                table: "IqcRecords",
                columns: new[] { "ItemId", "SupplierLot" });

            migrationBuilder.CreateIndex(
                name: "IX_LotGenealogy_ChildLotId",
                table: "LotGenealogy",
                column: "ChildLotId")
                .Annotation("SqlServer:Include", new[] { "ParentLotId", "Qty" });

            migrationBuilder.CreateIndex(
                name: "IX_LotGenealogy_ParentLotId_ChildLotId",
                table: "LotGenealogy",
                columns: new[] { "ParentLotId", "ChildLotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialLoads_ItemId",
                table: "MaterialLoads",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialLoads_LotId_LoadedAt",
                table: "MaterialLoads",
                columns: new[] { "LotId", "LoadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialLoads_WorkOrderId",
                table: "MaterialLoads",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "UX_MaterialLoads_ActivePerStationItem",
                table: "MaterialLoads",
                columns: new[] { "StationId", "ItemId" },
                unique: true,
                filter: "[UnloadedAt] IS NULL")
                .Annotation("SqlServer:Include", new[] { "LotId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialLots_ItemId_Status_ReceivedAt",
                table: "MaterialLots",
                columns: new[] { "ItemId", "Status", "ReceivedAt" })
                .Annotation("SqlServer:Include", new[] { "RemainingQty", "ExpiryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialLots_LotNo",
                table: "MaterialLots",
                column: "LotNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialLots_SupplierLot_ItemId",
                table: "MaterialLots",
                columns: new[] { "SupplierLot", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialVerificationLogs_StationId_At",
                table: "MaterialVerificationLogs",
                columns: new[] { "StationId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SerialComponents_LotId",
                table: "SerialComponents",
                column: "LotId")
                .Annotation("SqlServer:Include", new[] { "SerialId" });

            migrationBuilder.CreateIndex(
                name: "UX_SerialComponents_SerialStationItemLot",
                table: "SerialComponents",
                columns: new[] { "SerialId", "StationId", "ItemId", "LotId" },
                unique: true);

            // Spec M6 R11/R16: trace evidence is insert-only, like ScanLogs (ADR-004). A wrong row is answered with
            // an NCR (M7), never by editing it.
            foreach (var table in InsertOnlyTables)
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

        private static readonly string[] InsertOnlyTables = ["SerialComponents", "LotGenealogy", "MaterialVerificationLogs", "IqcRecords"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in InsertOnlyTables)
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS dbo.TR_{table}_BlockModify;");

            migrationBuilder.DropTable(
                name: "IqcRecords");

            migrationBuilder.DropTable(
                name: "LotGenealogy");

            migrationBuilder.DropTable(
                name: "MaterialLoads");

            migrationBuilder.DropTable(
                name: "MaterialVerificationLogs");

            migrationBuilder.DropTable(
                name: "SerialComponents");

            migrationBuilder.DropTable(
                name: "MaterialLots");
        }
    }
}
