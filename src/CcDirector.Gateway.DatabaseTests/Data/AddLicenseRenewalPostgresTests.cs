using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of <c>AddLicenseRenewal</c> (the License Switching mission): the renewal date a person sets for
/// an account's subscription, the failed renewal of a sign-in a Director reports, and the "C" collation on the account
/// a switch log row is about. It proves the migration applies right after <c>AddFactoryEmailReports</c> without
/// touching an account stored before it, that the real <see cref="LicenseStore"/> then writes and reads all four new
/// columns through <see cref="GatewayDatabase"/> on PostgreSQL, and that its Down removes them again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
///
/// It sets the runtime selector <c>CC_GATEWAY_DB_CONNECTION</c> to drive GatewayDatabase down its real PostgreSQL path,
/// so it joins the collection that never runs alongside another reader of that variable.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class AddLicenseRenewalPostgresTests
{
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261012234113_AddFactoryEmailReports";
    private const string MigrationUnderTest = "20261013062011_AddLicenseRenewal";
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";

    private static string Connection => PostgresProofDatabase.Connection;

    private static GatewayDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    private static string? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar()?.ToString();
    }

    private static string ColumnsPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway'
          AND ((table_name = 'license_accounts' AND column_name = 'RenewsOn')
            OR (table_name = 'license_holdings' AND column_name IN ('RenewalFailedAtUtc', 'RenewalError', 'RenewalErrorDetail')))
        """)!;

    private static string FromAccountCollation() => Scalar("""
        SELECT coalesce(collation_name, 'default') FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'license_switch_log' AND column_name = 'FromAccountEmail'
        """)!;

    [RequiresPostgresFact]
    public void AddLicenseRenewal_AppliesOnPostgres_TheStoreWritesAndReadsTheNewColumns_AndItsDownRemovesThem()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        // An account stored before the migration.
        Scalar($"""
            INSERT INTO gateway.license_accounts ("Id", tenant_id, "AccountEmail", "Plan", "FirstSeenAtUtc", "LastSeenAtUtc")
            VALUES ('11111111-1111-1111-1111-111111111111', 'local', '{AccountA}', 'max',
                    TIMESTAMPTZ '2026-10-10 10:00:00Z', TIMESTAMPTZ '2026-10-10 10:00:00Z');
            """);
        Assert.Equal("0", ColumnsPresent());
        Assert.Equal("default", FromAccountCollation());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("4", ColumnsPresent());
        Assert.Equal("C", FromAccountCollation());
        // The account stored before it is still there, with no renewal date - none is ever made up.
        Assert.Equal("null", Scalar($"""SELECT coalesce("RenewsOn"::text, 'null') FROM gateway.license_accounts WHERE "AccountEmail" = '{AccountA}'"""));

        // The store reads today's model, so its queries run on the schema at the newest migration.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());
        }

        var priorRuntimeConn = Environment.GetEnvironmentVariable(RuntimeConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, Connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var store = new LicenseStore(db);
            var now = new DateTime(2026, 10, 11, 3, 12, 0, DateTimeKind.Utc);
            var failedAt = now.AddHours(-3);

            store.ApplyReport(TenantId.Local, "dir-1", 1, new LicenseUsageReport
            {
                MachineName = "the-windows-machine",
                OsUser = "owner",
                SentAtUtc = now,
                Trigger = LicenseReportTriggers.Interval,
                Holdings =
                {
                    Holding(AccountA, true, now),
                    Holding(AccountB, false, now, failedAt, LicenseRenewalErrors.WrongAccount, "The approving browser is signed in as another account."),
                },
            }, now, TimeSpan.Zero);

            Assert.True(store.SetRenewalDate(TenantId.Local, AccountA, new DateOnly(2026, 10, 17)));
            Assert.False(store.SetRenewalDate(TenantId.Local, "nobody@example.com", new DateOnly(2026, 10, 17)));

            var picture = new LicenseReadModel(store).Read(TenantId.Local, now);
            Assert.Equal(new DateOnly(2026, 10, 17), picture.Accounts.Single(a => a.AccountEmail == AccountA).RenewsOn);
            Assert.Null(picture.Accounts.Single(a => a.AccountEmail == AccountB).RenewsOn);
            var spare = picture.Machines.Single().Holdings.Single(h => !h.IsActive);
            Assert.Equal((failedAt, LicenseRenewalErrors.WrongAccount, "The approving browser is signed in as another account."),
                (spare.RenewalFailedAtUtc, spare.RenewalError, spare.RenewalErrorDetail));
            Assert.Equal(DateTimeKind.Utc, spare.RenewalFailedAtUtc!.Value.Kind);
            // The date is stored as a date: no time and no zone to shift it by a day.
            Assert.Equal("2026-10-17", Scalar($"""SELECT "RenewsOn"::text FROM gateway.license_accounts WHERE "AccountEmail" = '{AccountA}'"""));

            Assert.True(store.SetRenewalDate(TenantId.Local, AccountA, null));
            Assert.Null(new LicenseReadModel(store).Read(TenantId.Local, now).Accounts.Single(a => a.AccountEmail == AccountA).RenewsOn);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal: back to AddFactoryEmailReports removes the columns and the collation, and both accounts survive.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", ColumnsPresent());
        Assert.Equal("default", FromAccountCollation());
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.license_accounts"));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("4", ColumnsPresent());
        Assert.Equal("C", FromAccountCollation());
    }

    private static LicenseHolding Holding(string email, bool active, DateTime now, DateTime? renewalFailedAt = null, string? error = null, string? detail = null) => new()
    {
        AccountEmail = email,
        IsActive = active,
        Plan = "max",
        FiveHour = new LicenseWindow { Utilization = 10, ResetsAtUtc = now.AddHours(2) },
        SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = now.AddDays(4) },
        ReadAtUtc = now,
        RenewalFailedAtUtc = renewalFailedAt,
        RenewalError = error,
        RenewalErrorDetail = detail,
    };
}
