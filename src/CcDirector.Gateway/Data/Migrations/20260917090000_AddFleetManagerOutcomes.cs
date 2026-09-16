using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FiledBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AboutSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AnsweredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AnswerText = table.Column<string>(type: "TEXT", nullable: true),
                    AnsweredBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AnswerMatchedOption = table.Column<bool>(type: "INTEGER", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_outcomes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_preferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_outcomes_tenant_id",
                table: "fleet_outcomes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_outcomes_tenant_id_Status_CreatedAtUtc",
                table: "fleet_outcomes",
                columns: new[] { "tenant_id", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_preferences_tenant_id",
                table: "fleet_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_preferences_tenant_id_CreatedAtUtc",
                table: "fleet_preferences",
                columns: new[] { "tenant_id", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_outcomes");

            migrationBuilder.DropTable(
                name: "fleet_preferences");
        }
    }
}
