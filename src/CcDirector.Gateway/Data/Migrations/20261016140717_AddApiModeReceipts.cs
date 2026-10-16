using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiModeReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_mode_receipts",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PromptId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    QueueReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Disposition = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Acknowledgement = table.Column<string>(type: "TEXT", nullable: true),
                    RefusalReason = table.Column<string>(type: "TEXT", nullable: true),
                    RefusedBy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    TurnId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TurnOutcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    TurnOutcomeReason = table.Column<string>(type: "TEXT", nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefusedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TurnEndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_mode_receipts", x => new { x.tenant_id, x.SessionId, x.PromptId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id",
                table: "api_mode_receipts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id_SessionId_TurnId",
                table: "api_mode_receipts",
                columns: new[] { "tenant_id", "SessionId", "TurnId" });

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_receipts_tenant_id_UpdatedAtUtc",
                table: "api_mode_receipts",
                columns: new[] { "tenant_id", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_mode_receipts");
        }
    }
}
