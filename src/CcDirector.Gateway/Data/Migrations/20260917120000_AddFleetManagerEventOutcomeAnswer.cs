using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerEventOutcomeAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutcomeId",
                table: "fleet_manager_events",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeTitle",
                table: "fleet_manager_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Words",
                table: "fleet_manager_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_events_tenant_id_OutcomeId",
                table: "fleet_manager_events",
                columns: new[] { "tenant_id", "OutcomeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fleet_manager_events_tenant_id_OutcomeId",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "OutcomeId",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "OutcomeTitle",
                table: "fleet_manager_events");

            migrationBuilder.DropColumn(
                name: "Words",
                table: "fleet_manager_events");
        }
    }
}
