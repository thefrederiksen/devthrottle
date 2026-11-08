using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddYourInboxAgentActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SentBySessionName",
                schema: "gateway",
                table: "factory_inbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SentBySessionNumber",
                schema: "gateway",
                table: "factory_inbox_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "your_inbox_agent_actions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SessionNumber = table.Column<int>(type: "integer", nullable: true),
                    SessionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReplyId = table.Column<Guid>(type: "uuid", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_your_inbox_agent_actions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_agent_actions_tenant_id",
                schema: "gateway",
                table: "your_inbox_agent_actions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_agent_actions_tenant_id_MessageId_AtUtc",
                schema: "gateway",
                table: "your_inbox_agent_actions",
                columns: new[] { "tenant_id", "MessageId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "your_inbox_agent_actions",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "SentBySessionName",
                schema: "gateway",
                table: "factory_inbox_messages");

            migrationBuilder.DropColumn(
                name: "SentBySessionNumber",
                schema: "gateway",
                table: "factory_inbox_messages");
        }
    }
}
