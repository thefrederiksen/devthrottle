using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MonthlyPriceCents",
                schema: "gateway",
                table: "license_accounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "license_usage_readings",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SevenDayUtilization = table.Column<double>(type: "double precision", nullable: true),
                    SevenDayResetsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FiveHourUtilization = table.Column<double>(type: "double precision", nullable: true),
                    FiveHourResetsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_usage_readings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_usage",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DirectorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AgentKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    MissionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ControllerSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MachineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AgentConversationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    LastOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    LastCacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    LastCacheCreationTokens = table.Column<long>(type: "bigint", nullable: false),
                    HasBaseline = table.Column<bool>(type: "boolean", nullable: false),
                    WeightedTotal = table.Column<double>(type: "double precision", nullable: false),
                    WeightedByAccountJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_usage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_usage_samples",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Weighted = table.Column<double>(type: "double precision", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "bigint", nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_usage_samples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_usage_readings_tenant_id",
                schema: "gateway",
                table: "license_usage_readings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_usage_readings_tenant_id_AccountEmail_AtUtc",
                schema: "gateway",
                table: "license_usage_readings",
                columns: new[] { "tenant_id", "AccountEmail", "AtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_usage_readings_tenant_id_AtUtc",
                schema: "gateway",
                table: "license_usage_readings",
                columns: new[] { "tenant_id", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_tenant_id",
                schema: "gateway",
                table: "session_usage",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_tenant_id_LastSeenAtUtc",
                schema: "gateway",
                table: "session_usage",
                columns: new[] { "tenant_id", "LastSeenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_tenant_id_SessionId",
                schema: "gateway",
                table: "session_usage",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_samples_tenant_id",
                schema: "gateway",
                table: "session_usage_samples",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_samples_tenant_id_AtUtc",
                schema: "gateway",
                table: "session_usage_samples",
                columns: new[] { "tenant_id", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_usage_samples_tenant_id_SessionId_AtUtc",
                schema: "gateway",
                table: "session_usage_samples",
                columns: new[] { "tenant_id", "SessionId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_usage_readings",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "session_usage",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "session_usage_samples",
                schema: "gateway");

            migrationBuilder.DropColumn(
                name: "MonthlyPriceCents",
                schema: "gateway",
                table: "license_accounts");
        }
    }
}
