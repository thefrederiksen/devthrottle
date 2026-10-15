using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryCheckState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckPath",
                table: "triggers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefinitionVersion",
                table: "triggers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seat",
                table: "triggers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretsJson",
                table: "triggers",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckPath",
                table: "cron_jobs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckSecretsJson",
                table: "cron_jobs",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefinitionVersion",
                table: "cron_jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "factory_check_state",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WrittenBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RunRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Reset = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_check_state", x => new { x.tenant_id, x.Factory, x.OwnerKey, x.Sequence });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_check_state_tenant_id",
                table: "factory_check_state",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_check_state");

            migrationBuilder.DropColumn(
                name: "CheckPath",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "DefinitionVersion",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "Seat",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "SecretsJson",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "CheckPath",
                table: "cron_jobs");

            migrationBuilder.DropColumn(
                name: "CheckSecretsJson",
                table: "cron_jobs");

            migrationBuilder.DropColumn(
                name: "DefinitionVersion",
                table: "cron_jobs");
        }
    }
}
