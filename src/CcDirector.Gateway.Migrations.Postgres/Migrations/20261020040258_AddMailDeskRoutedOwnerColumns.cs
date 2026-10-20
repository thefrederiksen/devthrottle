using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDeskRoutedOwnerColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mail Desk phase 4, section 4.2: what the owner's problem and the 08:00 email's "Returned and routed" part
            // need on each routed email - when, by which seat and with what note each of its (at most two) returns was
            // made, when it was handed to the owner, and the hand resolve of the owner's problem.
            migrationBuilder.AddColumn<string>(
                name: "EarlierReturnNote",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EarlierReturnedFrom",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(129)",
                maxLength: 129,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EarlierReturnedUtc",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerUtc",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedReason",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnedFrom",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(129)",
                maxLength: 129,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedUtc",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "timestamp with time zone",
                nullable: true);

            // A row handed to the owner before this column existed was handed when it last changed.
            migrationBuilder.Sql(
                """
                UPDATE gateway."mail_desk_routed" SET "OwnerUtc" = "ChangedUtc" WHERE "State" = 'returned' AND "OwnerUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EarlierReturnNote",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "EarlierReturnedFrom",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "EarlierReturnedUtc",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "OwnerUtc",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ResolvedReason",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ReturnedFrom",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ReturnedUtc",
                schema: "gateway",
                table: "mail_desk_routed");
        }
    }
}
