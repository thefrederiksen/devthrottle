using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MonthlyPriceCents",
                table: "license_accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "license_usage_readings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SevenDayUtilization = table.Column<double>(type: "REAL", nullable: true),
                    SevenDayResetsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FiveHourUtilization = table.Column<double>(type: "REAL", nullable: true),
                    FiveHourResetsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_usage_readings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_usage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AgentKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MissionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ControllerSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AgentConversationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    LastOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCacheCreationTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    HasBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    WeightedTotal = table.Column<double>(type: "REAL", nullable: false),
                    WeightedByAccountJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_usage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_usage_samples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Weighted = table.Column<double>(type: "REAL", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_usage_samples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_usage_readings_tenant_id",
                table: "license_usage_readings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_usage_readings_tenant_id_AccountEmail_AtUtc",
                table: "license_usage_readings",
                columns: new[] { "tenant_id", "AccountEmail", "AtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_usage_readings_tenant_id_AtUtc",
                table: "license_usage_readings",
                columns: new[] { "tenant_id", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_tenant_id",
                table: "session_usage",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_tenant_id_LastSeenAtUtc",
                table: "session_usage",
                columns: new[] { "tenant_id", "LastSeenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_tenant_id_SessionId",
                table: "session_usage",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_samples_tenant_id",
                table: "session_usage_samples",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_samples_tenant_id_AtUtc",
                table: "session_usage_samples",
                columns: new[] { "tenant_id", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_samples_tenant_id_SessionId_AtUtc",
                table: "session_usage_samples",
                columns: new[] { "tenant_id", "SessionId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_usage_readings");

            migrationBuilder.DropTable(
                name: "session_usage");

            migrationBuilder.DropTable(
                name: "session_usage_samples");

            migrationBuilder.DropColumn(
                name: "MonthlyPriceCents",
                table: "license_accounts");
        }
    }
}
