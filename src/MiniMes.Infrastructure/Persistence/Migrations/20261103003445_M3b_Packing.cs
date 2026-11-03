using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3b_Packing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "CartonSeq");

            migrationBuilder.CreateSequence(
                name: "PalletSeq");

            migrationBuilder.CreateTable(
                name: "PackingSpecs",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    QtyPerCarton = table.Column<int>(type: "int", nullable: false),
                    CartonsPerPallet = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackingSpecs", x => x.ItemId);
                    table.CheckConstraint("CK_PackingSpecs_Positive", "[QtyPerCarton] > 0 AND [CartonsPerPallet] > 0");
                    table.ForeignKey(
                        name: "FK_PackingSpecs_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Pallets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PalletNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    CartonCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ShippedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ShippedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ShipmentRef = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pallets", x => x.Id);
                    table.CheckConstraint("CK_Pallets_CartonCount", "[CartonCount] >= 0");
                    table.ForeignKey(
                        name: "FK_Pallets_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Pallets_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cartons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CartonNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    StationId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    IsPartial = table.Column<bool>(type: "bit", nullable: false),
                    PalletId = table.Column<int>(type: "int", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cartons", x => x.Id);
                    table.CheckConstraint("CK_Cartons_Qty", "[Qty] >= 0");
                    table.ForeignKey(
                        name: "FK_Cartons_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cartons_Pallets_PalletId",
                        column: x => x.PalletId,
                        principalTable: "Pallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cartons_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cartons_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CartonSerials",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CartonId = table.Column<int>(type: "int", nullable: false),
                    SerialId = table.Column<long>(type: "bigint", nullable: false),
                    PackedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PackedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    UnpackedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnpackedBy = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    UnpackReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CartonSerials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CartonSerials_Cartons_CartonId",
                        column: x => x.CartonId,
                        principalTable: "Cartons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CartonSerials_Serials_SerialId",
                        column: x => x.SerialId,
                        principalTable: "Serials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cartons_CartonNo",
                table: "Cartons",
                column: "CartonNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cartons_ItemId",
                table: "Cartons",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Cartons_PalletId",
                table: "Cartons",
                column: "PalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Cartons_WorkOrderId_Status",
                table: "Cartons",
                columns: new[] { "WorkOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Cartons_OpenPerStationWo",
                table: "Cartons",
                columns: new[] { "StationId", "WorkOrderId" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_CartonSerials_CartonId_UnpackedAt",
                table: "CartonSerials",
                columns: new[] { "CartonId", "UnpackedAt" })
                .Annotation("SqlServer:Include", new[] { "SerialId" });

            migrationBuilder.CreateIndex(
                name: "UX_CartonSerials_LivePerSerial",
                table: "CartonSerials",
                column: "SerialId",
                unique: true,
                filter: "[UnpackedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Pallets_ItemId",
                table: "Pallets",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Pallets_PalletNo",
                table: "Pallets",
                column: "PalletNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Pallets_OpenPerStationItem",
                table: "Pallets",
                columns: new[] { "StationId", "ItemId" },
                unique: true,
                filter: "[Status] = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CartonSerials");

            migrationBuilder.DropTable(
                name: "PackingSpecs");

            migrationBuilder.DropTable(
                name: "Cartons");

            migrationBuilder.DropTable(
                name: "Pallets");

            migrationBuilder.DropSequence(
                name: "CartonSeq");

            migrationBuilder.DropSequence(
                name: "PalletSeq");
        }
    }
}
