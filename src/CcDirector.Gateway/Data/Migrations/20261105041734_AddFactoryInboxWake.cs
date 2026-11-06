using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryInboxWake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_inbox_wakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SecretarySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SecretaryStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SecretaryToldUpToUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BossSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BossStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BossToldUpToUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SecretaryWaiting = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SecretaryWaitingDetail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    BossWaiting = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BossWaitingDetail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_inbox_wakes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_wakes_tenant_id",
                table: "factory_inbox_wakes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_inbox_wakes_tenant_id_Factory",
                table: "factory_inbox_wakes",
                columns: new[] { "tenant_id", "Factory" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_inbox_wakes");
        }
    }
}
