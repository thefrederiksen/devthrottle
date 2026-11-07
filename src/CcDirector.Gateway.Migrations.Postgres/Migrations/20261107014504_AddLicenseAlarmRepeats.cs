using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseAlarmRepeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastToldAtUtc",
                schema: "gateway",
                table: "license_alarms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                schema: "gateway",
                table: "license_alarms",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tone",
                schema: "gateway",
                table: "license_alarms",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "warning");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastToldAtUtc",
                schema: "gateway",
                table: "license_alarms");

            migrationBuilder.DropColumn(
                name: "Subject",
                schema: "gateway",
                table: "license_alarms");

            migrationBuilder.DropColumn(
                name: "Tone",
                schema: "gateway",
                table: "license_alarms");
        }
    }
}
