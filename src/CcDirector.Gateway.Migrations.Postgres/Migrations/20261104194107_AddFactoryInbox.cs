using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_inbox_bundles",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    HandedBySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    HandedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadBySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_bundles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_inbox_decisions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Why = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DelegatedSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    TodoId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedBySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_inbox_messages",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    RawText = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CorrectionsJson = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Surface = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SentBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedBySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ClaimSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: true),
                    MergedIntoId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinksJson = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_todos",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    FromMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedBySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    AddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_todos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_bundles_tenant_id",
                schema: "gateway",
                table: "factory_inbox_bundles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_bundles_tenant_id_Factory_ClosedAtUtc_HandedA~",
                schema: "gateway",
                table: "factory_inbox_bundles",
                columns: new[] { "tenant_id", "Factory", "ClosedAtUtc", "HandedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_decisions_tenant_id",
                schema: "gateway",
                table: "factory_inbox_decisions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_decisions_tenant_id_Factory_DecidedAtUtc",
                schema: "gateway",
                table: "factory_inbox_decisions",
                columns: new[] { "tenant_id", "Factory", "DecidedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_decisions_tenant_id_MessageId",
                schema: "gateway",
                table: "factory_inbox_decisions",
                columns: new[] { "tenant_id", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id",
                schema: "gateway",
                table: "factory_inbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id_BundleId",
                schema: "gateway",
                table: "factory_inbox_messages",
                columns: new[] { "tenant_id", "BundleId" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id_Factory_SentAtUtc",
                schema: "gateway",
                table: "factory_inbox_messages",
                columns: new[] { "tenant_id", "Factory", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_messages_tenant_id_Factory_State",
                schema: "gateway",
                table: "factory_inbox_messages",
                columns: new[] { "tenant_id", "Factory", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_todos_tenant_id",
                schema: "gateway",
                table: "factory_todos",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_todos_tenant_id_Factory_AddedAtUtc",
                schema: "gateway",
                table: "factory_todos",
                columns: new[] { "tenant_id", "Factory", "AddedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_inbox_bundles",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "factory_inbox_decisions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "factory_inbox_messages",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "factory_todos",
                schema: "gateway");
        }
    }
}
