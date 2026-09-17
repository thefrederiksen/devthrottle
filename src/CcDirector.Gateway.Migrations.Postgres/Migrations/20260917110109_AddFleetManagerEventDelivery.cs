using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerEventDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Detail",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectorId",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadingPending",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StopObservedAtUtc",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fleet_manager_owned_sessions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    FleetManagerSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SessionName = table.Column<string>(type: "text", nullable: false),
                    DirectorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FirstSeenAliveUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAliveUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_manager_owned_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_owned_sessions_tenant_id",
                schema: "gateway",
                table: "fleet_manager_owned_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_owned_sessions_tenant_id_EndedAtUtc",
                schema: "gateway",
                table: "fleet_manager_owned_sessions",
                columns: new[] { "tenant_id", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_owned_sessions_tenant_id_SessionId",
                schema: "gateway",
                table: "fleet_manager_owned_sessions",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_manager_owned_sessions",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "Detail",
                schema: "gateway",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "DirectorId",
                schema: "gateway",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "ReadingPending",
                schema: "gateway",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "StopObservedAtUtc",
                schema: "gateway",
                table: "fleet_manager_events");
        }
    }
}
