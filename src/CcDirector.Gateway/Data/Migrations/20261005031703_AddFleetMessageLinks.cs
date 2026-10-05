using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkId",
                table: "fleet_messages",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fleet_message_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SenderSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RecipientSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Amount = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SetUpBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SetUpAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedMessageId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_message_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id",
                table: "fleet_message_links",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id_LinkId",
                table: "fleet_message_links",
                columns: new[] { "tenant_id", "LinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id_RecipientSessionId_Status",
                table: "fleet_message_links",
                columns: new[] { "tenant_id", "RecipientSessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id_SenderSessionId_RecipientSessionId_Status",
                table: "fleet_message_links",
                columns: new[] { "tenant_id", "SenderSessionId", "RecipientSessionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_message_links");

            migrationBuilder.DropColumn(
                name: "LinkId",
                table: "fleet_messages");
        }
    }
}
