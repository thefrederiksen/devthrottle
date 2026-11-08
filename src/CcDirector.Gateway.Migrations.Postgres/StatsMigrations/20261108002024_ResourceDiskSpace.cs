using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.StatsMigrations
{
    /// <summary>
    /// Disk Space on the Meter (7 November 2026): the <c>drives_json</c> column on <c>resource_window</c>, filling every
    /// existing row - and every row from an older Director - with an empty list, and the <c>resource_disk_notice</c>
    /// table in the gateway_stats schema, with the tenant allowlist check every table here carries. ADDS ONLY.
    /// </summary>
    public partial class ResourceDiskSpace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "drives_json",
                schema: "gateway_stats",
                table: "resource_window",
                type: "text",
                nullable: false,
                defaultValue: "[]",
                collation: "C");

            migrationBuilder.CreateTable(
                name: "resource_disk_notice",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    machine_name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    drive = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    is_red = table.Column<bool>(type: "boolean", nullable: false),
                    last_red_notice_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    red_spell_noticed = table.Column<bool>(type: "boolean", nullable: false),
                    last_window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_disk_notice", x => new { x.tenant, x.machine_name, x.drive });
                    table.CheckConstraint("ck_resource_disk_notice_tenant_not_empty", "\"tenant\" ~ '^[a-z0-9-]+$'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_disk_notice",
                schema: "gateway_stats");

            migrationBuilder.DropColumn(
                name: "drives_json",
                schema: "gateway_stats",
                table: "resource_window");
        }
    }
}
