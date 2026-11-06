using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryInboxWake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_inbox_wakes",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SecretarySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    SecretaryStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SecretaryToldUpToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BossSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    BossStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BossToldUpToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SecretaryWaiting = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    SecretaryWaitingDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BossWaiting = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    BossWaitingDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_wakes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_wakes_tenant_id",
                schema: "gateway",
                table: "factory_inbox_wakes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_wakes_tenant_id_Factory",
                schema: "gateway",
                table: "factory_inbox_wakes",
                columns: new[] { "tenant_id", "Factory" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_inbox_wakes",
                schema: "gateway");
        }
    }
}
