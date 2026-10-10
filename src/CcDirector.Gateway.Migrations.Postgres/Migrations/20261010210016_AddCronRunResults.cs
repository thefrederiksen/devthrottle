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
            migrationBuilder.AddColumn<string>(
                name: "Problem",
                schema: "gateway",
                table: "cron_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProblemActivityId",
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
                name: "IX_cron_runs_Result",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropIndex(
                name: "IX_cron_runs_SessionId",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Problem",
                schema: "gateway",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ProblemActivityId",
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
        }
    }
}
