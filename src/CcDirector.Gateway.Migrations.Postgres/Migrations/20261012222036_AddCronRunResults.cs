using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCronRunResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ActivityDue",
                schema: "gateway",
                table: "cron_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                schema: "gateway",
                table: "cron_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Factory",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobName",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MissedNotListed",
                schema: "gateway",
                table: "cron_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Problem",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolveActivityId",
                schema: "gateway",
                table: "cron_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedReason",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                schema: "gateway",
                table: "cron_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Result",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "untracked");

            migrationBuilder.AddColumn<string>(
                name: "ResultReason",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResultUtc",
                schema: "gateway",
                table: "cron_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seat",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_ActivityDue",
                schema: "gateway",
                table: "cron_runs",
                column: "ActivityDue");

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_Result",
                schema: "gateway",
                table: "cron_runs",
                column: "Result");

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_SessionId",
                schema: "gateway",
                table: "cron_runs",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cron_runs_ActivityDue",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropIndex(
                name: "IX_cron_runs_Result",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropIndex(
                name: "IX_cron_runs_SessionId",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ActivityDue",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Factory",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "JobName",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "MissedNotListed",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Problem",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolveActivityId",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedReason",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Result",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResultReason",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResultUtc",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Seat",
                schema: "gateway",
                table: "cron_runs");
        }
    }
}
