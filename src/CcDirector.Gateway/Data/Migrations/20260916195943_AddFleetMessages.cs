using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_messages",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecipientSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SenderSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SenderName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SenderMachine = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TextHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRungAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StuckAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    InReplyToMessageId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ReplyByUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_messages", x => new { x.tenant_id, x.MessageId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id",
                table: "fleet_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_CreatedAtUtc",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_RecipientSessionId_ReadAtUtc_CreatedAtUtc",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "RecipientSessionId", "ReadAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_SenderSessionId_CreatedAtUtc",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "SenderSessionId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_messages");
        }
    }
}
