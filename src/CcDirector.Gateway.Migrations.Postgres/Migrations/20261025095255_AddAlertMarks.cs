using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_marks",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    AlertId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    SeenVersionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClearedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TouchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_marks", x => new { x.tenant_id, x.AlertId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_marks_tenant_id",
                schema: "gateway",
                table: "alert_marks",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_marks",
                schema: "gateway");
        }
    }
}
