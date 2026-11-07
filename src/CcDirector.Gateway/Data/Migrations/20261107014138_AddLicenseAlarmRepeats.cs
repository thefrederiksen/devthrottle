using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseAlarmRepeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastToldAtUtc",
                table: "license_alarms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "license_alarms",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tone",
                table: "license_alarms",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "warning");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastToldAtUtc",
                table: "license_alarms");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "license_alarms");

            migrationBuilder.DropColumn(
                name: "Tone",
                table: "license_alarms");
        }
    }
}
