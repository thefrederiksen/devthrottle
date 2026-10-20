using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of <c>AddTriggerBackupMachines</c> (Mail Desk, phase 5): <c>triggers</c> gains
/// <c>BackupMachinesJson</c> and <c>ClaimedByMachine</c>. It proves the migration applies right after
/// <c>AddApiModeReceiptPromptText</c>, gives a claim held before it the trigger's own machine and an unclaimed trigger
/// none, that the real <see cref="TriggerStore"/> then claims for a backup and hands the check back through
/// <see cref="GatewayDatabase"/> on PostgreSQL, and that its Down removes both columns and keeps the rows.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class AddTriggerBackupMachinesPostgresTests
{
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";
    private const string MigrationBefore = "20261018153213_AddMailDesk";
    private const string MigrationUnderTest = "20261018182501_AddTriggerBackupMachines";
    private const string Held = "11111111-1111-1111-1111-111111111111";

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

    private static string Columns() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'triggers' AND column_name IN ('BackupMachinesJson', 'ClaimedByMachine')
        """)!;

    [RequiresPostgresFact]
    public void AddTriggerBackupMachines_AppliesOnPostgres_TheStoreHandsACheckToABackupAndBack_AndItsDownRemovesIt()
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
        // Two definition triggers stored before it: one held by a Director on its machine, one never claimed.
        Scalar($"""
            INSERT INTO gateway.triggers ("Id", tenant_id, "Name", "Factory", "FactoryAgent", "Machine", "RepoPath", "CheckCommand",
                "IntervalSeconds", "Prompt", "Paused", "CreatedBy", "CreatedUtc", "ClaimedByDirectorId", "ClaimedUtc", "CheckPath", "Seat")
            VALUES ('{Held}', 'local', 'held', 'f', 'desk', 'machine-a', '', '', 600, 'p', false,
                    'test', TIMESTAMPTZ '2026-10-18 09:00:00Z', 'dir-a', TIMESTAMPTZ '2026-10-18 09:05:00Z', 'checks/new_mail.py', 'desk'),
                   ('22222222-2222-2222-2222-222222222222', 'local', 'free', 'f', 'desk', 'machine-b', '', '', 600, 'p', false,
                    'test', TIMESTAMPTZ '2026-10-18 09:00:00Z', '', NULL, 'checks/new_mail.py', 'desk');
            """);
        Assert.Equal("0", Columns());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("2", Columns());
        Assert.Equal("free|[]|,held|[]|machine-a", Scalar("""
            SELECT string_agg("Name" || '|' || "BackupMachinesJson" || '|' || "ClaimedByMachine", ',' ORDER BY "Name") FROM gateway.triggers
            """));

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate();
            Assert.False(ctx.Database.HasPendingModelChanges());
        }

        // The store, on PostgreSQL: give the held trigger a backup; the backup takes the lapsed claim, and the trigger's
        // own machine takes it back while the backup's claim is live.
        Scalar($"""UPDATE gateway.triggers SET "BackupMachinesJson" = '["machine-b"]' WHERE "Id" = '{Held}'""");
        var priorRuntimeConn = Environment.GetEnvironmentVariable(RuntimeConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, Connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var store = new TriggerStore(db);
            var afterLapse = new DateTime(2026, 10, 18, 9, 5, 0, DateTimeKind.Utc).Add(TriggerStore.ClaimLapse(600)).AddSeconds(1);

            var taken = store.ClaimForDirector(TenantId.Local, "dir-b", "machine-b", afterLapse, runsDefinitionChecks: true);
            Assert.Equal(new[] { "free", "held" }, taken.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("dir-b|machine-b", Scalar($"""SELECT "ClaimedByDirectorId" || '|' || "ClaimedByMachine" FROM gateway.triggers WHERE "Id" = '{Held}'"""));

            var back = store.ClaimForDirector(TenantId.Local, "dir-a", "machine-a", afterLapse.AddSeconds(30), runsDefinitionChecks: true);
            Assert.Equal("held", Assert.Single(back).Name);
            Assert.Equal("dir-a|machine-a", Scalar($"""SELECT "ClaimedByDirectorId" || '|' || "ClaimedByMachine" FROM gateway.triggers WHERE "Id" = '{Held}'"""));
            Assert.Equal("free", Assert.Single(store.ClaimForDirector(TenantId.Local, "dir-b", "machine-b", afterLapse.AddSeconds(60),
                runsDefinitionChecks: true)).Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
        }

        // The reversal removes both columns, and both triggers survive.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", Columns());
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.triggers"));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("2", Columns());
    }
}
