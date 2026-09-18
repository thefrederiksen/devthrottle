using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetOutcomeAdvice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Advice",
                table: "fleet_outcomes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AdviceSetAtUtc",
                table: "fleet_outcomes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FleetManagerPick",
                table: "fleet_outcomes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerNote",
                table: "fleet_outcomes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerNoteAtUtc",
                table: "fleet_outcomes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Advice",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "AdviceSetAtUtc",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "FleetManagerPick",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "OwnerNote",
                table: "fleet_outcomes");

            migrationBuilder.DropColumn(
                name: "OwnerNoteAtUtc",
                table: "fleet_outcomes");
        }
    }
}
