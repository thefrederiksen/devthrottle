using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDueChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                table: "trigger_runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedReason",
                table: "trigger_runs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                table: "trigger_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckCount",
                table: "cron_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckedUtc",
                table: "cron_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByDirectorId",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUtc",
                table: "cron_runs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                table: "trigger_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedReason",
                table: "trigger_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                table: "trigger_runs");

            migrationBuilder.DropColumn(
                name: "CheckCount",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "CheckedUtc",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ClaimedByDirectorId",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ClaimedUtc",
                table: "cron_runs");
        }
    }
}
