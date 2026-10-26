using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReportActionClaimsAndSeatIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Factory",
                table: "dev_reports",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactorySeat",
                table: "dev_reports",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActionClaimedAtUtc",
                table: "dev_report_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionClaimedSessionId",
                table: "dev_report_items",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Factory",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "FactorySeat",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "ActionClaimedAtUtc",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "ActionClaimedSessionId",
                table: "dev_report_items");
        }
    }
}
