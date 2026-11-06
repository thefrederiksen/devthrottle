using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <summary>
    /// Mission: Resource Monitor, section 6: the four resource summary tables - five-minute machine and session windows
    /// (kept 90 days) and their hourly roll-ups (kept two years). ONLY ADDS TABLES: nothing existing is touched. Moves
    /// <c>PRAGMA user_version</c> from 7 to 8 in its Up() and back to 7 in its Down(), per the rule in this folder's
    /// README.md.
    /// </summary>
    public partial class ResourceSummaryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resource_hour",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    director_id = table.Column<string>(type: "TEXT", nullable: false),
                    hour_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    machine_name = table.Column<string>(type: "TEXT", nullable: false),
                    window_count = table.Column<int>(type: "INTEGER", nullable: false),
                    refused_windows = table.Column<int>(type: "INTEGER", nullable: false),
                    sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    rolled_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    processor_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    processor_average_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    memory_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    crossings_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_hour", x => new { x.tenant, x.director_id, x.hour_start_utc });
                });

            migrationBuilder.CreateTable(
                name: "resource_session_hour",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    director_id = table.Column<string>(type: "TEXT", nullable: false),
                    hour_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    machine_name = table.Column<string>(type: "TEXT", nullable: false),
                    factory = table.Column<string>(type: "TEXT", nullable: true),
                    window_count = table.Column<int>(type: "INTEGER", nullable: false),
                    sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    processes_with_unknown_figures = table.Column<int>(type: "INTEGER", nullable: false),
                    rolled_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    processor_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    processor_average_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    memory_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    crossings_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_session_hour", x => new { x.tenant, x.director_id, x.hour_start_utc, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "resource_session_window",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    director_id = table.Column<string>(type: "TEXT", nullable: false),
                    window_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    hour_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    machine_name = table.Column<string>(type: "TEXT", nullable: false),
                    factory = table.Column<string>(type: "TEXT", nullable: true),
                    sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    processes_with_unknown_figures = table.Column<int>(type: "INTEGER", nullable: false),
                    received_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    processor_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    processor_average_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    memory_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    crossings_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_session_window", x => new { x.tenant, x.director_id, x.window_start_utc, x.session_id });
                });

            migrationBuilder.CreateTable(
                name: "resource_window",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    director_id = table.Column<string>(type: "TEXT", nullable: false),
                    window_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    hour_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    machine_name = table.Column<string>(type: "TEXT", nullable: false),
                    window_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    received_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    refused_reason = table.Column<string>(type: "TEXT", nullable: true),
                    processor_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    processor_average_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "REAL", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    memory_samples = table.Column<int>(type: "INTEGER", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "TEXT", nullable: true),
                    crossings_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_window", x => new { x.tenant, x.director_id, x.window_start_utc });
                });

            migrationBuilder.CreateIndex(
                name: "ix_resource_hour_tenant_hour",
                table: "resource_hour",
                columns: new[] { "tenant", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_session_hour_tenant_hour",
                table: "resource_session_hour",
                columns: new[] { "tenant", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_session_window_tenant_director_hour",
                table: "resource_session_window",
                columns: new[] { "tenant", "director_id", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_session_window_tenant_window",
                table: "resource_session_window",
                columns: new[] { "tenant", "window_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_window_tenant_director_hour",
                table: "resource_window",
                columns: new[] { "tenant", "director_id", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_window_tenant_window",
                table: "resource_window",
                columns: new[] { "tenant", "window_start_utc" });

            migrationBuilder.Sql("PRAGMA user_version = 8");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_hour");

            migrationBuilder.DropTable(
                name: "resource_session_hour");

            migrationBuilder.DropTable(
                name: "resource_session_window");

            migrationBuilder.DropTable(
                name: "resource_window");

            migrationBuilder.Sql("PRAGMA user_version = 7");
        }
    }
}
