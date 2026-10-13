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
/// The PostgreSQL half of <c>AddLicenseSwitchHold</c> (the License Switching mission): the time a machine is held out
/// of the automatic switch until, kept on the failed switch row that earned it. It proves the migration applies right
/// after <c>AddLicenseRenewal</c> without touching a switch log row stored before it, that the real
/// <see cref="LicenseStore"/> then writes the hold and reads the holds in force through <see cref="GatewayDatabase"/>
/// on PostgreSQL - the two statements the once-a-minute check and every read of the Licenses page send - and that its
/// Down removes the column again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
///
/// It sets the runtime selector <c>CC_GATEWAY_DB_CONNECTION</c> to drive GatewayDatabase down its real PostgreSQL path,
/// so it joins the collection that never runs alongside another reader of that variable.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class AddLicenseSwitchHoldPostgresTests
{
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261013062011_AddLicenseRenewal";
    private const string MigrationUnderTest = "20261013223228_AddLicenseSwitchHold";
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";
    private const string OldMachine = "22222222-2222-2222-2222-222222222222";
    private const string FirstFailure = "0123456789abcdef0123456789abcde1";
    private const string SecondFailure = "0123456789abcdef0123456789abcde2";

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

    private static string ColumnPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'license_switch_log' AND column_name = 'HeldUntilUtc'
        """)!;

    [RequiresPostgresFact]
    public void AddLicenseSwitchHold_AppliesOnPostgres_TheStoreWritesAndReadsTheHold_AndItsDownRemovesIt()
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
        // A machine, and two switches of it that failed the same way one after the other, stored before the migration.
        Scalar($"""
            INSERT INTO gateway.license_machines ("Id", tenant_id, "MachineName", "OsUser", "MachineKey", "OsUserKey", "DirectorId",
                "LastSequence", "LastReportAtUtc", "LastSentAtUtc", "LastTrigger", "ActiveAccountEmail", "ClockSkewSeconds")
            VALUES ('{OldMachine}', 'local', 'the-old-machine', 'owner', 'the-old-machine', 'owner', 'dir-old',
                1, TIMESTAMPTZ '2026-10-10 10:00:00Z', TIMESTAMPTZ '2026-10-10 10:00:00Z', 'interval', '{AccountA}', 0);
            INSERT INTO gateway.license_switch_log ("Id", tenant_id, "Kind", "SwitchId", "MachineId", "MachineName", "OsUser",
                "FromAccountEmail", "ToAccountEmail", "Reason", "State", "ReasonCode", "CreatedAtUtc", "SendCount", "FinishedAtUtc")
            VALUES ('44444444-4444-4444-4444-444444444441', 'local', 'switch', '{FirstFailure}', '{OldMachine}', 'the-old-machine', 'owner',
                '{AccountA}', '{AccountB}', 'threshold', 'failed', 'exchange-failed-restored', TIMESTAMPTZ '2026-10-10 11:00:00Z', 1, TIMESTAMPTZ '2026-10-10 11:00:05Z'),
                   ('44444444-4444-4444-4444-444444444442', 'local', 'switch', '{SecondFailure}', '{OldMachine}', 'the-old-machine', 'owner',
                '{AccountA}', '{AccountB}', 'threshold', 'failed', 'exchange-failed-restored', TIMESTAMPTZ '2026-10-10 11:01:00Z', 1, TIMESTAMPTZ '2026-10-10 11:01:05Z');
            """);
        Assert.Equal("0", ColumnPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("1", ColumnPresent());
        // The rows stored before it are still there, and neither carries a hold - none is ever made up.
        Assert.Equal("2|0", Scalar("""
            SELECT count(*)::text || '|' || count("HeldUntilUtc")::text FROM gateway.license_switch_log WHERE "Kind" = 'switch'
            """));

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
            var machine = Guid.Parse(OldMachine);
            var secondEnded = new DateTime(2026, 10, 10, 11, 1, 5, DateTimeKind.Utc);
            Assert.Empty(store.ListHolds(TenantId.Local, secondEnded));

            // The first failure of a run holds nothing; the second is held one minute from when it ended, on its row.
            Assert.Null(store.HoldAfterRepeatedFailure(TenantId.Local, FirstFailure));
            var until = store.HoldAfterRepeatedFailure(TenantId.Local, SecondFailure);
            Assert.Equal(secondEnded.AddMinutes(1), until);
            Assert.Equal(DateTimeKind.Utc, until!.Value.Kind);
            Assert.Equal("2026-10-10 11:02:05", Scalar($"""
                SELECT to_char("HeldUntilUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM gateway.license_switch_log WHERE "SwitchId" = '{SecondFailure}'
                """));

            // The holds in force: the machine's latest switch row, while its time is ahead.
            var held = Assert.Single(store.ListHolds(TenantId.Local, secondEnded.AddSeconds(59)));
            Assert.Equal((SecondFailure, machine, AccountA, AccountB, LicenseSwitchReasonCodes.ExchangeFailedRestored),
                (held.SwitchId, held.MachineId, held.FromAccountEmail, held.ToAccountEmail, held.ReasonCode));
            Assert.Empty(store.ListHolds(TenantId.Local, secondEnded.AddMinutes(1)));
            // And the engine's picture carries it, through the one place that picture is built.
            var state = Assert.Single(new LicenseSnapshotSource(store, new LicenseReadModel(store))
                .Read(TenantId.Local, secondEnded.AddSeconds(30), Array.Empty<LicenseExhaustion>()).Machines);
            Assert.Equal(new LicenseHold(secondEnded.AddMinutes(1), AccountA, AccountB, LicenseSwitchReasonCodes.ExchangeFailedRestored), state.Hold);

            // A later switch row of the machine ends the hold, whatever its state.
            Assert.NotNull(store.CreatePendingSwitch(TenantId.Local, machine, AccountA, AccountB, LicenseSwitchReasons.Manual, secondEnded.AddSeconds(40)));
            Assert.Empty(store.ListHolds(TenantId.Local, secondEnded.AddSeconds(45)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal: back to AddLicenseRenewal removes the column, and every switch row survives.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", ColumnPresent());
        Assert.Equal("3", Scalar("""SELECT count(*) FROM gateway.license_switch_log WHERE "Kind" = 'switch'"""));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("1", ColumnPresent());
    }
}
