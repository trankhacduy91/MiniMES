using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M1_VersionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_Routings_OneDraftPerItem",
                table: "Routings",
                column: "ItemId",
                unique: true,
                filter: "[Status] = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "UX_Routings_OneOpenApprovedPerItem",
                table: "Routings",
                column: "ItemId",
                unique: true,
                filter: "[Status] = 'Approved' AND [EffectiveTo] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Boms_OneDraftPerItem",
                table: "Boms",
                column: "ItemId",
                unique: true,
                filter: "[Status] = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "UX_Boms_OneOpenApprovedPerItem",
                table: "Boms",
                column: "ItemId",
                unique: true,
                filter: "[Status] = 'Approved' AND [EffectiveTo] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Routings_OneDraftPerItem",
                table: "Routings");

            migrationBuilder.DropIndex(
                name: "UX_Routings_OneOpenApprovedPerItem",
                table: "Routings");

            migrationBuilder.DropIndex(
                name: "UX_Boms_OneDraftPerItem",
                table: "Boms");

            migrationBuilder.DropIndex(
                name: "UX_Boms_OneOpenApprovedPerItem",
                table: "Boms");
        }
    }
}
