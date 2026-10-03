using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_invitations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    team_id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    invited_by_subject = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    accept_token_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    responded_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    accepted_by_subject = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_invitations", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_invitations_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_invitations_accept_token_hash",
                table: "team_invitations",
                column: "accept_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_invitations_team_id_email",
                table: "team_invitations",
                columns: new[] { "team_id", "email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_invitations");
        }
    }
}
