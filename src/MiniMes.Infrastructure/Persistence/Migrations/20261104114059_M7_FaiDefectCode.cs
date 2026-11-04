using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M7_FaiDefectCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefectCode",
                table: "FaiRecords",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FaiRecords_DefectOnFail",
                table: "FaiRecords",
                sql: "([Result] = 'Fail' AND [DefectCode] IS NOT NULL) OR ([Result] = 'Pass' AND [DefectCode] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FaiRecords_DefectOnFail",
                table: "FaiRecords");

            migrationBuilder.DropColumn(
                name: "DefectCode",
                table: "FaiRecords");
        }
    }
}
