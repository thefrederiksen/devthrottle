using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDueChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                schema: "gateway",
                table: "trigger_runs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedReason",
                schema: "gateway",
                table: "trigger_runs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                schema: "gateway",
                table: "trigger_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckCount",
                schema: "gateway",
                table: "cron_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckedUtc",
                schema: "gateway",
                table: "cron_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByDirectorId",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUtc",
                schema: "gateway",
                table: "cron_runs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                schema: "gateway",
                table: "trigger_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedReason",
                schema: "gateway",
                table: "trigger_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                schema: "gateway",
                table: "trigger_runs");

            migrationBuilder.DropColumn(
                name: "CheckCount",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "CheckedUtc",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ClaimedByDirectorId",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ClaimedUtc",
                schema: "gateway",
                table: "cron_runs");
        }
    }
}
