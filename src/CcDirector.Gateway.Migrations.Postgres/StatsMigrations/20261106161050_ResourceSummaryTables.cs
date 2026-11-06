using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.StatsMigrations
{
    /// <summary>
    /// Mission: Resource Monitor, section 6: the four resource summary tables in the gateway_stats schema, keyed by
    /// tenant, each with the tenant allowlist check every table in this schema carries. ONLY ADDS TABLES.
    /// </summary>
    public partial class ResourceSummaryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resource_hour",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    director_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    hour_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    machine_name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    window_count = table.Column<int>(type: "integer", nullable: false),
                    refused_windows = table.Column<int>(type: "integer", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    rolled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processor_samples = table.Column<int>(type: "integer", nullable: false),
                    processor_average_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    memory_samples = table.Column<int>(type: "integer", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    crossings_json = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_hour", x => new { x.tenant, x.director_id, x.hour_start_utc });
                    table.CheckConstraint("ck_resource_hour_tenant_not_empty", "\"tenant\" ~ '^[a-z0-9-]+$'");
                });

            migrationBuilder.CreateTable(
                name: "resource_session_hour",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    director_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    hour_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    machine_name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    factory = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    window_count = table.Column<int>(type: "integer", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    processes_with_unknown_figures = table.Column<int>(type: "integer", nullable: false),
                    rolled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processor_samples = table.Column<int>(type: "integer", nullable: false),
                    processor_average_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    memory_samples = table.Column<int>(type: "integer", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    crossings_json = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_session_hour", x => new { x.tenant, x.director_id, x.hour_start_utc, x.session_id });
                    table.CheckConstraint("ck_resource_session_hour_tenant_not_empty", "\"tenant\" ~ '^[a-z0-9-]+$'");
                });

            migrationBuilder.CreateTable(
                name: "resource_session_window",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    director_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    hour_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    machine_name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    factory = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    processes_with_unknown_figures = table.Column<int>(type: "integer", nullable: false),
                    received_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processor_samples = table.Column<int>(type: "integer", nullable: false),
                    processor_average_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    memory_samples = table.Column<int>(type: "integer", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    crossings_json = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_session_window", x => new { x.tenant, x.director_id, x.window_start_utc, x.session_id });
                    table.CheckConstraint("ck_resource_session_window_tenant_not_empty", "\"tenant\" ~ '^[a-z0-9-]+$'");
                });

            migrationBuilder.CreateTable(
                name: "resource_window",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    director_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    hour_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    machine_name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    window_seconds = table.Column<int>(type: "integer", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    received_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    refused_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    processor_samples = table.Column<int>(type: "integer", nullable: false),
                    processor_average_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_peak_percent = table.Column<double>(type: "double precision", nullable: true),
                    processor_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    memory_samples = table.Column<int>(type: "integer", nullable: false),
                    memory_average_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_peak_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_unreadable_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    crossings_json = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_window", x => new { x.tenant, x.director_id, x.window_start_utc });
                    table.CheckConstraint("ck_resource_window_tenant_not_empty", "\"tenant\" ~ '^[a-z0-9-]+$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_resource_hour_tenant_hour",
                schema: "gateway_stats",
                table: "resource_hour",
                columns: new[] { "tenant", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_session_hour_tenant_hour",
                schema: "gateway_stats",
                table: "resource_session_hour",
                columns: new[] { "tenant", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_session_window_tenant_director_hour",
                schema: "gateway_stats",
                table: "resource_session_window",
                columns: new[] { "tenant", "director_id", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_session_window_tenant_window",
                schema: "gateway_stats",
                table: "resource_session_window",
                columns: new[] { "tenant", "window_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_window_tenant_director_hour",
                schema: "gateway_stats",
                table: "resource_window",
                columns: new[] { "tenant", "director_id", "hour_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_resource_window_tenant_window",
                schema: "gateway_stats",
                table: "resource_window",
                columns: new[] { "tenant", "window_start_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_hour",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "resource_session_hour",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "resource_session_window",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "resource_window",
                schema: "gateway_stats");
        }
    }
}
