using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trigger_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TriggerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trigger_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "triggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FactoryAgent = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Machine = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepoPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CheckCommand = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ClaimedByDirectorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClaimedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triggers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trigger_runs_tenant_id",
                table: "trigger_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_trigger_runs_tenant_id_TriggerId_RecordedUtc",
                table: "trigger_runs",
                columns: new[] { "tenant_id", "TriggerId", "RecordedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_triggers_tenant_id",
                table: "triggers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_triggers_tenant_id_Name",
                table: "triggers",
                columns: new[] { "tenant_id", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trigger_runs");

            migrationBuilder.DropTable(
                name: "triggers");
        }
    }
}
