using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_outcomes",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    FiledBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AboutSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    AnsweredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnswerText = table.Column<string>(type: "text", nullable: true),
                    AnsweredBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AnswerMatchedOption = table.Column<bool>(type: "boolean", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_outcomes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_preferences",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_preferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_outcomes_tenant_id",
                schema: "gateway",
                table: "fleet_outcomes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_outcomes_tenant_id_Status_CreatedAtUtc",
                schema: "gateway",
                table: "fleet_outcomes",
                columns: new[] { "tenant_id", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_preferences_tenant_id",
                schema: "gateway",
                table: "fleet_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_preferences_tenant_id_CreatedAtUtc",
                schema: "gateway",
                table: "fleet_preferences",
                columns: new[] { "tenant_id", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_outcomes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "fleet_preferences",
                schema: "gateway");
        }
    }
}
