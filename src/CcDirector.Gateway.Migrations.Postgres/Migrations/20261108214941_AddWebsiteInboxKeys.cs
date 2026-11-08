using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteInboxKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebsiteKey",
                schema: "gateway",
                table: "your_inbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "website_inbox_keys",
                schema: "gateway",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByDeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_inbox_keys", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_messages_tenant_id_WebsiteKey",
                schema: "gateway",
                table: "your_inbox_messages",
                columns: new[] { "tenant_id", "WebsiteKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_website_inbox_keys_KeyHash",
                schema: "gateway",
                table: "website_inbox_keys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "website_inbox_keys",
                schema: "gateway");

            migrationBuilder.DropIndex(
                name: "IX_your_inbox_messages_tenant_id_WebsiteKey",
                schema: "gateway",
                table: "your_inbox_messages");

            migrationBuilder.DropColumn(
                name: "WebsiteKey",
                schema: "gateway",
                table: "your_inbox_messages");
        }
    }
}
