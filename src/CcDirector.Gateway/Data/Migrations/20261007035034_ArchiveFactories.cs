using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveFactories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "factory_registry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedBy",
                table: "factory_registry",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedSchedulesJson",
                table: "factory_registry",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "factory_registry");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                table: "factory_registry");

            migrationBuilder.DropColumn(
                name: "ArchivedSchedulesJson",
                table: "factory_registry");
        }
    }
}
