using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryBossStarts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_boss_starts",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Factory = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Seat = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_boss_starts", x => new { x.tenant_id, x.SessionId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_boss_starts_tenant_id",
                schema: "gateway",
                table: "factory_boss_starts",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_boss_starts",
                schema: "gateway");
        }
    }
}
