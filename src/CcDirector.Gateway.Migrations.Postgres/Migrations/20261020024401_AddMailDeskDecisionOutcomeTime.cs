using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDeskDecisionOutcomeTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mail Desk phase 4, section 2.3 (review of 4274, finding 2): when a decision's outcome was written, so the
            // 08:00 email counts a move by the time it was filed. Phase 3 wrote no outcome, so every earlier row is null.
            migrationBuilder.AddColumn<DateTime>(
                name: "OutcomeUtc",
                schema: "gateway",
                table: "mail_desk_decisions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutcomeUtc",
                schema: "gateway",
                table: "mail_desk_decisions");
        }
    }
}
