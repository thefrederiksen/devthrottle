using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactorySeatBriefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_seat_briefs",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Seat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TextBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AuthorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PublishedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_seat_briefs", x => new { x.tenant_id, x.Factory, x.Seat, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_seat_briefs_tenant_id",
                table: "factory_seat_briefs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_seat_briefs");
        }
    }
}
