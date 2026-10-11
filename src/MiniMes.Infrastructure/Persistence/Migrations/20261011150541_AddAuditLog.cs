using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RecordKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_ChangedAt",
                table: "AuditLog",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TableName_RecordKey",
                table: "AuditLog",
                columns: new[] { "TableName", "RecordKey" });

            // AuditLog is append-only at DB level: blocks UPDATE/DELETE even for `sa` (sa ignores DENY).
            // Limits: TRUNCATE and DISABLE TRIGGER by db_owner/sysadmin still work (see M0 spec 2.7).
            migrationBuilder.Sql("""
                CREATE TRIGGER dbo.TR_AuditLog_BlockModify ON dbo.AuditLog
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'AuditLog is append-only. UPDATE/DELETE is not allowed.', 1;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_AuditLog_BlockModify;");

            migrationBuilder.DropTable(
                name: "AuditLog");
        }
    }
}
