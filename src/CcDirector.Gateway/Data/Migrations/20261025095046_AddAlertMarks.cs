using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_marks",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    AlertId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SeenVersionUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClearedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TouchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_marks", x => new { x.tenant_id, x.AlertId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_marks_tenant_id",
                table: "alert_marks",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_marks");
        }
    }
}
