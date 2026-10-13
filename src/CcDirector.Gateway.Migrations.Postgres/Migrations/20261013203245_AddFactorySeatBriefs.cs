using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactorySeatBriefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_seat_briefs",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Seat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    AuthorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_seat_briefs", x => new { x.tenant_id, x.Factory, x.Seat, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_seat_briefs_tenant_id",
                schema: "gateway",
                table: "factory_seat_briefs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_seat_briefs",
                schema: "gateway");
        }
    }
}
