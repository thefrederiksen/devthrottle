using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryDefinitionPublisher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublishedByKind",
                schema: "gateway",
                table: "factory_definition_versions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedByName",
                schema: "gateway",
                table: "factory_definition_versions",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedByKind",
                schema: "gateway",
                table: "factory_definition_versions");

            migrationBuilder.DropColumn(
                name: "PublishedByName",
                schema: "gateway",
                table: "factory_definition_versions");
        }
    }
}
