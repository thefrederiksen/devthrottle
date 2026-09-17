using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_manager_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", nullable: false),
                    AddressedTo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Crashed = table.Column<bool>(type: "INTEGER", nullable: true),
                    VerdictId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    VerdictJson = table.Column<string>(type: "TEXT", nullable: true),
                    NoVerdictReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveredTo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DeliveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_manager_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id",
                table: "fleet_manager_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id_AcknowledgedAtUtc_CreatedAtUtc",
                table: "fleet_manager_events",
                columns: new[] { "tenant_id", "AcknowledgedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id_SessionId",
                table: "fleet_manager_events",
                columns: new[] { "tenant_id", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_manager_events");
        }
    }
}
