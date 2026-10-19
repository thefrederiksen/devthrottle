using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddApiModeReceiptDirectorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id_DirectorId_State",
                schema: "gateway",
                table: "api_mode_receipts",
                columns: new[] { "tenant_id", "DirectorId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_api_mode_receipts_tenant_id_DirectorId_State",
                schema: "gateway",
                table: "api_mode_receipts");
        }
    }
}
