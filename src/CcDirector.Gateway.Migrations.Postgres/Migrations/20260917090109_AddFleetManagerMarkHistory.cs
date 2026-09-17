using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerMarkHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnsweredByRole",
                schema: "gateway",
                table: "fleet_outcomes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fleet_manager_marks",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    FirstMarkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMarkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_manager_marks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_marks_tenant_id",
                schema: "gateway",
                table: "fleet_manager_marks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_marks_tenant_id_SessionId",
                schema: "gateway",
                table: "fleet_manager_marks",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_manager_marks",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "AnsweredByRole",
                schema: "gateway",
                table: "fleet_outcomes");
        }
    }
}
