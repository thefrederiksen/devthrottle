using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveFactories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                schema: "gateway",
                table: "factory_registry",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedBy",
                schema: "gateway",
                table: "factory_registry",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedSchedulesJson",
                schema: "gateway",
                table: "factory_registry",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                schema: "gateway",
                table: "factory_registry");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                schema: "gateway",
                table: "factory_registry");

            migrationBuilder.DropColumn(
                name: "ArchivedSchedulesJson",
                schema: "gateway",
                table: "factory_registry");
        }
    }
}
