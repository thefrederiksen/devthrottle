using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetOutcomeAdvice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Advice",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AdviceSetAtUtc",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FleetManagerPick",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerNote",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerNoteAtUtc",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Advice",
                schema: "gateway",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "AdviceSetAtUtc",
                schema: "gateway",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "FleetManagerPick",
                schema: "gateway",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "OwnerNote",
                schema: "gateway",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "OwnerNoteAtUtc",
                schema: "gateway",
                table: "fleet_outcomes");
        }
    }
}
