using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnVerdicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "turn_verdict_feedback",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    VerdictId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TurnEndObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrectedVerdict = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_verdict_feedback", x => new { x.tenant_id, x.VerdictId });
                });

            migrationBuilder.CreateTable(
                name: "turn_verdicts",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    JudgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerdictId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TurnEndObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScreenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    VerdictJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_verdicts", x => new { x.tenant_id, x.SessionId, x.JudgedAtUtc });
                });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_feedback_tenant_id",
                table: "turn_verdict_feedback",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_feedback_tenant_id_ReportedAtUtc",
                table: "turn_verdict_feedback",
                columns: new[] { "tenant_id", "ReportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_feedback_tenant_id_SessionId_ReportedAtUtc",
                table: "turn_verdict_feedback",
                columns: new[] { "tenant_id", "SessionId", "ReportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdicts_tenant_id",
                table: "turn_verdicts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdicts_tenant_id_JudgedAtUtc",
                table: "turn_verdicts",
                columns: new[] { "tenant_id", "JudgedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdicts_tenant_id_SessionId_JudgedAtUtc",
                table: "turn_verdicts",
                columns: new[] { "tenant_id", "SessionId", "JudgedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "turn_verdict_feedback");

            migrationBuilder.DropTable(
                name: "turn_verdicts");
        }
    }
}
