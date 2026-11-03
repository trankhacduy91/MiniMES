using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_RepairReplacedParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "ScanLogId",
                table: "SerialComponents",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "RepairLogId",
                table: "SerialComponents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SerialComponents_OneSource",
                table: "SerialComponents",
                sql: "([ScanLogId] IS NULL AND [RepairLogId] IS NOT NULL) OR ([ScanLogId] IS NOT NULL AND [RepairLogId] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SerialComponents_OneSource",
                table: "SerialComponents");

            migrationBuilder.DropColumn(
                name: "RepairLogId",
                table: "SerialComponents");

            migrationBuilder.Sql("ALTER TABLE [SerialComponents] ALTER COLUMN [ScanLogId] bigint NOT NULL;");
        }
    }
}
