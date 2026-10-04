using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_requests",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "team_request_changes",
                schema: "gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    by_subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_request_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_request_changes_team_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "gateway",
                        principalTable: "team_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_request_changes_request_id",
                schema: "gateway",
                table: "team_request_changes",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_request_changes_tenant_id",
                schema: "gateway",
                table: "team_request_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_request_changes_tenant_id_request_id_at_utc",
                schema: "gateway",
                table: "team_request_changes",
                columns: new[] { "tenant_id", "request_id", "at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_team_requests_tenant_id",
                schema: "gateway",
                table: "team_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_requests_tenant_id_sender_subject_sent_at_utc",
                schema: "gateway",
                table: "team_requests",
                columns: new[] { "tenant_id", "sender_subject", "sent_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_team_requests_tenant_id_sent_at_utc",
                schema: "gateway",
                table: "team_requests",
                columns: new[] { "tenant_id", "sent_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_request_changes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "team_requests",
                schema: "gateway");
        }
    }
}
