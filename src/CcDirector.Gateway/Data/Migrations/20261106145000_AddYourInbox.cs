using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddYourInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "your_inbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 24400, nullable: false),
                    FromKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SessionNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Seat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReportTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    FactoryMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_your_inbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "your_inbox_replies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    RoutedTo = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FleetMessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FactoryMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SentBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_your_inbox_replies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_messages_tenant_id",
                table: "your_inbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_messages_tenant_id_State_SentAtUtc",
                table: "your_inbox_messages",
                columns: new[] { "tenant_id", "State", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_replies_tenant_id",
                table: "your_inbox_replies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_replies_tenant_id_MessageId_SentAtUtc",
                table: "your_inbox_replies",
                columns: new[] { "tenant_id", "MessageId", "SentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "your_inbox_messages");

            migrationBuilder.DropTable(
                name: "your_inbox_replies");
        }
    }
}
