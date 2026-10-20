using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDeskChannelChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_desk_channel_checks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TriggerRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Machine = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sentence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ResolvedReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_channel_checks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_channel_checks_tenant_id",
                table: "mail_desk_channel_checks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_channel_checks_tenant_id_ChannelId_CheckedUtc",
                table: "mail_desk_channel_checks",
                columns: new[] { "tenant_id", "ChannelId", "CheckedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_desk_channel_checks");
        }
    }
}
