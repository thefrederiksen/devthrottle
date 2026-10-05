using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessageLinkRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_message_link_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequesterSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AskedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnsweredBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AnsweredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Amount = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LinkId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_message_link_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id",
                table: "fleet_message_link_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id_RequesterSessionId_Status",
                table: "fleet_message_link_requests",
                columns: new[] { "tenant_id", "RequesterSessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id_RequestId",
                table: "fleet_message_link_requests",
                columns: new[] { "tenant_id", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_message_link_requests_tenant_id_Status",
                table: "fleet_message_link_requests",
                columns: new[] { "tenant_id", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_message_link_requests");
        }
    }
}
