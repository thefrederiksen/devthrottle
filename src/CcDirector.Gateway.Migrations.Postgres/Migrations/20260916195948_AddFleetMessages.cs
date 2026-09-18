using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_messages",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    RecipientSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SenderSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    SenderName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SenderMachine = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RingCount = table.Column<int>(type: "integer", nullable: false),
                    LastRungAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StuckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InReplyToMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReplyByUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_messages", x => new { x.tenant_id, x.MessageId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id",
                schema: "gateway",
                table: "fleet_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_CreatedAtUtc",
                schema: "gateway",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_RecipientSessionId_ReadAtUtc_Creat~",
                schema: "gateway",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "RecipientSessionId", "ReadAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_SenderSessionId_CreatedAtUtc",
                schema: "gateway",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "SenderSessionId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_messages",
                schema: "gateway");
        }
    }
}
