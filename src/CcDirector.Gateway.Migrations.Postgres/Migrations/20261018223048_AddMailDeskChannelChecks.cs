using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDeskChannelChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_desk_channel_checks",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Machine = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Sentence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_channel_checks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_channel_checks_tenant_id",
                schema: "gateway",
                table: "mail_desk_channel_checks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_channel_checks_tenant_id_ChannelId_CheckedUtc",
                schema: "gateway",
                table: "mail_desk_channel_checks",
                columns: new[] { "tenant_id", "ChannelId", "CheckedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_desk_channel_checks",
                schema: "gateway");
        }
    }
}
