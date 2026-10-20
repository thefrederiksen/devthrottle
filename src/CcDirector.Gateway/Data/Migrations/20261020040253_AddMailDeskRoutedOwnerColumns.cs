using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
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
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EarlierReturnedFrom",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 129,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EarlierReturnedUtc",
                table: "mail_desk_routed",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerUtc",
                table: "mail_desk_routed",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedReason",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                table: "mail_desk_routed",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnedFrom",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 129,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedUtc",
                table: "mail_desk_routed",
                type: "TEXT",
                nullable: true);

            // A row handed to the owner before this column existed was handed when it last changed.
            migrationBuilder.Sql(
                """
                UPDATE "mail_desk_routed" SET "OwnerUtc" = "ChangedUtc" WHERE "State" = 'returned' AND "OwnerUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EarlierReturnNote",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "EarlierReturnedFrom",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "EarlierReturnedUtc",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "OwnerUtc",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ResolvedReason",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ReturnedFrom",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ReturnedUtc",
                table: "mail_desk_routed");
        }
    }
}
