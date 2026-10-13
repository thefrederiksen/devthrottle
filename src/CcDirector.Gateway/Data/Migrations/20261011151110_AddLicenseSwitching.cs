using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseSwitching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "license_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Plan = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RateLimitTier = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_bars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Window = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BarredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_bars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_holdings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Plan = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RateLimitTier = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FiveHourUtilization = table.Column<double>(type: "REAL", nullable: true),
                    FiveHourResetsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SevenDayUtilization = table.Column<double>(type: "REAL", nullable: true),
                    SevenDayResetsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExtraUsageEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    ExtraUsageUtilization = table.Column<double>(type: "REAL", nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    JudgedReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReadError = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ReadErrorDetail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SignedInAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SignInExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_holdings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_machines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OsUser = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MachineKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OsUserKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastReportAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTrigger = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ActiveAccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    SwitchInProgressId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ClockSkewSeconds = table.Column<double>(type: "REAL", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_machines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_switch_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SwitchId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OsUser = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FromAccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ToAccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SendCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DirectorStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DirectorFinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExchangeMilliseconds = table.Column<int>(type: "INTEGER", nullable: true),
                    ActiveAccountEmailAfter = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    VerifiedByUsageRead = table.Column<bool>(type: "INTEGER", nullable: true),
                    Replayed = table.Column<bool>(type: "INTEGER", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_switch_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_accounts_tenant_id",
                table: "license_accounts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_accounts_tenant_id_AccountEmail",
                table: "license_accounts",
                columns: new[] { "tenant_id", "AccountEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_bars_tenant_id",
                table: "license_bars",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_bars_tenant_id_AccountEmail_Window",
                table: "license_bars",
                columns: new[] { "tenant_id", "AccountEmail", "Window" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_holdings_tenant_id",
                table: "license_holdings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_holdings_tenant_id_AccountEmail",
                table: "license_holdings",
                columns: new[] { "tenant_id", "AccountEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_license_holdings_tenant_id_MachineId_AccountEmail",
                table: "license_holdings",
                columns: new[] { "tenant_id", "MachineId", "AccountEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_machines_tenant_id",
                table: "license_machines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_machines_tenant_id_MachineKey_OsUserKey",
                table: "license_machines",
                columns: new[] { "tenant_id", "MachineKey", "OsUserKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id",
                table: "license_switch_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id_CreatedAtUtc",
                table: "license_switch_log",
                columns: new[] { "tenant_id", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id_State",
                table: "license_switch_log",
                columns: new[] { "tenant_id", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_license_switch_log_tenant_id_SwitchId",
                table: "license_switch_log",
                columns: new[] { "tenant_id", "SwitchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_accounts");

            migrationBuilder.DropTable(
                name: "license_bars");

            migrationBuilder.DropTable(
                name: "license_holdings");

            migrationBuilder.DropTable(
                name: "license_machines");

            migrationBuilder.DropTable(
                name: "license_switch_log");
        }
    }
}
