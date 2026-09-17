using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_manager_events",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SessionName = table.Column<string>(type: "text", nullable: false),
                    AddressedTo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Crashed = table.Column<bool>(type: "boolean", nullable: true),
                    VerdictId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    VerdictJson = table.Column<string>(type: "text", nullable: true),
                    NoVerdictReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredTo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    DeliveryCount = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_manager_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id",
                schema: "gateway",
                table: "fleet_manager_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id_AcknowledgedAtUtc_CreatedAtU~",
                schema: "gateway",
                table: "fleet_manager_events",
                columns: new[] { "tenant_id", "AcknowledgedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id_SessionId",
                schema: "gateway",
                table: "fleet_manager_events",
                columns: new[] { "tenant_id", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_manager_events",
                schema: "gateway");
        }
    }
}
