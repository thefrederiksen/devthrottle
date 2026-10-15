using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedSessionFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_session_filters",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Colour = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReposJson = table.Column<string>(type: "TEXT", nullable: false),
                    MachinesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_session_filters", x => new { x.tenant_id, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_saved_session_filters_tenant_id",
                table: "saved_session_filters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_saved_session_filters_tenant_id_NameKey",
                table: "saved_session_filters",
                columns: new[] { "tenant_id", "NameKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_session_filters");
        }
    }
}
