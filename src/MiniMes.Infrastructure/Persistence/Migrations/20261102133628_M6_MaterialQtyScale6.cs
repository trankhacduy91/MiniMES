using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_MaterialQtyScale6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server refuses ALTER COLUMN on a column a CHECK constraint uses: drop it, alter, add it back.
            migrationBuilder.DropCheckConstraint(name: "CK_MaterialLots_Qty", table: "MaterialLots");

            migrationBuilder.AlterColumn<decimal>(
                name: "Qty",
                table: "SerialComponents",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "RemainingQty",
                table: "MaterialLots",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Qty",
                table: "MaterialLots",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "Qty",
                table: "LotGenealogy",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AddCheckConstraint(name: "CK_MaterialLots_Qty", table: "MaterialLots",
                sql: "[Qty] > 0 AND [RemainingQty] >= 0 AND [RemainingQty] <= [Qty]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQL Server refuses ALTER COLUMN on a column a CHECK constraint uses: drop it, alter, add it back.
            migrationBuilder.DropCheckConstraint(name: "CK_MaterialLots_Qty", table: "MaterialLots");

            migrationBuilder.AlterColumn<decimal>(
                name: "Qty",
                table: "SerialComponents",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "RemainingQty",
                table: "MaterialLots",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "Qty",
                table: "MaterialLots",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "Qty",
                table: "LotGenealogy",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AddCheckConstraint(name: "CK_MaterialLots_Qty", table: "MaterialLots",
                sql: "[Qty] > 0 AND [RemainingQty] >= 0 AND [RemainingQty] <= [Qty]");
        }
    }
}
