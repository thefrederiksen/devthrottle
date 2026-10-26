using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReportItemActedOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActedOnAtUtc",
                schema: "gateway",
                table: "dev_report_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActedOnSessionId",
                schema: "gateway",
                table: "dev_report_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                collation: "C");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActedOnAtUtc",
                schema: "gateway",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "ActedOnSessionId",
                schema: "gateway",
                table: "dev_report_items");
        }
    }
}
