using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionHistoryFactorySeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FactoryDefinitionVersion",
                table: "session_history",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactoryKit",
                table: "session_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactorySeat",
                table: "session_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactoryStartOverride",
                table: "session_history",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FactoryDefinitionVersion",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "FactoryKit",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "FactorySeat",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "FactoryStartOverride",
                table: "session_history");
        }
    }
}
