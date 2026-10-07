using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_bill_charges",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: false),
                    period_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    period_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    price_per_seat_cents = table.Column<int>(type: "integer", nullable: false),
                    amount_cents = table.Column<int>(type: "integer", nullable: false),
                    charged_cents = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_bill_charges", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_bill_charges_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "gateway",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_bills",
                schema: "gateway",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    price_per_seat_cents = table.Column<int>(type: "integer", nullable: false),
                    plan_started_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_period_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    current_period_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    auto_renew = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_bills", x => x.team_id);
                    table.ForeignKey(
                        name: "FK_team_bills_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "gateway",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_bill_charges_team_id_period_start_utc",
                schema: "gateway",
                table: "team_bill_charges",
                columns: new[] { "team_id", "period_start_utc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_bills_status_current_period_end_utc",
                schema: "gateway",
                table: "team_bills",
                columns: new[] { "status", "current_period_end_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_bill_charges",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "team_bills",
                schema: "gateway");
        }
    }
}
