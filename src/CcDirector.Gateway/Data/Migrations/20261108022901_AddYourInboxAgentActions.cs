using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddYourInboxAgentActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SentBySessionName",
                table: "factory_inbox_messages",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SentBySessionNumber",
                table: "factory_inbox_messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "your_inbox_agent_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReplyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_your_inbox_agent_actions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_agent_actions_tenant_id",
                table: "your_inbox_agent_actions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_agent_actions_tenant_id_MessageId_AtUtc",
                table: "your_inbox_agent_actions",
                columns: new[] { "tenant_id", "MessageId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "your_inbox_agent_actions");

            migrationBuilder.DropColumn(
                name: "SentBySessionName",
                table: "factory_inbox_messages");

            migrationBuilder.DropColumn(
                name: "SentBySessionNumber",
                table: "factory_inbox_messages");
        }
    }
}
