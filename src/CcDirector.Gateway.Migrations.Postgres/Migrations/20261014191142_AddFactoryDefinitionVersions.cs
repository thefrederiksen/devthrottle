using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryDefinitionVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_definition_versions",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentBytes = table.Column<int>(type: "integer", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    AuthorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChangeNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RestoredFrom = table.Column<int>(type: "integer", nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_definition_versions", x => new { x.tenant_id, x.Factory, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_definition_versions_tenant_id",
                schema: "gateway",
                table: "factory_definition_versions",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_definition_versions",
                schema: "gateway");
        }
    }
}
