using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCronRunResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ActivityDue",
                table: "cron_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "cron_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Factory",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobName",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MissedNotListed",
                table: "cron_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Problem",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolveActivityId",
                table: "cron_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedReason",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                table: "cron_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Result",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "untracked");

            migrationBuilder.AddColumn<string>(
                name: "ResultReason",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResultUtc",
                table: "cron_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seat",
                table: "cron_runs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_ActivityDue",
                table: "cron_runs",
                column: "ActivityDue");

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_Result",
                table: "cron_runs",
                column: "Result");

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_SessionId",
                table: "cron_runs",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cron_runs_ActivityDue",
                table: "cron_runs");

            migrationBuilder.DropIndex(
                name: "IX_cron_runs_Result",
                table: "cron_runs");

            migrationBuilder.DropIndex(
                name: "IX_cron_runs_SessionId",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ActivityDue",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Factory",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "JobName",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "MissedNotListed",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Problem",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolveActivityId",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedReason",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Result",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResultReason",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "ResultUtc",
                table: "cron_runs");

            migrationBuilder.DropColumn(
                name: "Seat",
                table: "cron_runs");
        }
    }
}
