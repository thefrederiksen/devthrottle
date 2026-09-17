using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnVerdictTraceRowAndClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClockDeadlineUtc",
                table: "turn_verdict_traces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowColour",
                table: "turn_verdict_traces",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowLabel",
                table: "turn_verdict_traces",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockDeadlineUtc",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "RowColour",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "RowLabel",
                table: "turn_verdict_traces");
        }
    }
}
