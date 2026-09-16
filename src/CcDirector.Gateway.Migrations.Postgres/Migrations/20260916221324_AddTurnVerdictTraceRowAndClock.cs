using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnVerdictTraceRowAndClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClockDeadlineUtc",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowColour",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowLabel",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockDeadlineUtc",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "RowColour",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "RowLabel",
                schema: "gateway",
                table: "turn_verdict_traces");
        }
    }
}
