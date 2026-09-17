using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerEventOutcomeAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutcomeId",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "OutcomeTitle",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Words",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id_OutcomeId",
                schema: "gateway",
                table: "fleet_manager_events",
                columns: new[] { "tenant_id", "OutcomeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fleet_manager_events_tenant_id_OutcomeId",
                schema: "gateway",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "OutcomeId",
                schema: "gateway",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "OutcomeTitle",
                schema: "gateway",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "Words",
                schema: "gateway",
                table: "fleet_manager_events");
        }
    }
}
