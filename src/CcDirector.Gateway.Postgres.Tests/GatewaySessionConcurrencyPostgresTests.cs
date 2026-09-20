using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The concurrency store on REAL PostgreSQL: the same scenarios as the SQLite suites, run again through the
/// provider the hosted Gateway actually uses.
///
/// This is the run that matters for the lost update. SQLite has one writer per file, so the SQLite arm of
/// the race proves the STATEMENTS are right but cannot exhibit the concurrency the hosted Gateway lives in.
/// It is also the only run that exercises the parts of the store that are provider-specific: <c>GREATEST</c>
/// (which SQLite spells <c>MAX</c>), the <c>gateway_stats</c> schema qualification in every statement, how a
/// UTC timestamp parameter is typed for a <c>timestamptz</c> column, and how the database orders text.
///
/// GATING. The whole class is gated on <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c> and reports SKIPPED when
/// it is unset, so the ordinary test run and CI are unaffected and nothing here touches a database. Stand
/// the server up with the per-caller rig, which hands out that variable:
///
///     powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance &lt;yours&gt; -Port &lt;yours&gt; -Verb up
///
/// The rig's login role holds exactly the hosted role's measured grants and no more, so a green here is a
/// green under the privileges the hosted Gateway will actually have - not under a superuser that would let
/// anything pass.
/// </summary>
public sealed class GatewaySessionConcurrencyPostgresTests : IDisposable
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    /// <summary>A Fact that skips itself when <see cref="ConnectionEnvVar"/> is unset, so the default test
    /// run (SQLite, no Postgres server) is unaffected. Setting Skip in the attribute reports the test as
    /// skipped rather than passed - a silent pass would be a green that proves nothing.</summary>
    private sealed class RequiresPostgresStatsFactAttribute : FactAttribute
    {
        public RequiresPostgresStatsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} (scripts\\pg-stats-proof-rig.ps1 -Verb up) to run the concurrency store against real PostgreSQL.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    private readonly List<ServiceProvider> _providers = new();
    private readonly string _jsonPath =
        Path.Combine(Path.GetTempPath(), "cc-conc-pg-parity-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        try { File.Delete(_jsonPath); } catch (IOException) { /* temp artifact */ }
    }

    /// <summary>A fresh, independent pooled factory against the same database - one more "container".</summary>
    private IDbContextFactory<GatewayStatsDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<GatewayStatsDbContext>(o => o.UseNpgsql(Connection));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();
    }

    /// <summary>
    /// Make sure the three tables exist in the <c>gateway_stats</c> schema, then empty them. The database is
    /// long-lived (the rig deliberately never drops it), so each test starts from empty tables rather than
    /// from whatever the previous one left - a test that quietly inherited another's rows could report a
    /// maximum it never wrote.
    /// </summary>
    private IDbContextFactory<GatewayStatsDbContext> FreshStore()
    {
        var factory = NewFactory();
        using (var ctx = factory.CreateDbContext())
        {
            // EnsureCreated also creates the schema itself, under the restricted role - which is the
            // privilege the whole step turns on. Worker 1 owns proving that properly; here it is simply the
            // cheapest way to have the tables, and it fails loud if the privilege is missing.
            ctx.Database.EnsureCreated();
            ctx.Database.ExecuteSqlRaw(
                $"DELETE FROM {GatewayStatsDbContext.PostgresSchema}.concurrency_hour_member; " +
                $"DELETE FROM {GatewayStatsDbContext.PostgresSchema}.concurrency_hour; " +
                $"DELETE FROM {GatewayStatsDbContext.PostgresSchema}.concurrency_peak;");
        }
        return factory;
    }

    [RequiresPostgresStatsFact]
    public void TheThreeTables_LandInTheStatisticsSchema_NotInPublic()
    {
        var factory = FreshStore();
        using var ctx = factory.CreateDbContext();

        // Every statement the store issues names these tables schema-qualified. If they were created in
        // public - or the default schema changed - the store's writes would fail, or worse, silently write
        // somewhere else.
        foreach (var table in new[] { "concurrency_peak", "concurrency_hour", "concurrency_hour_member" })
        {
            var inStatsSchema = ctx.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS \"Value\" FROM information_schema.tables WHERE table_schema = {0} AND table_name = {1}",
                    GatewayStatsDbContext.PostgresSchema, table)
                .AsEnumerable().Single();
            Assert.Equal(1, inStatsSchema);

            var inPublic = ctx.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {0}",
                    table)
                .AsEnumerable().Single();
            Assert.Equal(0, inPublic);
        }
    }

    [RequiresPostgresStatsFact]
    public void Upsert_KeepsTheHigherMaximum_WhenTwoContainersRaceTheSameHourAndPeak()
    {
        var factory = FreshStore();
        ConcurrencyStoreScenarios.RunTheRace(
            () => factory,
            (f, tenant) => new ConcurrencyStoreScenarios.UpsertContainer(f, tenant),
            TenantId.Local);
    }

    [RequiresPostgresStatsFact]
    public void ReadModifyWrite_LosesTheHigherMaximum_WhichIsWhyTheStoreDoesNotUseIt()
    {
        var factory = FreshStore();
        var failure = Assert.Throws<EqualException>(() => ConcurrencyStoreScenarios.RunTheRace(
            () => factory,
            (f, tenant) => new ConcurrencyStoreScenarios.ReadModifyWriteContainer(f, tenant),
            TenantId.Local));

        Assert.Contains("Expected: 8", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:   7", failure.Message, StringComparison.Ordinal);
    }

    [RequiresPostgresStatsFact]
    public void ManyContainersHammeringOneHour_AllEndAtTheTrueMaximum()
    {
        // Genuine concurrency against a server that really does run these transactions at the same time,
        // which is the condition SQLite cannot reproduce and the hosted Gateway is in on every deploy.
        var factory = FreshStore();
        const int containers = 4;
        const int observationsEach = 25;
        var stores = Enumerable.Range(0, containers)
            .Select(_ => new GatewaySessionConcurrencyStore(NewFactory()))
            .ToList();

        var trueMax = 0;
        var work = new List<Action>();
        for (var c = 0; c < containers; c++)
        {
            var store = stores[c];
            var offset = c;
            work.Add(() =>
            {
                for (var i = 0; i < observationsEach; i++)
                {
                    var live = 1 + ((offset * observationsEach) + i);
                    store.Observe(ConcurrencyStoreScenarios.Roster(live, live),
                        ConcurrencyStoreScenarios.T0.AddSeconds(i), TenantId.Local);
                }
            });
            trueMax = Math.Max(trueMax, (offset * observationsEach) + observationsEach);
        }

        Parallel.ForEach(work, action => action());

        using var ctx = factory.CreateDbContext();
        var peak = ctx.ConcurrencyPeaks.AsNoTracking().Single(p => p.Tenant == TenantId.Local.Value);
        var hour = ctx.ConcurrencyHours.AsNoTracking()
            .Single(h => h.Tenant == TenantId.Local.Value && h.HourUtc == ConcurrencyStoreScenarios.RaceHourKey);
        Assert.Equal(trueMax, peak.LiveMax);
        Assert.Equal(trueMax, hour.MaxLive);
    }

    [RequiresPostgresStatsFact]
    public void RenderedSnapshot_IsIdentical_AcrossTheWholeFixture_AndAfterBothStoresRestart()
    {
        var factory = FreshStore();
        ConcurrencyStoreScenarios.AssertOutputParityAcrossTheFixture(() => factory, _jsonPath);
    }

    [RequiresPostgresStatsFact]
    public void RenderedSnapshot_IsIdentical_OnTheRetentionBoundary()
    {
        var factory = FreshStore();
        ConcurrencyStoreScenarios.AssertOutputParityOnTheRetentionBoundary(() => factory, _jsonPath, TenantId.Local);
    }

    [RequiresPostgresStatsFact]
    public void AnHourObservedAgainAfterALaterOne_MatchesTheFileStore()
    {
        var factory = FreshStore();
        ConcurrencyStoreScenarios.AssertAnHourObservedAgainAfterALaterOneMatchesTheFileStore(
            () => factory, _jsonPath, TenantId.Local);
    }

    [RequiresPostgresStatsFact]
    public void TheParityComparison_NoticesWhenTheTwoStoresDiverge()
    {
        var factory = FreshStore();
        ConcurrencyStoreScenarios.AssertTheParityComparisonDetectsADifference(() => factory, _jsonPath, TenantId.Local);
    }
}
