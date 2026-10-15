using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_attachment_contents",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_attachment_contents", x => new { x.tenant_id, x.AttachmentId });
                });

            migrationBuilder.CreateTable(
                name: "factory_attachments",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Seat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, collation: "C"),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_document_versions",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TextBytes = table.Column<int>(type: "integer", nullable: false),
                    AuthorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_document_versions", x => new { x.tenant_id, x.Factory, x.Name, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "factory_journal_entries",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Seat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    EntryDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TextBytes = table.Column<int>(type: "integer", nullable: false),
                    AttachmentIds = table.Column<string>(type: "text", nullable: false),
                    AuthorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, collation: "C"),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_journal_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachment_contents_tenant_id",
                schema: "gateway",
                table: "factory_attachment_contents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachments_tenant_id",
                schema: "gateway",
                table: "factory_attachments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachments_tenant_id_Factory_ImportKey",
                schema: "gateway",
                table: "factory_attachments",
                columns: new[] { "tenant_id", "Factory", "ImportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachments_tenant_id_Factory_Seat",
                schema: "gateway",
                table: "factory_attachments",
                columns: new[] { "tenant_id", "Factory", "Seat" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_versions_tenant_id",
                schema: "gateway",
                table: "factory_document_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_versions_tenant_id_Factory_ImportKey",
                schema: "gateway",
                table: "factory_document_versions",
                columns: new[] { "tenant_id", "Factory", "ImportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_journal_entries_tenant_id",
                schema: "gateway",
                table: "factory_journal_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_journal_entries_tenant_id_Factory_ImportKey",
                schema: "gateway",
                table: "factory_journal_entries",
                columns: new[] { "tenant_id", "Factory", "ImportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_journal_entries_tenant_id_Factory_Seat_EntryDate",
                schema: "gateway",
                table: "factory_journal_entries",
                columns: new[] { "tenant_id", "Factory", "Seat", "EntryDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_attachment_contents",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "factory_attachments",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "factory_document_versions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "factory_journal_entries",
                schema: "gateway");
        }
    }
}
