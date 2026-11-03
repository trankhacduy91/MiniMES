using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3b_PackingLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabelPrintLogs_SerialId_PrintNo",
                table: "LabelPrintLogs");

            migrationBuilder.AlterColumn<int>(
                name: "WorkOrderId",
                table: "LabelPrintLogs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "SerialNo",
                table: "LabelPrintLogs",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(30)",
                oldUnicode: false,
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<long>(
                name: "SerialId",
                table: "LabelPrintLogs",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "LabelType",
                table: "LabelPrintLogs",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetKey",
                table: "LabelPrintLogs",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            // Every row so far is an M2 serial label: give it its type and key before the unique index and CHECK.
            migrationBuilder.Sql("UPDATE dbo.LabelPrintLogs SET LabelType = 'Serial', TargetKey = SerialNo;");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintLogs_LabelType_TargetKey_PrintNo",
                table: "LabelPrintLogs",
                columns: new[] { "LabelType", "TargetKey", "PrintNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintLogs_SerialId",
                table: "LabelPrintLogs",
                column: "SerialId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LabelPrintLogs_SerialTarget",
                table: "LabelPrintLogs",
                sql: "([LabelType] = 'Serial' AND [SerialId] IS NOT NULL AND [SerialNo] = [TargetKey] AND [WorkOrderId] IS NOT NULL) OR ([LabelType] <> 'Serial' AND [SerialId] IS NULL AND [SerialNo] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabelPrintLogs_LabelType_TargetKey_PrintNo",
                table: "LabelPrintLogs");

            migrationBuilder.DropIndex(
                name: "IX_LabelPrintLogs_SerialId",
                table: "LabelPrintLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LabelPrintLogs_SerialTarget",
                table: "LabelPrintLogs");

            // The M2 table only knew serial labels: carton and pallet print history is lost on the way down.
            migrationBuilder.Sql("DELETE FROM dbo.LabelPrintLogs WHERE LabelType <> 'Serial';");

            migrationBuilder.DropColumn(
                name: "LabelType",
                table: "LabelPrintLogs");

            migrationBuilder.DropColumn(
                name: "TargetKey",
                table: "LabelPrintLogs");

            migrationBuilder.AlterColumn<int>(
                name: "WorkOrderId",
                table: "LabelPrintLogs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SerialNo",
                table: "LabelPrintLogs",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "varchar(30)",
                oldUnicode: false,
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "SerialId",
                table: "LabelPrintLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintLogs_SerialId_PrintNo",
                table: "LabelPrintLogs",
                columns: new[] { "SerialId", "PrintNo" },
                unique: true);
        }
    }
}
