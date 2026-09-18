using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerEventDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "fleet_manager_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectorId",
                table: "fleet_manager_events",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadingPending",
                table: "fleet_manager_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StopObservedAtUtc",
                table: "fleet_manager_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fleet_manager_owned_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FleetManagerSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FirstSeenAliveUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAliveUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_manager_owned_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_owned_sessions_tenant_id",
                table: "fleet_manager_owned_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_owned_sessions_tenant_id_EndedAtUtc",
                table: "fleet_manager_owned_sessions",
                columns: new[] { "tenant_id", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_owned_sessions_tenant_id_SessionId",
                table: "fleet_manager_owned_sessions",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_manager_owned_sessions");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "DirectorId",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "ReadingPending",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "StopObservedAtUtc",
                table: "fleet_manager_events");
        }
    }
}
