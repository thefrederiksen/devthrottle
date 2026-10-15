using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_attachment_contents",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_attachment_contents", x => new { x.tenant_id, x.AttachmentId });
                });

            migrationBuilder.CreateTable(
                name: "factory_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Seat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AuthorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "factory_document_versions",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TextBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_document_versions", x => new { x.tenant_id, x.Factory, x.Name, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "factory_journal_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Seat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EntryDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TextBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    AttachmentIds = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_journal_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachment_contents_tenant_id",
                table: "factory_attachment_contents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachments_tenant_id",
                table: "factory_attachments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachments_tenant_id_Factory_ImportKey",
                table: "factory_attachments",
                columns: new[] { "tenant_id", "Factory", "ImportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_attachments_tenant_id_Factory_Seat",
                table: "factory_attachments",
                columns: new[] { "tenant_id", "Factory", "Seat" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_versions_tenant_id",
                table: "factory_document_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_versions_tenant_id_Factory_ImportKey",
                table: "factory_document_versions",
                columns: new[] { "tenant_id", "Factory", "ImportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_journal_entries_tenant_id",
                table: "factory_journal_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_journal_entries_tenant_id_Factory_ImportKey",
                table: "factory_journal_entries",
                columns: new[] { "tenant_id", "Factory", "ImportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_journal_entries_tenant_id_Factory_Seat_EntryDate",
                table: "factory_journal_entries",
                columns: new[] { "tenant_id", "Factory", "Seat", "EntryDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_attachment_contents");

            migrationBuilder.DropTable(
                name: "factory_attachments");

            migrationBuilder.DropTable(
                name: "factory_document_versions");

            migrationBuilder.DropTable(
                name: "factory_journal_entries");
        }
    }
}
