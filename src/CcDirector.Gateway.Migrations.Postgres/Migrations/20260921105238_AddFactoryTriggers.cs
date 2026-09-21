using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trigger_runs",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Count = table.Column<int>(type: "integer", nullable: true),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DirectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trigger_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "triggers",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    Factory = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FactoryAgent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Machine = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    RepoPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CheckCommand = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ClaimedByDirectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triggers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trigger_runs_tenant_id",
                schema: "gateway",
                table: "trigger_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_trigger_runs_tenant_id_TriggerId_RecordedUtc",
                schema: "gateway",
                table: "trigger_runs",
                columns: new[] { "tenant_id", "TriggerId", "RecordedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_triggers_tenant_id",
                schema: "gateway",
                table: "triggers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_triggers_tenant_id_Name",
                schema: "gateway",
                table: "triggers",
                columns: new[] { "tenant_id", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trigger_runs",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "triggers",
                schema: "gateway");
        }
    }
}
