using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamMembershipEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_membership_events",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: false),
                    account_subject = table.Column<string>(type: "text", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(30)", maxLength: 30, nullable: false),
                    from_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    by_subject = table.Column<string>(type: "text", nullable: true),
                    at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_membership_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_membership_events_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "gateway",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_membership_events_team_id_account_subject",
                schema: "gateway",
                table: "team_membership_events",
                columns: new[] { "team_id", "account_subject" });

            migrationBuilder.CreateIndex(
                name: "IX_team_membership_events_team_id_at_utc",
                schema: "gateway",
                table: "team_membership_events",
                columns: new[] { "team_id", "at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_membership_events",
                schema: "gateway");
        }
    }
}
