using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_bill_charges",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    team_id = table.Column<string>(type: "TEXT", nullable: false),
                    period_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    period_end_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    seats = table.Column<int>(type: "INTEGER", nullable: false),
                    price_per_seat_cents = table.Column<int>(type: "INTEGER", nullable: false),
                    amount_cents = table.Column<int>(type: "INTEGER", nullable: false),
                    charged_cents = table.Column<int>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_bill_charges", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_bill_charges_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_bills",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    seats = table.Column<int>(type: "INTEGER", nullable: false),
                    price_per_seat_cents = table.Column<int>(type: "INTEGER", nullable: false),
                    plan_started_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    current_period_start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    current_period_end_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    auto_renew = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_bills", x => x.team_id);
                    table.ForeignKey(
                        name: "FK_team_bills_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_bill_charges_team_id_period_start_utc",
                table: "team_bill_charges",
                columns: new[] { "team_id", "period_start_utc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_bills_status_current_period_end_utc",
                table: "team_bills",
                columns: new[] { "status", "current_period_end_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_bill_charges");

            migrationBuilder.DropTable(
                name: "team_bills");
        }
    }
}
