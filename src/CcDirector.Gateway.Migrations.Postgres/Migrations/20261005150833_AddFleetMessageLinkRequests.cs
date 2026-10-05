using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessageLinkRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_message_link_requests",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    RequesterSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    TargetSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    AskedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AnsweredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Amount = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LinkId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_message_link_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id",
                schema: "gateway",
                table: "fleet_message_link_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id_RequesterSessionId_St~",
                schema: "gateway",
                table: "fleet_message_link_requests",
                columns: new[] { "tenant_id", "RequesterSessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id_RequestId",
                schema: "gateway",
                table: "fleet_message_link_requests",
                columns: new[] { "tenant_id", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id_Status",
                schema: "gateway",
                table: "fleet_message_link_requests",
                columns: new[] { "tenant_id", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_message_link_requests",
                schema: "gateway");
        }
    }
}
