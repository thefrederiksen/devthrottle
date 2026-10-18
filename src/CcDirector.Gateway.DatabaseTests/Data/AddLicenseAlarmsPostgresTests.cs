using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of <c>AddLicenseAlarms</c> (License Switching phase 6): the table of license alarms. It proves the
/// migration applies right after <c>AddFactoryDefinitionPublisher</c> without touching a Licenses log row stored before
/// it, that the real <see cref="LicenseAlarmService"/> then opens, re-raises, emails, closes and re-opens an alarm
/// through <see cref="GatewayDatabase"/> on PostgreSQL - the statements every check, every page's poll and the minute
/// check send, including the email mark into a <c>timestamp with time zone</c> column and the identity compare on the
/// byte-collated kind and account - and that its Down removes the table again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
///
/// It sets the runtime selector <c>CC_GATEWAY_DB_CONNECTION</c> to drive GatewayDatabase down its real PostgreSQL path,
/// so it joins the collection that never runs alongside another reader of that variable.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class AddLicenseAlarmsPostgresTests
{
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261016211807_AddFactoryDefinitionPublisher";
    private const string MigrationUnderTest = "20261017082512_AddLicenseAlarms";

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

    private static string TablePresent() => Scalar("""
        SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = 'license_alarms'
        """)!;

    [RequiresPostgresFact]
    public async Task AddLicenseAlarms_AppliesOnPostgres_TheServiceOpensEmailsAndClosesAnAlarm_AndItsDownRemovesIt()
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
        // A Licenses log row stored before the migration.
        Scalar("""
            INSERT INTO gateway.license_switch_log ("Id", tenant_id, "Kind", "MachineId", "MachineName", "OsUser",
                "FromAccountEmail", "ToAccountEmail", "State", "CreatedAtUtc", "SendCount", "FinishedAtUtc")
            VALUES ('44444444-4444-4444-4444-444444444441', 'local', 'spare-added', '22222222-2222-2222-2222-222222222222',
                'machine-a', 'owner', '', 'alice@example.com', 'recorded', TIMESTAMPTZ '2026-10-16 11:00:00Z', 0, TIMESTAMPTZ '2026-10-16 11:00:00Z');
            """);
        Assert.Equal("0", TablePresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("1", TablePresent());
        Assert.Equal("1", Scalar("""SELECT count(*) FROM gateway.license_switch_log"""));

        // The service reads today's model, so its queries run on the schema at the newest migration.
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
            var now = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);
            var emails = 0;
            var pushes = 0;
            var alarms = new LicenseAlarmService(db,
                (_, _, _) =>
                {
                    emails++;
                    return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
                },
                (_, opened, _) =>
                {
                    pushes += opened.Count;
                    return Task.CompletedTask;
                },
                () => now);
            var kind = new LicenseAlarmKind("not-ready-at-80", "Not ready to switch");
            var machine = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var condition = new LicenseAlarmCondition(machine, "machine-a", "alice@example.com",
                "machine-a holds no spare that could take it off alice@example.com.", "Sign machine-a in to a second account.");

            // Opens: a row, a log row, one email marked with its time, one push.
            await alarms.RaiseAsync(TenantId.Local, kind, new[] { condition }, CancellationToken.None);
            var open = Assert.Single(alarms.ListOpen(TenantId.Local));
            Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, (DateTime?)now), (open.EmailState, open.EmailSentAtUtc));
            Assert.Equal("2026-10-17 08:00:00", Scalar("""
                SELECT to_char("EmailSentAtUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM gateway.license_alarms
                """));
            Assert.Equal((1, 1), (emails, pushes));

            // Raised again, with the account in another case: the same alarm, found by its byte-collated identity.
            now = now.AddMinutes(1);
            var again = await alarms.RaiseAsync(TenantId.Local, kind,
                new[] { condition with { AccountEmail = "Alice@Example.com" } }, CancellationToken.None);
            Assert.Equal(new LicenseAlarmRaised(0, 1, 0), again);
            Assert.Equal((1, 1), (emails, pushes));

            // Cleared: it closes, and the log has its two rows beside the one stored before.
            now = now.AddMinutes(1);
            await alarms.RaiseAsync(TenantId.Local, kind, Array.Empty<LicenseAlarmCondition>(), CancellationToken.None);
            Assert.Empty(alarms.ListOpen(TenantId.Local));
            Assert.Equal("alarm-closed,alarm-opened,spare-added", Scalar("""
                SELECT string_agg("Kind", ',' ORDER BY "Kind") FROM gateway.license_switch_log
                """));

            // Raised once more: a new alarm, a new email.
            now = now.AddMinutes(1);
            await alarms.RaiseAsync(TenantId.Local, kind, new[] { condition }, CancellationToken.None);
            Assert.Single(alarms.ListOpen(TenantId.Local));
            Assert.Equal("2", Scalar("""SELECT count(*) FROM gateway.license_alarms"""));
            Assert.Equal((2, 2), (emails, pushes));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal: back to AddFactoryDefinitionPublisher removes the table, and the Licenses log survives.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", TablePresent());
        Assert.Equal("4", Scalar("""SELECT count(*) FROM gateway.license_switch_log"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("1", TablePresent());
    }
}
