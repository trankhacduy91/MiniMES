using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_LotSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "LotSeq");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSequence(
                name: "LotSeq");
        }
    }
}
