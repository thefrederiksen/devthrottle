using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteInboxKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebsiteKey",
                table: "your_inbox_messages",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "website_inbox_keys",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByDeviceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_inbox_keys", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_messages_tenant_id_WebsiteKey",
                table: "your_inbox_messages",
                columns: new[] { "tenant_id", "WebsiteKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_website_inbox_keys_KeyHash",
                table: "website_inbox_keys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "website_inbox_keys");

            migrationBuilder.DropIndex(
                name: "IX_your_inbox_messages_tenant_id_WebsiteKey",
                table: "your_inbox_messages");

            migrationBuilder.DropColumn(
                name: "WebsiteKey",
                table: "your_inbox_messages");
        }
    }
}
