using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryDefinitionPublisher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublishedByKind",
                table: "factory_definition_versions",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedByName",
                table: "factory_definition_versions",
                type: "TEXT",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedByKind",
                table: "factory_definition_versions");

            migrationBuilder.DropColumn(
                name: "PublishedByName",
                table: "factory_definition_versions");
        }
    }
}
