using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseSwitching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "license_accounts",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RateLimitTier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_bars",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Window = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    BarredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_bars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_holdings",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RateLimitTier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FiveHourUtilization = table.Column<double>(type: "double precision", nullable: true),
                    FiveHourResetsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SevenDayUtilization = table.Column<double>(type: "double precision", nullable: true),
                    SevenDayResetsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtraUsageEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    ExtraUsageUtilization = table.Column<double>(type: "double precision", nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    JudgedReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadError = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReadErrorDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SignedInAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignInExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_holdings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_machines",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OsUser = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MachineKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    OsUserKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    DirectorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastReportAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastTrigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActiveAccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SwitchInProgressId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ClockSkewSeconds = table.Column<double>(type: "double precision", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_machines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_switch_log",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SwitchId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OsUser = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DirectorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FromAccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ToAccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    ReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendCount = table.Column<int>(type: "integer", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DirectorStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DirectorFinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExchangeMilliseconds = table.Column<int>(type: "integer", nullable: true),
                    ActiveAccountEmailAfter = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    VerifiedByUsageRead = table.Column<bool>(type: "boolean", nullable: true),
                    Replayed = table.Column<bool>(type: "boolean", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_switch_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_accounts_tenant_id",
                schema: "gateway",
                table: "license_accounts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_accounts_tenant_id_AccountEmail",
                schema: "gateway",
                table: "license_accounts",
                columns: new[] { "tenant_id", "AccountEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_bars_tenant_id",
                schema: "gateway",
                table: "license_bars",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_bars_tenant_id_AccountEmail_Window",
                schema: "gateway",
                table: "license_bars",
                columns: new[] { "tenant_id", "AccountEmail", "Window" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_holdings_tenant_id",
                schema: "gateway",
                table: "license_holdings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_holdings_tenant_id_AccountEmail",
                schema: "gateway",
                table: "license_holdings",
                columns: new[] { "tenant_id", "AccountEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_license_holdings_tenant_id_MachineId_AccountEmail",
                schema: "gateway",
                table: "license_holdings",
                columns: new[] { "tenant_id", "MachineId", "AccountEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_machines_tenant_id",
                schema: "gateway",
                table: "license_machines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_machines_tenant_id_MachineKey_OsUserKey",
                schema: "gateway",
                table: "license_machines",
                columns: new[] { "tenant_id", "MachineKey", "OsUserKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id",
                schema: "gateway",
                table: "license_switch_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id_CreatedAtUtc",
                schema: "gateway",
                table: "license_switch_log",
                columns: new[] { "tenant_id", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id_State",
                schema: "gateway",
                table: "license_switch_log",
                columns: new[] { "tenant_id", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id_SwitchId",
                schema: "gateway",
                table: "license_switch_log",
                columns: new[] { "tenant_id", "SwitchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_accounts",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "license_bars",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "license_holdings",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "license_machines",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "license_switch_log",
                schema: "gateway");
        }
    }
}
