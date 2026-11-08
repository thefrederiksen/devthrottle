using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <summary>
    /// Disk Space on the Meter (7 November 2026): the drives each five-minute window reported, as a new
    /// <c>drives_json</c> column on <c>resource_window</c> that fills every existing row - and every row from an older
    /// Director - with an empty list, and the <c>resource_disk_notice</c> table that remembers where each drive stands
    /// for the owner's disk space notices. ADDS ONLY: nothing existing is changed or dropped. Moves
    /// <c>PRAGMA user_version</c> from 8 to 9 in its Up() and back to 8 in its Down(), per the rule in this folder's
    /// README.md.
    /// </summary>
    public partial class ResourceDiskSpace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "drives_json",
                table: "resource_window",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "resource_disk_notice",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    machine_name = table.Column<string>(type: "TEXT", nullable: false),
                    drive = table.Column<string>(type: "TEXT", nullable: false),
                    is_red = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_red_notice_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    red_spell_noticed = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_window_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_disk_notice", x => new { x.tenant, x.machine_name, x.drive });
                });

            migrationBuilder.Sql("PRAGMA user_version = 9");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_disk_notice");

            migrationBuilder.DropColumn(
                name: "drives_json",
                table: "resource_window");

            migrationBuilder.Sql("PRAGMA user_version = 8");
        }
    }
}
