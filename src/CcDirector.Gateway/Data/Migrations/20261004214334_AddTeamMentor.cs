using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamMentor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PersonSubject",
                table: "session_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "team_mentor_blocks",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Week = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    PersonSubject = table.Column<string>(type: "TEXT", nullable: false),
                    Tone = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    WorkedOn = table.Column<string>(type: "TEXT", nullable: false),
                    HowItWent = table.Column<string>(type: "TEXT", nullable: true),
                    WentBadlyAndWhy = table.Column<string>(type: "TEXT", nullable: true),
                    OneThingToTry = table.Column<string>(type: "TEXT", nullable: false),
                    QuotesJson = table.Column<string>(type: "TEXT", nullable: false),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_mentor_blocks", x => new { x.tenant_id, x.Week, x.PersonSubject });
                });

            migrationBuilder.CreateTable(
                name: "team_mentor_outcomes",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Week = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    PersonSubject = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_mentor_outcomes", x => new { x.tenant_id, x.Week, x.PersonSubject });
                });

            migrationBuilder.CreateTable(
                name: "team_mentor_runs",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Week = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RanAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BlocksWritten = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_mentor_runs", x => new { x.tenant_id, x.Week });
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_mentor_blocks_tenant_id",
                table: "team_mentor_blocks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_mentor_outcomes_tenant_id",
                table: "team_mentor_outcomes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_mentor_runs_tenant_id",
                table: "team_mentor_runs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_mentor_blocks");

            migrationBuilder.DropTable(
                name: "team_mentor_outcomes");

            migrationBuilder.DropTable(
                name: "team_mentor_runs");

            migrationBuilder.DropColumn(
                name: "PersonSubject",
                table: "session_history");
        }
    }
}
