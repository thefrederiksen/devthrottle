using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDeskRoutedLiveColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mail Desk phase 4, section 3.1: what routing in live mode needs on each routed email, and one routed row per
            // decision. Nothing wrote this table before phase 4, so no row should exist; any that does takes its channel
            // from its decision below, and two rows of one decision would make the unique index fail loudly.
            migrationBuilder.DropIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                table: "mail_desk_routed");

            migrationBuilder.AddColumn<Guid>(
                name: "ChannelId",
                table: "mail_desk_routed",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "DoneNote",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerReason",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RerouteCount",
                table: "mail_desk_routed",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StartFailures",
                table: "mail_desk_routed",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StartName",
                table: "mail_desk_routed",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedUtc",
                table: "mail_desk_routed",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "mail_desk_routed" SET "ChannelId" = (
                    SELECT d."ChannelId" FROM "mail_desk_decisions" d WHERE d."Id" = "mail_desk_routed"."DecisionId")
                WHERE EXISTS (SELECT 1 FROM "mail_desk_decisions" d WHERE d."Id" = "mail_desk_routed"."DecisionId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                table: "mail_desk_routed",
                columns: new[] { "tenant_id", "DecisionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "DoneNote",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "OwnerReason",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "RerouteCount",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "StartFailures",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "StartName",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "StartedUtc",
                table: "mail_desk_routed");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                table: "mail_desk_routed",
                columns: new[] { "tenant_id", "DecisionId" });
        }
    }
}
