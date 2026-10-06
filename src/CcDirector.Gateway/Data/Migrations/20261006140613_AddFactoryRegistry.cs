using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_goal_numbers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AsOf = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Link = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    PostedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PostedBySession = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_goal_numbers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_registry",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Computer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CeoSeat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GoalText = table.Column<string>(type: "TEXT", nullable: true),
                    GoalFile = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    GoalApprovedOn = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    SeatsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_registry", x => new { x.tenant_id, x.Factory });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_goal_numbers_tenant_id",
                table: "factory_goal_numbers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_goal_numbers_tenant_id_Factory_PostedAtUtc",
                table: "factory_goal_numbers",
                columns: new[] { "tenant_id", "Factory", "PostedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_registry_tenant_id",
                table: "factory_registry",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_goal_numbers");

            migrationBuilder.DropTable(
                name: "factory_registry");
        }
    }
}
