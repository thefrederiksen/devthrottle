using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// The write path's batch semantics, provider-independent, plus the end-to-end replay property on the
/// self-host SQLite store.
///
/// These are the facts the port had to preserve when the statements moved to Entity Framework: one batch is
/// one tenant, an IDLE poll writes nothing at all, and replaying one snapshot any number of times leaves the
/// same numbers as replaying it once. The real-PostgreSQL half - no lost update between interleaved writers -
/// is <c>GatewayStatsWritePathPostgresTests</c> (CcDirector.Gateway.Postgres.Tests), because a lost update
/// cannot be demonstrated on a
/// single-writer SQLite file at all.
/// </summary>
public sealed class GatewayStatsWritePathTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsWritePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// A context factory that refuses to hand out a context. An empty batch must not reach it: the writer has
    /// to decide there is nothing to do BEFORE it creates a context or opens a transaction, and this is the
    /// only way to prove it did - counting statements afterwards would not distinguish "wrote nothing" from
    /// "opened a transaction and committed nothing", which is a round trip a poll every few seconds cannot
    /// afford.
    /// </summary>
    private sealed class RefusingContextFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        public GatewayStatsDbContext CreateDbContext() =>
            throw new InvalidOperationException("The writer created a context for a batch with nothing in it.");
    }

    [Fact]
    public void EmptyBatch_WritesNothing_AndNeverOpensAContext()
    {
        var writer = new GatewayStatsWriter(new RefusingContextFactory());
        var batch = new StatsWriteBatch(TenantId.Local, DateTime.UtcNow, "2026-07-30T12");

        Assert.True(batch.IsEmpty);
        var committed = writer.Commit(batch, (_, _) => throw new InvalidOperationException("nothing to resolve"));

        Assert.Empty(committed.Identities);
        Assert.Empty(committed.SessionHighWater);
        Assert.Empty(committed.AgentDrivenHighWater);
        Assert.Empty(committed.TokenHighWater);
        Assert.Equal(0, writer.StatementsExecuted);
    }

    [Fact]
    public void IdlePoll_OfAnUnchangedRoster_ExecutesNoStatements()
    {
        using var agg = new GatewayInputStatsAggregator(_path);
        var session = Session("s1", ("typed", "phone", 4, 90));

        agg.ObserveSnapshot(new[] { session });
        var afterFirstFold = agg.StatementsExecuted;
        Assert.True(afterFirstFold > 0, "the first fold must write something");

        // The same roster again, and again: nothing has changed, so the mirror answers "already recorded"
        // and the store is not touched at all.
        agg.ObserveSnapshot(new[] { session });
        agg.ObserveSnapshot(new[] { session });

        Assert.Equal(afterFirstFold, agg.StatementsExecuted);
    }

    [Fact]
    public void ReplayingOneSnapshotTenTimes_EqualsReplayingItOnce()
    {
        var snapshot = new[]
        {
            Session("s1", ("typed", "phone", 4, 90), ("voice", "phone", 2, 30)),
            Session("s2", ("typed", "desktop", 7, 210)),
        };

        long onceTurns, onceChars, onceWingman;
        var oncePath = Path.Combine(_dir, "once.db");
        using (var once = new GatewayInputStatsAggregator(oncePath))
        {
            once.ObserveSnapshot(snapshot);
            (onceTurns, onceChars) = Totals(once);
            onceWingman = once.WingmanUsage().Sessions;
        }

        using var ten = new GatewayInputStatsAggregator(_path);
        for (var i = 0; i < 10; i++) ten.ObserveSnapshot(snapshot);
        var (tenTurns, tenChars) = Totals(ten);

        Assert.Equal(onceTurns, tenTurns);
        Assert.Equal(onceChars, tenChars);
        Assert.Equal(onceWingman, ten.WingmanUsage().Sessions);
        Assert.Equal(13, tenTurns);
    }

    /// <summary>
    /// The reset rule, unchanged in meaning: a reported count LOWER than the last one seen means a Director
    /// restarted that session id and is counting fresh from zero, so the whole current count is new activity.
    /// It is now applied by the raise statement rather than by the fold against its own mirror - see
    /// <see cref="AResetSurvivesAGatewayRestart_BecauseTheStoreAndTheMirrorAgreeAboutIt"/> for why that
    /// relocation is the fix and not a detail.
    /// </summary>
    [Fact]
    public void ADroppedCount_StillFoldsAsFreshActivity()
    {
        using var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(Session("s1", ("typed", "phone", 10, 300)));
        agg.Observe(Session("s1", ("typed", "phone", 3, 40)));  // the Director restarted this session id

        var (turns, chars) = Totals(agg);
        Assert.Equal(13, turns);
        Assert.Equal(340, chars);
    }

    /// <summary>
    /// THE RESET DEFECT, and the fact that says it is gone. Folding a reset correctly ONCE is not enough - the
    /// stored row has to agree with the mirror about what happened, because the mirror is thrown away on every
    /// Gateway restart and rebuilt from that row.
    ///
    /// It used to disagree, and silently. The fold noticed the drop and counted the restarted session's whole
    /// tally, then set its mirror to the new low count - while the stored watermark, raised by a comparison
    /// that only ever moves upward, kept the pre-restart high one. Two different numbers for one fact, and the
    /// stored one wins the moment the Gateway restarts: the rebuilt mirror measures the restarted session's
    /// growth from a watermark it already passed, so every turn between the reset and the old high mark is
    /// counted twice on the way there and lost on the way back.
    ///
    /// Here: ten turns, a restart to three, then growth to five. The honest all-time total is 10 + 3 + 2 = 15,
    /// and it has to survive the aggregator being torn down and rebuilt from the file in between.
    /// </summary>
    [Fact]
    public void AResetSurvivesAGatewayRestart_BecauseTheStoreAndTheMirrorAgreeAboutIt()
    {
        using (var before = new GatewayInputStatsAggregator(_path))
        {
            before.Observe(Session("s1", ("typed", "phone", 10, 300)));
            before.Observe(Session("s1", ("typed", "phone", 3, 40)));   // the Director restarted this session
            Assert.Equal(13, Totals(before).Turns);
        }

        // A new aggregator has no memory at all: whatever it believes now, it read out of the stored row.
        using var after = new GatewayInputStatsAggregator(_path);
        after.Observe(Session("s1", ("typed", "phone", 5, 60)));        // the restarted session grows by two

        var (turns, chars) = Totals(after);
        Assert.Equal(15, turns);
        Assert.Equal(360, chars);
    }

    /// <summary>
    /// The other half of the same property: the per-agent tally is attributed from the SAME difference the
    /// session bucket got, so the two can never disagree about how much a session did. They used to be two
    /// separate subtractions against the same mirror, which agreed only for as long as both kept computing
    /// the same thing.
    /// </summary>
    [Fact]
    public void ThePerAgentTally_MatchesTheSessionTotals_AcrossAResetAndARestart()
    {
        using (var before = new GatewayInputStatsAggregator(_path))
        {
            before.Observe(Agent(Session("s1", ("typed", "phone", 10, 300))));
            before.Observe(Agent(Session("s1", ("typed", "phone", 3, 40))));
        }

        using var after = new GatewayInputStatsAggregator(_path);
        after.Observe(Agent(Session("s1", ("typed", "phone", 5, 60))));

        var (turns, chars) = Totals(after);
        var agent = Assert.Single(after.AgentTotals());
        Assert.Equal(turns, agent.Turns);
        Assert.Equal(chars, agent.Characters);
    }

    /// <summary>A model a Director never named is stored as SQL NULL, not as an identity spelled "". The port
    /// moved that through Entity Framework's nullable mapping instead of a DBNull parameter, so it is worth
    /// one test that the null bucket is still a real, separate bucket.</summary>
    [Fact]
    public void AnUnnamedModel_StaysNull_AndNeverBecomesAnIdentity()
    {
        using var agg = new GatewayInputStatsAggregator(_path);

        var unnamed = Session("s1", ("typed", "phone", 2, 20));
        var named = Session("s2", ("typed", "phone", 3, 30));
        named.CurrentModel = "claude-opus-5";
        agg.ObserveSnapshot(new[] { unnamed, named });

        var models = agg.ModelTotals();
        Assert.Equal(2, models.Count);
        Assert.Contains(models, m => m.Model is null && m.Turns == 2);
        Assert.Contains(models, m => m.Model == "claude-opus-5" && m.Turns == 3);
    }

    /// <summary>
    /// Retention on the SELF-HOST provider: the expired detail leaves, every turn it carried stays, and the
    /// working-day series stops claiming an hour that is no longer there.
    ///
    /// The PostgreSQL half of this is in <c>GatewayStatsWritePathPostgresTests</c>
    /// (CcDirector.Gateway.Postgres.Tests). It is worth having
    /// BOTH, because the sweep is now a <c>DELETE ... RETURNING</c> whose rows are folded into the archive in
    /// memory, and SQLite is the provider with the sharp edge there: it may emit RETURNING rows while the
    /// statement is still running, and touching the same table mid-read is undefined. One implementation over
    /// two providers is only a claim until it has been run twice.
    /// </summary>
    [Fact]
    public void Pruning_KeepsEveryTurn_AndStopsClaimingTheExpiredHour()
    {
        using var agg = new GatewayInputStatsAggregator(_path);
        var now = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        var longAgo = now.AddDays(-200);

        // Folded at a moment when it was not yet expired, so nothing prunes it on the way in.
        agg.Observe(Session("s-old", ("typed", "phone", 7, 70)), longAgo);
        // This fold's own retention sweep is what takes it.
        agg.Observe(Session("s-new", ("typed", "phone", 5, 50)), now);

        // Twelve turns went in and twelve are still counted - the all-time total does not shrink when the
        // detail behind it is pruned, which is the whole reason departing rows are folded rather than dropped.
        var (turns, chars) = Totals(agg);
        Assert.Equal(12, turns);
        Assert.Equal(120, chars);

        // But the working-day series only knows about the hour that is still real. An archive row is all-time
        // data with no honest hour, and letting it through here would invent an hour of the working day.
        var hours = agg.HourlyTurns();
        Assert.Equal("2026-07-30T12", Assert.Single(hours).Hour);
        Assert.Equal(5, hours[0].Turns);
    }

    private static (long Turns, long Chars) Totals(GatewayInputStatsAggregator agg)
    {
        var dto = agg.CurrentTotals();
        return (dto.Buckets.Sum(b => b.Turns), dto.Buckets.Sum(b => b.Characters));
    }

    private static SessionDto Agent(SessionDto session, string agent = "ClaudeCode")
    {
        session.Agent = agent;
        return session;
    }

    private static SessionDto Session(string id, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = new SessionDto { SessionId = id, InputStats = new InputStatsDto() };
        foreach (var b in buckets)
            dto.InputStats!.Buckets.Add(new InputStatBucketDto
            {
                Modality = b.modality,
                Surface = b.surface,
                Turns = b.turns,
                Characters = b.chars,
            });
        return dto;
    }
}
