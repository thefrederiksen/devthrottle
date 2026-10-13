using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RenewalError",
                table: "license_holdings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenewalErrorDetail",
                table: "license_holdings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalFailedAtUtc",
                table: "license_holdings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RenewsOn",
                table: "license_accounts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenewalError",
                table: "license_holdings");

            migrationBuilder.DropColumn(
                name: "RenewalErrorDetail",
                table: "license_holdings");

            migrationBuilder.DropColumn(
                name: "RenewalFailedAtUtc",
                table: "license_holdings");

            migrationBuilder.DropColumn(
                name: "RenewsOn",
                table: "license_accounts");
        }
    }
}
