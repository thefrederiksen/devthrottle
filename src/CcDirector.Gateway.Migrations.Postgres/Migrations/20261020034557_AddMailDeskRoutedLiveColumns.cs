using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
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
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.AddColumn<Guid>(
                name: "ChannelId",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "DoneNote",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerReason",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RerouteCount",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StartFailures",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StartName",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedUtc",
                schema: "gateway",
                table: "mail_desk_routed",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE gateway."mail_desk_routed" r SET "ChannelId" = d."ChannelId"
                FROM gateway."mail_desk_decisions" d WHERE d."Id" = r."DecisionId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                schema: "gateway",
                table: "mail_desk_routed",
                columns: new[] { "tenant_id", "DecisionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "DoneNote",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "OwnerReason",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "RerouteCount",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "StartFailures",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "StartName",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.DropColumn(
                name: "StartedUtc",
                schema: "gateway",
                table: "mail_desk_routed");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                schema: "gateway",
                table: "mail_desk_routed",
                columns: new[] { "tenant_id", "DecisionId" });
        }
    }
}
