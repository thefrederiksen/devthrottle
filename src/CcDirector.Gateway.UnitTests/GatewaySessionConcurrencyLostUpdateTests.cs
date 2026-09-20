using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Stats;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Sdk;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LOST-UPDATE PROOF, on SQLite. Every maximum in the concurrency store - both all-time peaks and all
/// five per-hour figures - is written with an explicit <c>ON CONFLICT DO UPDATE ... GREATEST</c> rather than
/// a change-tracked read-then-save. This file is the evidence that the difference is real and that the
/// assertion which claims it can actually fail.
///
/// The race is the one the hosted Gateway is in every time it deploys: a slot swap runs TWO containers
/// against ONE store, both folding the same <c>/sessions</c> roster. Each has its own in-memory picture, so
/// each decides what to write from a view of the store taken before the other wrote.
///
/// The interleaving and the assertion live once, in <see cref="ConcurrencyStoreScenarios.RunTheRace"/>, and
/// are run here twice:
///
///  - against the real store, the higher maximum survives (the test the store has to pass);
///  - against a deliberately change-tracked read-modify-write writer, the SAME assertion fails - and that
///    failure is asserted here, in the committed suite, so the proof is not a claim about a red somebody saw
///    once on their own machine. If that writer were ever "fixed", this test turns red and says so.
///
/// Both arms also run against REAL PostgreSQL, in <c>GatewaySessionConcurrencyPostgresTests</c>
/// (CcDirector.Gateway.Postgres.Tests),
/// which is where the property actually matters: single-writer SQLite on one machine never exposed it.
/// </summary>
public sealed class GatewaySessionConcurrencyLostUpdateTests : IDisposable
{
    private readonly StatsConcurrencyTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly TenantId Tenant = TenantId.Local;

    [Fact]
    public void Upsert_KeepsTheHigherMaximum_WhenTwoContainersRaceTheSameHourAndPeak()
    {
        ConcurrencyStoreScenarios.RunTheRace(
            () => _db.NewFactory(),
            (factory, tenant) => new ConcurrencyStoreScenarios.UpsertContainer(factory, tenant),
            Tenant);
    }

    [Fact]
    public void ReadModifyWrite_LosesTheHigherMaximum_WhichIsWhyTheStoreDoesNotUseIt()
    {
        // The SAME race and the SAME assertion, run against the change-tracked read-then-save writer. It
        // fails - that is the point. This is the red that makes the green above mean something, kept in the
        // suite rather than reported as something a worker once watched happen.
        var failure = Assert.Throws<EqualException>(() => ConcurrencyStoreScenarios.RunTheRace(
            () => _db.NewFactory(),
            (factory, tenant) => new ConcurrencyStoreScenarios.ReadModifyWriteContainer(factory, tenant),
            Tenant));

        // And it fails for the RIGHT reason: the maximum that was actually reached is not the one stored.
        // Without this, any failure at all - a missing row, a schema fault - would satisfy the test above
        // and it would report "the naive writer loses updates" on evidence of something else entirely.
        Assert.Contains("Expected: 8", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:   7", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyContainersHammeringOneHour_AllEndAtTheTrueMaximum()
    {
        // The deterministic interleaving above is the proof; this is the same property under real threads,
        // which is how it will actually happen. Four containers, each folding rosters of a different size at
        // the same hour, all writing concurrently. The store must end at the largest roster any of them saw.
        const int containers = 4;
        const int observationsEach = 25;
        var stores = Enumerable.Range(0, containers)
            .Select(_ => new GatewaySessionConcurrencyStore(_db.NewFactory()))
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
                        ConcurrencyStoreScenarios.T0.AddSeconds(i), Tenant);
                }
            });
            trueMax = Math.Max(trueMax, (offset * observationsEach) + observationsEach);
        }

        Parallel.ForEach(work, action => action());

        using var ctx = _db.NewFactory().CreateDbContext();
        var peak = ctx.ConcurrencyPeaks.AsNoTracking().Single(p => p.Tenant == Tenant.Value);
        var hour = ctx.ConcurrencyHours.AsNoTracking()
            .Single(h => h.Tenant == Tenant.Value && h.HourUtc == ConcurrencyStoreScenarios.RaceHourKey);
        Assert.Equal(trueMax, peak.LiveMax);
        Assert.Equal(trueMax, hour.MaxLive);
    }
}
