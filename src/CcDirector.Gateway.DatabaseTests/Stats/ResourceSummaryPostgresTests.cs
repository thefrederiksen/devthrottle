using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// The resource summary tables on PostgreSQL (Mission: Resource Monitor, section 6): the PostgreSQL migration chain
/// builds them, and the store and the hourly roll-up - whose queries the SQLite unit tests have already proved on the
/// other provider - store, roll up and delete on the hosted provider too: timestamp with time zone, the "C" collation,
/// the tenant allowlist check, and the bulk deletes.
///
/// It reads <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c>, the restricted-role statistics connection the rig hands out,
/// and is gated by [RequiresPostgresFact]: it runs under scripts/test-database.ps1 against the throwaway database that
/// run built, and reports SKIPPED anywhere else. It refuses any database whose name does not start with "ccpg", and it
/// drops and recreates the gateway_stats schema.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceSummaryPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";
    private static readonly DateTime W0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = new("0b9f7c1e-4a51-4c1e-9d8c-2f7a3b1c9e44");
    private const long Gb = 1_000_000_000;

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    /// <summary>A clock the test sets. The store and the roll-up only ever ask it the time.</summary>
    private sealed class SetClock : TimeProvider
    {
        public SetClock(DateTime utc) => Now = new DateTimeOffset(utc);
        public DateTimeOffset Now { get; private set; }
        public void SetUtcNow(DateTimeOffset value) => Now = value;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MigratingFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        // The hosted options: the PostgreSQL chain's assembly and its history table in the statistics schema.
        private readonly DbContextOptions<GatewayStatsDbContext> _options =
            new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            }).Options;

        public GatewayStatsDbContext CreateDbContext() => new(_options);
    }

    /// <summary>Drop the statistics schema and build it again by RUNNING the PostgreSQL migration chain.</summary>
    private static IDbContextFactory<GatewayStatsDbContext> MigratedFromEmpty()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop the statistics schema in '{database}': its name must begin with the throwaway prefix 'ccpg'.");
        var factory = new MigratingFactory();
        using var ctx = factory.CreateDbContext();
        ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
        ctx.Database.Migrate();
        return factory;
    }

    private static ResourceSummaryWindow Window(DateTime start, bool redSpike)
    {
        var share = new ResourceSummarySessionSample("s-1", "Biographies", 30, 2 * Gb, null);
        return ResourceSummaryBuilder.Build(start, "devlinux", Enumerable.Range(0, 60).Select(i =>
        {
            var spike = redSpike && i >= 24 && i < 42;
            return new ResourceSummarySample(start.AddSeconds(5 * i), spike ? 99 : 20, null,
                spike ? ResourceLevel.Critical : ResourceLevel.Normal, 8 * Gb, 16 * Gb, null, ResourceLevel.Normal, new[] { share });
        }));
    }

    [RequiresPostgresFact]
    public void TheChainBuildsTheTables_AndTheyStoreRollUpAndDeleteOnPostgres()
    {
        var factory = MigratedFromEmpty();
        var clock = new SetClock(W0.AddMinutes(30));
        var store = new ResourceSummaryStore(() => factory, clock);

        // Stored once, even when sent twice; the spike survives.
        var batch = new ResourceSummaryBatch { Windows = { Window(W0, redSpike: false), Window(W0.AddMinutes(5), redSpike: true) } };
        Assert.Equal(2, store.Store(Tenant, "director-1", batch).HeldWindowStartsUtc.Count);
        Assert.Equal(2, store.Store(Tenant, "director-1", batch).HeldWindowStartsUtc.Count);
        var rows = store.MachineWindows(Tenant, W0, W0.AddHours(1))!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(DateTimeKind.Utc, rows[0].WindowStartUtc.Kind);
        Assert.Equal(99, rows[1].ProcessorPeakPercent);
        Assert.Equal(ResourceSummaryContract.Red, Assert.Single(ResourceSummaryStore.CrossingsOf(rows[1])).Level);
        Assert.Equal(2, store.SessionWindows(Tenant, "devlinux", W0, W0.AddHours(1))!.Count);

        // Rolled up once the hour is over, then nothing more to roll.
        clock.SetUtcNow(new DateTimeOffset(W0.AddHours(1).AddMinutes(1)));
        var rollup = new ResourceSummaryRollup(() => factory, clock);
        Assert.Equal(1, rollup.RunOnce().HoursRolled);
        Assert.Equal(0, rollup.RunOnce().HoursRolled);
        using (var db = factory.CreateDbContext())
        {
            var hour = db.ResourceHours.Single();
            Assert.Equal(2, hour.WindowCount);
            Assert.Equal(99, hour.ProcessorPeakPercent);
            Assert.Equal("Biographies", db.ResourceSessionHours.Single().Factory);
        }

        // Past 90 days the five-minute rows go; past two years the hourly rows go.
        clock.SetUtcNow(new DateTimeOffset(W0.AddDays(90).AddHours(1)));
        var past90 = rollup.RunOnce();
        Assert.Equal(2, past90.WindowsDeleted);
        Assert.Equal(2, past90.SessionWindowsDeleted);
        clock.SetUtcNow(new DateTimeOffset(W0.AddDays(730).AddHours(1)));
        var pastTwoYears = rollup.RunOnce();
        Assert.Equal(1, pastTwoYears.HoursDeleted);
        Assert.Equal(1, pastTwoYears.SessionHoursDeleted);
    }

    [RequiresPostgresFact]
    public void ATenantOutsideTheAllowlist_IsRefusedByTheSchema()
    {
        var factory = MigratedFromEmpty();
        var store = new ResourceSummaryStore(() => factory, new SetClock(W0.AddMinutes(30)));

        // The same tenant check every gateway_stats table carries: a spelling no production mint produces is refused.
        Assert.ThrowsAny<DbUpdateException>(() =>
            store.Store(new TenantId("Not A Tenant"), "director-1", new ResourceSummaryBatch { Windows = { Window(W0, redSpike: false) } }));
    }
}
