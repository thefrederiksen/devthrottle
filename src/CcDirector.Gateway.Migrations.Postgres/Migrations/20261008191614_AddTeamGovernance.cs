using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_governance",
                schema: "gateway",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "text", nullable: false),
                    agent_reviews_pull_requests = table.Column<bool>(type: "boolean", nullable: false),
                    no_self_merge = table.Column<bool>(type: "boolean", nullable: false),
                    work_starts_as_assigned_issue = table.Column<bool>(type: "boolean", nullable: false),
                    allow_claude_code = table.Column<bool>(type: "boolean", nullable: false),
                    allow_codex = table.Column<bool>(type: "boolean", nullable: false),
                    allow_other_agents = table.Column<bool>(type: "boolean", nullable: false),
                    agent_hours_per_week = table.Column<int>(type: "integer", nullable: true),
                    sessions_at_once = table.Column<int>(type: "integer", nullable: true),
                    keep_mentor_pages_months = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_governance", x => x.team_id);
                    table.ForeignKey(
                        name: "FK_team_governance_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "gateway",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_governance_changes",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    what = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_governance_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_governance_changes_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "gateway",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_governance_items",
                schema: "gateway",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    item_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_governance_items", x => new { x.team_id, x.kind, x.item_id });
                    table.ForeignKey(
                        name: "FK_team_governance_items_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "gateway",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_governance_changes_team_id_created_at_utc",
                schema: "gateway",
                table: "team_governance_changes",
                columns: new[] { "team_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_governance",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "team_governance_changes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "team_governance_items",
                schema: "gateway");
        }
    }
}
