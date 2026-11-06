using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddYourInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "your_inbox_messages",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(24400)", maxLength: 24400, nullable: false),
                    FromKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    SessionNumber = table.Column<int>(type: "integer", nullable: true),
                    SessionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Seat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FactoryMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_your_inbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "your_inbox_replies",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    RoutedTo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    FleetMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FactoryMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    SentBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_your_inbox_replies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_messages_tenant_id",
                schema: "gateway",
                table: "your_inbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_messages_tenant_id_State_SentAtUtc",
                schema: "gateway",
                table: "your_inbox_messages",
                columns: new[] { "tenant_id", "State", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_replies_tenant_id",
                schema: "gateway",
                table: "your_inbox_replies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_your_inbox_replies_tenant_id_MessageId_SentAtUtc",
                schema: "gateway",
                table: "your_inbox_replies",
                columns: new[] { "tenant_id", "MessageId", "SentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "your_inbox_messages",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "your_inbox_replies",
                schema: "gateway");
        }
    }
}
