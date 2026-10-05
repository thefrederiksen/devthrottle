using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkId",
                schema: "gateway",
                table: "fleet_messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fleet_message_links",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    SenderSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    RecipientSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Amount = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SetUpBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SetUpAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_message_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id",
                schema: "gateway",
                table: "fleet_message_links",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id_LinkId",
                schema: "gateway",
                table: "fleet_message_links",
                columns: new[] { "tenant_id", "LinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id_RecipientSessionId_Status",
                schema: "gateway",
                table: "fleet_message_links",
                columns: new[] { "tenant_id", "RecipientSessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_links_tenant_id_SenderSessionId_RecipientSess~",
                schema: "gateway",
                table: "fleet_message_links",
                columns: new[] { "tenant_id", "SenderSessionId", "RecipientSessionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_message_links",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "LinkId",
                schema: "gateway",
                table: "fleet_messages");
        }
    }
}
