using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionHistoryFactorySeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FactoryDefinitionVersion",
                schema: "gateway",
                table: "session_history",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactoryKit",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactorySeat",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FactoryStartOverride",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FactoryDefinitionVersion",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "FactoryKit",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "FactorySeat",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "FactoryStartOverride",
                schema: "gateway",
                table: "session_history");
        }
    }
}
