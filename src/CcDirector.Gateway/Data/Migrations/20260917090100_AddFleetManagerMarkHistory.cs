using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetManagerMarkHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnsweredByRole",
                table: "fleet_outcomes",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fleet_manager_marks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstMarkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMarkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_manager_marks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_marks_tenant_id",
                table: "fleet_manager_marks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_manager_marks_tenant_id_SessionId",
                table: "fleet_manager_marks",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_manager_marks");

            migrationBuilder.DropColumn(
                name: "AnsweredByRole",
                table: "fleet_outcomes");
        }
    }
}
