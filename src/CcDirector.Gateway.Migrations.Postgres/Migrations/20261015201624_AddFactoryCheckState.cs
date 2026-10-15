using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryCheckState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckPath",
                schema: "gateway",
                table: "triggers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefinitionVersion",
                schema: "gateway",
                table: "triggers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seat",
                schema: "gateway",
                table: "triggers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretsJson",
                schema: "gateway",
                table: "triggers",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckPath",
                schema: "gateway",
                table: "cron_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckSecretsJson",
                schema: "gateway",
                table: "cron_jobs",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefinitionVersion",
                schema: "gateway",
                table: "cron_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "factory_check_state",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    OwnerKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, collation: "C"),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WrittenBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RunRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reset = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_check_state", x => new { x.tenant_id, x.Factory, x.OwnerKey, x.Sequence });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_check_state_tenant_id",
                schema: "gateway",
                table: "factory_check_state",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_check_state",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "CheckPath",
                schema: "gateway",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "DefinitionVersion",
                schema: "gateway",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "Seat",
                schema: "gateway",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "SecretsJson",
                schema: "gateway",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "CheckPath",
                schema: "gateway",
                table: "cron_jobs");

            migrationBuilder.DropColumn(
                name: "CheckSecretsJson",
                schema: "gateway",
                table: "cron_jobs");

            migrationBuilder.DropColumn(
                name: "DefinitionVersion",
                schema: "gateway",
                table: "cron_jobs");
        }
    }
}
