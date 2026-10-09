using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_governance",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "TEXT", nullable: false),
                    agent_reviews_pull_requests = table.Column<bool>(type: "INTEGER", nullable: false),
                    no_self_merge = table.Column<bool>(type: "INTEGER", nullable: false),
                    work_starts_as_assigned_issue = table.Column<bool>(type: "INTEGER", nullable: false),
                    allow_claude_code = table.Column<bool>(type: "INTEGER", nullable: false),
                    allow_codex = table.Column<bool>(type: "INTEGER", nullable: false),
                    allow_other_agents = table.Column<bool>(type: "INTEGER", nullable: false),
                    agent_hours_per_week = table.Column<int>(type: "INTEGER", nullable: true),
                    sessions_at_once = table.Column<int>(type: "INTEGER", nullable: true),
                    keep_mentor_pages_months = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_governance", x => x.team_id);
                    table.ForeignKey(
                        name: "FK_team_governance_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_governance_changes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    team_id = table.Column<string>(type: "TEXT", nullable: false),
                    changed_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    what = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_governance_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_governance_changes_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_governance_items",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    item_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_governance_items", x => new { x.team_id, x.kind, x.item_id });
                    table.ForeignKey(
                        name: "FK_team_governance_items_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_governance_changes_team_id_created_at_utc",
                table: "team_governance_changes",
                columns: new[] { "team_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_governance");

            migrationBuilder.DropTable(
                name: "team_governance_changes");

            migrationBuilder.DropTable(
                name: "team_governance_items");
        }
    }
}
