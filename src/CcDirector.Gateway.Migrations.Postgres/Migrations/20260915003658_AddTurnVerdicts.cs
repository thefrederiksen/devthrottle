using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnVerdicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "turn_verdict_feedback",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    VerdictId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TurnEndObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrectedVerdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_verdict_feedback", x => new { x.tenant_id, x.VerdictId });
                });

            migrationBuilder.CreateTable(
                name: "turn_verdicts",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    JudgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerdictId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TurnEndObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScreenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Failed = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    VerdictJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_verdicts", x => new { x.tenant_id, x.SessionId, x.JudgedAtUtc });
                });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_feedback_tenant_id",
                schema: "gateway",
                table: "turn_verdict_feedback",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_feedback_tenant_id_ReportedAtUtc",
                schema: "gateway",
                table: "turn_verdict_feedback",
                columns: new[] { "tenant_id", "ReportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_feedback_tenant_id_SessionId_ReportedAtUtc",
                schema: "gateway",
                table: "turn_verdict_feedback",
                columns: new[] { "tenant_id", "SessionId", "ReportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdicts_tenant_id",
                schema: "gateway",
                table: "turn_verdicts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdicts_tenant_id_JudgedAtUtc",
                schema: "gateway",
                table: "turn_verdicts",
                columns: new[] { "tenant_id", "JudgedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdicts_tenant_id_SessionId_JudgedAtUtc",
                schema: "gateway",
                table: "turn_verdicts",
                columns: new[] { "tenant_id", "SessionId", "JudgedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "turn_verdict_feedback",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "turn_verdicts",
                schema: "gateway");
        }
    }
}
