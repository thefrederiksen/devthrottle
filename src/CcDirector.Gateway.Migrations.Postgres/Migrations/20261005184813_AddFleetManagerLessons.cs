using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedByOwnerAtUtc",
                schema: "gateway",
                table: "fleet_preferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "gateway",
                table: "fleet_preferences",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "preference");

            migrationBuilder.AddColumn<string>(
                name: "Mistake",
                schema: "gateway",
                table: "fleet_preferences",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LessonId",
                schema: "gateway",
                table: "fleet_manager_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmedByOwnerAtUtc",
                schema: "gateway",
                table: "fleet_preferences");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "gateway",
                table: "fleet_preferences");

            migrationBuilder.DropColumn(
                name: "Mistake",
                schema: "gateway",
                table: "fleet_preferences");

            migrationBuilder.DropColumn(
                name: "LessonId",
                schema: "gateway",
                table: "fleet_manager_events");
        }
    }
}
