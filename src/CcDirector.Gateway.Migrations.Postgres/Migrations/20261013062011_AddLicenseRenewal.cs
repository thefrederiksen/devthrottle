using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FromAccountEmail",
                schema: "gateway",
                table: "license_switch_log",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);

            migrationBuilder.AddColumn<string>(
                name: "RenewalError",
                schema: "gateway",
                table: "license_holdings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenewalErrorDetail",
                schema: "gateway",
                table: "license_holdings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalFailedAtUtc",
                schema: "gateway",
                table: "license_holdings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RenewsOn",
                schema: "gateway",
                table: "license_accounts",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenewalError",
                schema: "gateway",
                table: "license_holdings");

            migrationBuilder.DropColumn(
                name: "RenewalErrorDetail",
                schema: "gateway",
                table: "license_holdings");

            migrationBuilder.DropColumn(
                name: "RenewalFailedAtUtc",
                schema: "gateway",
                table: "license_holdings");

            migrationBuilder.DropColumn(
                name: "RenewsOn",
                schema: "gateway",
                table: "license_accounts");

            migrationBuilder.AlterColumn<string>(
                name: "FromAccountEmail",
                schema: "gateway",
                table: "license_switch_log",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320,
                oldCollation: "C");
        }
    }
}
