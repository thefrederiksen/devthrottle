using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamMentor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PersonSubject",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "team_mentor_blocks",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Week = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    PersonSubject = table.Column<string>(type: "text", nullable: false),
                    Tone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WorkedOn = table.Column<string>(type: "text", nullable: false),
                    HowItWent = table.Column<string>(type: "text", nullable: true),
                    WentBadlyAndWhy = table.Column<string>(type: "text", nullable: true),
                    OneThingToTry = table.Column<string>(type: "text", nullable: false),
                    QuotesJson = table.Column<string>(type: "text", nullable: false),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_mentor_blocks", x => new { x.tenant_id, x.Week, x.PersonSubject });
                });

            migrationBuilder.CreateTable(
                name: "team_mentor_outcomes",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Week = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    PersonSubject = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_mentor_outcomes", x => new { x.tenant_id, x.Week, x.PersonSubject });
                });

            migrationBuilder.CreateTable(
                name: "team_mentor_runs",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Week = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RanAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BlocksWritten = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_mentor_runs", x => new { x.tenant_id, x.Week });
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_mentor_blocks_tenant_id",
                schema: "gateway",
                table: "team_mentor_blocks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_mentor_outcomes_tenant_id",
                schema: "gateway",
                table: "team_mentor_outcomes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_mentor_runs_tenant_id",
                schema: "gateway",
                table: "team_mentor_runs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_mentor_blocks",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "team_mentor_outcomes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "team_mentor_runs",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "PersonSubject",
                schema: "gateway",
                table: "session_history");
        }
    }
}
