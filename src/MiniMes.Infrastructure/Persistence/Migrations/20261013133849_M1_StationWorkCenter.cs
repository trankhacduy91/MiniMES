using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M1_StationWorkCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkCenterId",
                table: "Stations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stations_WorkCenterId",
                table: "Stations",
                column: "WorkCenterId");

            migrationBuilder.AddForeignKey(
                name: "FK_Stations_WorkCenters_WorkCenterId",
                table: "Stations",
                column: "WorkCenterId",
                principalTable: "WorkCenters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Stations_WorkCenters_WorkCenterId",
                table: "Stations");

            migrationBuilder.DropIndex(
                name: "IX_Stations_WorkCenterId",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "WorkCenterId",
                table: "Stations");
        }
    }
}
