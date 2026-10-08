using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RenameCeoSeatToBossSeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CeoSeat",
                schema: "gateway",
                table: "factory_registry",
                newName: "BossSeat");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BossSeat",
                schema: "gateway",
                table: "factory_registry",
                newName: "CeoSeat");
        }
    }
}
