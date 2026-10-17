using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_LabelPrintLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LabelPrintLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SerialId = table.Column<long>(type: "bigint", nullable: false),
                    SerialNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    PrintNo = table.Column<int>(type: "int", nullable: false),
                    PrintedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PrintedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelPrintLogs", x => x.Id);
                    table.CheckConstraint("CK_LabelPrintLogs_PrintNo", "[PrintNo] >= 1");
                    table.ForeignKey(
                        name: "FK_LabelPrintLogs_Serials_SerialId",
                        column: x => x.SerialId,
                        principalTable: "Serials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LabelPrintLogs_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintLogs_SerialId_PrintNo",
                table: "LabelPrintLogs",
                columns: new[] { "SerialId", "PrintNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintLogs_WorkOrderId",
                table: "LabelPrintLogs",
                column: "WorkOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LabelPrintLogs");
        }
    }
}
