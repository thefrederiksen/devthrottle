using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnVerdictTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "turn_verdict_traces",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TurnEndObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Cause = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    VerdictId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReplacedVerdictId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReplySeconds = table.Column<double>(type: "REAL", nullable: true),
                    ColourEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PackageJson = table.Column<string>(type: "TEXT", nullable: true),
                    PackageOmitted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    PromptTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawReply = table.Column<string>(type: "TEXT", nullable: true),
                    RawReplyTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerdictJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_verdict_traces", x => new { x.tenant_id, x.TraceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_traces_tenant_id",
                table: "turn_verdict_traces",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_traces_tenant_id_RecordedAtUtc",
                table: "turn_verdict_traces",
                columns: new[] { "tenant_id", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_turn_verdict_traces_tenant_id_SessionId_RecordedAtUtc",
                table: "turn_verdict_traces",
                columns: new[] { "tenant_id", "SessionId", "RecordedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "turn_verdict_traces");
        }
    }
}
