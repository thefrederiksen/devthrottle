using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReportActionClaimsAndSeatIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Factory",
                schema: "gateway",
                table: "dev_reports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "FactorySeat",
                schema: "gateway",
                table: "dev_reports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<DateTime>(
                name: "ActionClaimedAtUtc",
                schema: "gateway",
                table: "dev_report_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionClaimedSessionId",
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
                name: "Factory",
                schema: "gateway",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "FactorySeat",
                schema: "gateway",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "ActionClaimedAtUtc",
                schema: "gateway",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "ActionClaimedSessionId",
                schema: "gateway",
                table: "dev_report_items");
        }
    }
}
