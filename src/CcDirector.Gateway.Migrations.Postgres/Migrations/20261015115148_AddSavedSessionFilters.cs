using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedSessionFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_session_filters",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, collation: "C"),
                    Colour = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReposJson = table.Column<string>(type: "text", nullable: false),
                    MachinesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_session_filters", x => new { x.tenant_id, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_saved_session_filters_tenant_id",
                schema: "gateway",
                table: "saved_session_filters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_saved_session_filters_tenant_id_NameKey",
                schema: "gateway",
                table: "saved_session_filters",
                columns: new[] { "tenant_id", "NameKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_session_filters",
                schema: "gateway");
        }
    }
}
