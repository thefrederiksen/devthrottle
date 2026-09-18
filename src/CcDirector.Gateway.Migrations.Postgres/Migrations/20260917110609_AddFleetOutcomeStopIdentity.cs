using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <summary>
    /// The Fleet Manager mission, steps 7 to 9 round 2 fixes: an outcome record stores the stop it was filed about - the
    /// Wingman verdict id and that verdict's turn end - so the walkthrough closes it only with an answer to that stop.
    /// Two nullable columns; no existing row changes, and a record filed before names no stop.
    /// </summary>
    public partial class AddFleetOutcomeStopIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AboutTurnEndObservedAtUtc",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AboutVerdictId",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AboutTurnEndObservedAtUtc",
                schema: "gateway",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "AboutVerdictId",
                schema: "gateway",
                table: "fleet_outcomes");
        }
    }
}
