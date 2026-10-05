using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedByOwnerAtUtc",
                table: "fleet_preferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "fleet_preferences",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "preference");

            migrationBuilder.AddColumn<string>(
                name: "Mistake",
                table: "fleet_preferences",
                type: "TEXT",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmedByOwnerAtUtc",
                table: "fleet_preferences");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "fleet_preferences");

            migrationBuilder.DropColumn(
                name: "Mistake",
                table: "fleet_preferences");
        }
    }
}
