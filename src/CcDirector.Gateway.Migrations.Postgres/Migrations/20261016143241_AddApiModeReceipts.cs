using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddApiModeReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_mode_receipts",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    PromptId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DirectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QueueReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Acknowledgement = table.Column<string>(type: "text", nullable: true),
                    RefusalReason = table.Column<string>(type: "text", nullable: true),
                    RefusedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TurnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    TurnOutcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TurnOutcomeReason = table.Column<string>(type: "text", nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefusedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TurnEndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastGeneration = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastGenerationStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    FirstGenerationStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstSequence = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_mode_receipts", x => new { x.tenant_id, x.SessionId, x.PromptId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id",
                schema: "gateway",
                table: "api_mode_receipts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id_SessionId_TurnId",
                schema: "gateway",
                table: "api_mode_receipts",
                columns: new[] { "tenant_id", "SessionId", "TurnId" });

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id_UpdatedAtUtc",
                schema: "gateway",
                table: "api_mode_receipts",
                columns: new[] { "tenant_id", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_mode_receipts",
                schema: "gateway");
        }
    }
}
