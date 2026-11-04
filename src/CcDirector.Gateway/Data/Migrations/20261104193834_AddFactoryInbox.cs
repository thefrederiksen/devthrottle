using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_inbox_bundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HandedBySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HandedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadBySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_bundles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_inbox_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Why = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    DelegatedSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TodoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DecidedBySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_inbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    RawText = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    CorrectionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Surface = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SentBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReceivedBySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BundleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MergedIntoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LinksJson = table.Column<string>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_todos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    FromMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AddedBySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_todos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_bundles_tenant_id",
                table: "factory_inbox_bundles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_bundles_tenant_id_Factory_ClosedAtUtc_HandedAtUtc",
                table: "factory_inbox_bundles",
                columns: new[] { "tenant_id", "Factory", "ClosedAtUtc", "HandedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_decisions_tenant_id",
                table: "factory_inbox_decisions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_decisions_tenant_id_Factory_DecidedAtUtc",
                table: "factory_inbox_decisions",
                columns: new[] { "tenant_id", "Factory", "DecidedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_decisions_tenant_id_MessageId",
                table: "factory_inbox_decisions",
                columns: new[] { "tenant_id", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id",
                table: "factory_inbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id_BundleId",
                table: "factory_inbox_messages",
                columns: new[] { "tenant_id", "BundleId" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id_Factory_SentAtUtc",
                table: "factory_inbox_messages",
                columns: new[] { "tenant_id", "Factory", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id_Factory_State",
                table: "factory_inbox_messages",
                columns: new[] { "tenant_id", "Factory", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_todos_tenant_id",
                table: "factory_todos",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_todos_tenant_id_Factory_AddedAtUtc",
                table: "factory_todos",
                columns: new[] { "tenant_id", "Factory", "AddedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_inbox_bundles");

            migrationBuilder.DropTable(
                name: "factory_inbox_decisions");

            migrationBuilder.DropTable(
                name: "factory_inbox_messages");

            migrationBuilder.DropTable(
                name: "factory_todos");
        }
    }
}
