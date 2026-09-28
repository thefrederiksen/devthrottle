using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryMemoryNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_memory_notes",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Factory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    AuthorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_memory_notes", x => new { x.tenant_id, x.Factory, x.Name, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_memory_notes_tenant_id",
                schema: "gateway",
                table: "factory_memory_notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_memory_notes_tenant_id_Factory_Name",
                schema: "gateway",
                table: "factory_memory_notes",
                columns: new[] { "tenant_id", "Factory", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_memory_notes",
                schema: "gateway");
        }
    }
}
