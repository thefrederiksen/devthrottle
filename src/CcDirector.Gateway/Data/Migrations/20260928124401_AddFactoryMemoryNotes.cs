using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryMemoryNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_memory_notes",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_memory_notes", x => new { x.tenant_id, x.Factory, x.Name, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_memory_notes_tenant_id",
                table: "factory_memory_notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_memory_notes_tenant_id_Factory_Name",
                table: "factory_memory_notes",
                columns: new[] { "tenant_id", "Factory", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_memory_notes");
        }
    }
}
