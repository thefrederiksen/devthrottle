using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// PROOF ROW 2: interleaved writers on the statistics write path never lose an update AND never count one
/// twice, against a REAL PostgreSQL server.
///
/// WHAT THIS SUITE ASSERTS, AND WHY IT IS NOT WHAT IT USED TO ASSERT. The first version of these facts
/// checked WATERMARKS: after two writers race, is the stored high-water the higher of the two values? That
/// number was always going to be right - the raise compares, so it cannot go backwards - and it is not where
/// the store's numbers live. The numbers live in the APPEND-ONLY DELTA LEDGER, which every all-time total is
/// the sum of, and that ledger was always going to be wrong: each writer computed its growth against its OWN
/// in-memory mirror and appended that growth to the shared ledger, so two writers measuring from two stale
/// baselines appended MORE in total than the watermark ever moved. The watermark assertion passed; the totals
/// inflated on every interleave.
///
/// So the assertion carried by EVERY interleave below is the one that was missing:
///
///     AFTER ANY INTERLEAVE, THE SUM OF APPENDED DELTAS EQUALS FINAL WATERMARK MINUS INITIAL WATERMARK.
///
/// It is checked inside <see cref="InterleaveAndCommit"/> itself rather than written out beside each
/// watermark check, so no fact can be added here that forgets it. It holds because the raise statement
/// returns what the row held before it and what it holds after, and the writer appends exactly that
/// difference - the identity is by construction, not by care.
///
/// THE RACE IS CAUSED, NOT WITNESSED. A concurrency test that has never been watched failing proves nothing:
/// it passes just as happily when the race never happened. An earlier version of this suite tried to prove
/// the race by OBSERVING the server - and a review showed that a witness can be strengthened in every
/// dimension and still witness the WRONG EVENT, by making the second writer touch no watermark row at all and
/// watching the fact pass on an unrelated identity-row block. So <see cref="InterleaveAndCommit"/> now
/// CONSTRUCTS the interleave through seams inside the writer: it is told when the second writer reaches THE
/// ROW UNDER TEST, and a writer that never reaches it cannot get past that point. The one observational check
/// that survives is bound to the contested RELATION, and
/// <see cref="TheRelationCheck_RefusesToCertify_AnUnrelatedLock"/> is what keeps it honest.
///
/// Gated behind <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c> - the RESTRICTED-role, statistics-database
/// connection string that <c>scripts/pg-stats-proof-rig.ps1</c> hands out, whose role holds exactly the
/// hosted role's measured grants and nothing more. With it unset (the ordinary SQLite test run and CI) every
/// fact reports SKIPPED and nothing touches a database. Point it at a THROWAWAY local container - never at
/// the hosted database, which staging shares with production; the guard below refuses any database whose name
/// does not start with "ccpg", and the suite drops and recreates the whole gateway_stats schema.
///
/// Stand the rig up with your OWN instance and port - one instance per caller, so no two agents share a
/// server and nobody's deliberate red is somebody else's privilege change:
///   powershell -NoProfile -File scripts/pg-stats-proof-rig.ps1 -Instance w4 -Port 55434 -Verb up
/// </summary>
public sealed class GatewayStatsWritePathPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";
    private const string Session = "s-highwater";
    private const string Repo = "thefrederiksen/devthrottle";
    private const string Checkout = "D:\\ReposFred\\devthrottle";
    private const string TheAgent = "ClaudeCode";
    private static readonly TenantId Tenant = TenantId.Local;

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} (scripts/pg-stats-proof-rig.ps1) to run the write-path proof on real PostgreSQL.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    // ---- The rig -------------------------------------------------------------------------------------

    /// <summary>A context per operation over the Npgsql connection pool - the hosted shape, where a context
    /// is short-lived and the CONNECTION is what is pooled.</summary>
    private sealed class NpgsqlStatsContextFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        private readonly DbContextOptions<GatewayStatsDbContext> _options =
            new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(Connection).Options;

        public GatewayStatsDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// A factory pinned to ONE caller-owned connection, so the test knows exactly which server backend a
    /// writer's statements run on.
    ///
    /// That is the whole reason it exists: the race witness has to name WRITER B'S OWN BACKEND, and a pooled
    /// factory hands out whichever connection is free. With the connection pinned, the test can ask it for
    /// its backend process id BEFORE the writer starts and then, from a third connection, ask the server
    /// about that exact backend while it is blocked and unable to answer for itself.
    /// </summary>
    private sealed class PinnedConnectionFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        private readonly DbContextOptions<GatewayStatsDbContext> _options;

        public PinnedConnectionFactory(NpgsqlConnection connection) =>
            _options = new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(connection).Options;

        public GatewayStatsDbContext CreateDbContext() => new(_options);
    }

    private static IDbContextFactory<GatewayStatsDbContext> Factory() => new NpgsqlStatsContextFactory();

    private static NpgsqlConnection OpenPinned(out int backendPid)
    {
        var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_backend_pid()";
        backendPid = Convert.ToInt32(cmd.ExecuteScalar());
        return connection;
    }

    /// <summary>
    /// Drop and recreate the statistics schema from the model. Every fact starts from an empty store, so no
    /// fact can pass on a row another one left behind.
    ///
    /// The tables are created from <see cref="GatewayStatsDbContext"/> itself rather than by a migration
    /// because the migration chain is a different worker's piece and lands separately; the SHAPE is the same
    /// model either way, which is what these facts are about. It also proves something worth having on its
    /// own: a role holding only CREATE on the database can create this schema, its tables and its indexes -
    /// including the (tenant, spelling) unique indexes the identity mints now conflict against.
    /// </summary>
    private static void ResetSchema()
    {
        GuardThrowawayDatabase();
        using var ctx = Factory().CreateDbContext();
        ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
        ctx.Database.ExecuteSqlRaw($"CREATE SCHEMA {GatewayStatsDbContext.PostgresSchema}");
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript());
    }

    private static void GuardThrowawayDatabase()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop the statistics schema in the database '{database}': its name must begin " +
                $"with the throwaway prefix 'ccpg'. Point {ConnectionEnvVar} at a disposable local database " +
                "(the rig's ccpgstats_<instance>). The hosted database is shared with production and is never a test target.");
        if (Connection.Contains("supabase", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Refusing to run the write-path proof against a Supabase host. Local throwaway containers only.");
    }

    private static StatsWriteBatch NewBatch() =>
        new(Tenant, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc), "2026-07-30T12");

    /// <summary>The caller's identity mirror must never be consulted: every batch here queues the spellings it
    /// uses, so the writer resolves them itself against the store. A call to this is a real defect.</summary>
    private static long ResolveNothing(string display, IdentityKind kind) =>
        throw new InvalidOperationException($"No identity should need resolving here ({kind}: {display}).");

    // Queue the identities every delta row a bucket observation can produce needs, so a growing observation
    // can be filed without the caller's mirror. Minting is an upsert that returns the winning id, so a second
    // batch naming the same spellings simply resolves to the same ids - which is itself worth exercising on
    // every fact rather than in one place.
    private static void WithIdentities(StatsWriteBatch batch)
    {
        batch.NewIdentities.Add((Repo, IdentityKind.Repo));
        batch.NewIdentities.Add((Checkout, IdentityKind.Checkout));
        batch.NewIdentities.Add((TheAgent, IdentityKind.Agent));
    }

    private static StatsWriteBatch.BucketObservation Bucket(
        long reportedTurns, long reportedChars, long believedTurns, long believedChars, long believedGeneration = 0) =>
        new(Session, "typed", "phone", false, Repo, Checkout, null, false, TheAgent,
            reportedTurns, reportedChars, believedTurns, believedChars, believedGeneration);

    /// <summary>The row key the write path names when it is about to raise the session watermark under test -
    /// what the <c>BeforeRaise</c> seam reports, and what the interleave facts require to have been reached.</summary>
    private const string SessionRow = "session_highwater:" + Session + "/typed/phone";
    private const string AgentDrivenRow = "agent_driven_highwater:" + Session;
    private const string TokenRow = "token_highwater:" + Session;

    private static string Relation(string table) => GatewayStatsDbContext.PostgresSchema + "." + table;

    private static StatsCommitResult Write(IDbContextFactory<GatewayStatsDbContext> factory, Action<StatsWriteBatch> fill)
    {
        var batch = NewBatch();
        WithIdentities(batch);
        fill(batch);
        return new GatewayStatsWriter(factory).Commit(batch, ResolveNothing);
    }

    /// <summary>
    /// A resolver over identities that are ALREADY minted, standing in for the aggregator's mirror.
    ///
    /// The interleave facts use this so their racing batches queue NO identities at all. That is not
    /// tidiness - it is the difference between racing the row the fact claims to race and racing whatever
    /// happens to be locked first. Identity upserts run BEFORE any watermark raise, so a batch that mints
    /// makes writer B block on writer A's IDENTITY row, and a review proved a fact could then pass with
    /// writer B doing no watermark work whatsoever. With the spellings pre-minted, the only row either writer
    /// touches is the watermark row under test.
    /// </summary>
    private static Func<string, IdentityKind, long> ResolverFor(StatsCommitResult seeded) =>
        (display, kind) => seeded.Identities[kind][display];

    private static long ReadOne(IDbContextFactory<GatewayStatsDbContext> factory, Func<GatewayStatsDbContext, long> read)
    {
        using var ctx = factory.CreateDbContext();
        return read(ctx);
    }

    // ---- Proof row 2: no lost update AND no double count, on all three high-water paths ---------------

    [RequiresPostgresFact]
    public void SessionHighWater_InterleavedWriters_KeepTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        var seeded = Write(factory, b => b.Buckets.Add(Bucket(5, 50, 0, 0)));

        InterleaveAndCommit(
            factory,
            ResolverFor(seeded), SessionRow, Relation("session_highwater"),
            // Both writers last learned 5 from the store, which is what a second container's mirror holds
            // when it has not yet seen the first one's write. That is the whole stale-baseline situation.
            holdsOpen: b => b.Buckets.Add(Bucket(10, 100, 5, 50)),
            racesIt: b => b.Buckets.Add(Bucket(7, 70, 5, 50)),
            witness: (Seed: 5, Held: 10, Raced: 7),
            readWatermark: () => ReadOne(factory, ctx => ctx.SessionHighwater.Single().Turns),
            readLedger: () => ReadOne(factory, ctx => ctx.StatDeltas.Sum(r => r.Turns)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        // The higher watermark stands. Under a read-modify-write this reads 7/70 - the second writer computed
        // its new value from a row state that no longer existed by the time it wrote.
        Assert.Equal(10, row.Turns);
        Assert.Equal(100, row.Chars);
        // And the ledger holds ten turns, not seventeen. The racing writer's five-to-seven was already inside
        // the winning writer's five-to-ten; appending it as well is the double count this whole shape removes.
        Assert.Equal(10, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(100, ctx.StatDeltas.Sum(r => r.Chars));
        // The per-agent tally is built from the same difference, so it cannot disagree.
        Assert.Equal(10, ctx.AgentDeltas.Sum(r => r.Turns));
    }

    [RequiresPostgresFact]
    public void TokenHighWater_InterleavedWriters_KeepTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        var seeded = Write(factory, b => b.Tokens.Add(new StatsWriteBatch.TokenObservation(
            Session, null, 100, 10, 5, 1, 0, 0, 0, 0, 0)));

        InterleaveAndCommit(
            factory,
            ResolverFor(seeded), TokenRow, Relation("token_highwater"),
            holdsOpen: b => b.Tokens.Add(new StatsWriteBatch.TokenObservation(
                Session, null, 900, 90, 45, 9, 100, 10, 5, 1, 0)),
            racesIt: b => b.Tokens.Add(new StatsWriteBatch.TokenObservation(
                Session, null, 300, 30, 15, 3, 100, 10, 5, 1, 0)),
            witness: (Seed: 100, Held: 900, Raced: 300),
            readWatermark: () => ReadOne(factory, ctx => ctx.TokenHighwater.Single().InputTokens),
            readLedger: () => ReadOne(factory, ctx => ctx.TokenDeltas.Sum(r => r.InputTokens)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.TokenHighwater.Single();
        Assert.Equal(900, row.InputTokens);
        Assert.Equal(90, row.OutputTokens);
        Assert.Equal(45, row.CacheReadTokens);
        Assert.Equal(9, row.CacheCreationTokens);
        // Every one of the four scalars is judged on its own, so every one of them has to hold the identity.
        Assert.Equal(900, ctx.TokenDeltas.Sum(r => r.InputTokens));
        Assert.Equal(90, ctx.TokenDeltas.Sum(r => r.OutputTokens));
        Assert.Equal(45, ctx.TokenDeltas.Sum(r => r.CacheReadTokens));
        Assert.Equal(9, ctx.TokenDeltas.Sum(r => r.CacheCreationTokens));
    }

    [RequiresPostgresFact]
    public void AgentDrivenHighWater_InterleavedWriters_KeepTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        var seeded = Write(factory, b => b.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 2, 20, 0, 0, 0)));

        InterleaveAndCommit(
            factory,
            ResolverFor(seeded), AgentDrivenRow, Relation("agent_driven_highwater"),
            holdsOpen: b => b.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 12, 120, 2, 20, 0)),
            racesIt: b => b.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 6, 60, 2, 20, 0)),
            witness: (Seed: 2, Held: 12, Raced: 6),
            readWatermark: () => ReadOne(factory, ctx => ctx.AgentDrivenHighwater.Single().Turns),
            readLedger: () => ReadOne(factory, ctx => ctx.AgentDrivenDeltas.Sum(r => r.Turns)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.AgentDrivenHighwater.Single();
        Assert.Equal(12, row.Turns);
        Assert.Equal(120, row.Chars);
        Assert.Equal(12, ctx.AgentDrivenDeltas.Sum(r => r.Turns));
        Assert.Equal(120, ctx.AgentDrivenDeltas.Sum(r => r.Chars));
    }

    /// <summary>
    /// A RESET observed against CURRENT state is still counted in full - and the stored row comes DOWN with
    /// it, which is the half that used to be missing.
    ///
    /// A reported count below the stored watermark is two different events wearing one face: a Director that
    /// restarted this session id and is counting again from zero, or a writer whose baseline another writer has
    /// already overtaken. The fold cannot tell them apart, and the store used to be asked to do both jobs at
    /// once - it kept the floor (right for the stale reader) while the fold counted the drop as fresh activity
    /// (right for the restart). The two answers then disagreed about the same row, and the disagreement
    /// surfaced on the next Gateway restart, when the mirror is rebuilt from the row.
    ///
    /// Now the writer sends the baseline it believed, as evidence, and the statement rules. Here the belief is
    /// CURRENT, so the drop is a real restart: the whole of the new count is new activity and the row adopts
    /// it. The stale case is the one the interleave facts above exercise, and it rules the other way.
    /// </summary>
    [RequiresPostgresFact]
    public void ARestartObservedAgainstCurrentState_CountsInFull_AndBringsTheStoredRowDownWithIt()
    {
        ResetSchema();
        var factory = Factory();
        Write(factory, b => b.Buckets.Add(Bucket(10, 300, 0, 0)));

        // Believing 10 and reporting 3, with the row itself at 10: the writer is up to date and the session
        // has genuinely started over.
        Write(factory, b => b.Buckets.Add(Bucket(3, 40, 10, 300)));

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        Assert.Equal(3, row.Turns);
        Assert.Equal(40, row.Chars);
        // Thirteen turns really happened: ten before the restart and three after.
        Assert.Equal(13, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(340, ctx.StatDeltas.Sum(r => r.Chars));
    }

    /// <summary>
    /// A DELAYED READING FROM THE LIFE BEFORE A RESET IS NOT COUNTED AS GROWTH IN THE LIFE AFTER IT.
    ///
    /// This is the reviewer's scenario, and it was the worst thing left in the write path. A Director
    /// restarting a session id makes its tally drop to zero and count again; the store adopts that as a reset.
    /// A writer still in flight with a reading from BEFORE the reset then arrives looking exactly like
    /// ordinary growth - its number is higher than the freshly reset row - and was counted a SECOND time.
    /// Measured before the fix: 100 banked turns, a reset to 5, a straggling observation of 110, and the
    /// ledger ended at 210 against honest activity of 115. Nothing rewrites an appended delta, so the 95 was
    /// wrong forever, and its size grew with the pre-reset watermark. A Director restart after a counter reset
    /// is an ordinary event, not an edge case, so this was not an acceptable residual.
    ///
    /// The store can SEE a reset, so it can count them: the row carries a GENERATION that advances each time a
    /// reset is adopted, the writer sends the generation it last saw, and a reading whose belief is from an
    /// older generation changes nothing at all. No producer change and no wire-contract change were needed -
    /// the evidence was already here and simply had nowhere to be recorded.
    ///
    /// WHAT IS STILL LOST, SAID PLAINLY: the straggler's 110 carried ten turns of the OLD life that had never
    /// been counted, and dropping the reading drops them. That is an UNDERCOUNT of at most one poll interval
    /// of the ended life, per collision, and it does NOT scale with the watermark. Recovering those ten would
    /// mean trusting the writer's own belief about a generation the store no longer holds - no arbiter exists
    /// for a life that has ended - and two stragglers doing that would overcount again. A small bounded loss
    /// beats an unbounded permanent gain.
    /// </summary>
    [RequiresPostgresFact]
    public void AStragglerFromTheLifeBeforeAReset_IsNotCountedAsGrowthInTheLifeAfterIt()
    {
        ResetSchema();
        var factory = Factory();
        var seeded = Write(factory, b => b.Buckets.Add(Bucket(100, 1000, 0, 0)));
        var resolve = ResolverFor(seeded);
        var writer = new GatewayStatsWriter(factory);

        // The Director restarted this session id. This writer's baseline is current, so the drop is a real
        // reset: the row adopts 5 and moves on to its next incarnation.
        var afterReset = NewBatch();
        afterReset.Buckets.Add(Bucket(5, 50, 100, 1000, believedGeneration: 0));
        var reset = writer.Commit(afterReset, resolve);
        Assert.Equal(1, reset.SessionHighWater.Single().Generation);

        // The straggler: a reading of 110 from the life that has just ended, carrying that life's baseline.
        var straggler = NewBatch();
        straggler.Buckets.Add(Bucket(110, 1100, 100, 1000, believedGeneration: 0));
        var ignored = writer.Commit(straggler, resolve);

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        // The row is untouched by the straggler: still the new life, still on its generation.
        Assert.Equal(5, row.Turns);
        Assert.Equal(50, row.Chars);
        Assert.Equal(1, row.Generation);
        Assert.Equal(1, ignored.SessionHighWater.Single().Generation);
        // 100 banked plus the 5 of the new life. Before the generation column this read 210.
        Assert.Equal(105, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(1050, ctx.StatDeltas.Sum(r => r.Chars));
    }

    /// <summary>
    /// And the writer that sent the straggler recovers on its very next poll, because the commit told it what
    /// the row actually holds - the same rule that governs everything else here. Its mirror moves to the new
    /// life, and the growth it measures next is growth in that life.
    /// </summary>
    [RequiresPostgresFact]
    public void AWriterThatSentAStraggler_CountsCorrectlyOnItsNextPoll()
    {
        ResetSchema();
        var factory = Factory();
        var seeded = Write(factory, b => b.Buckets.Add(Bucket(100, 1000, 0, 0)));
        var resolve = ResolverFor(seeded);
        var writer = new GatewayStatsWriter(factory);

        var afterReset = NewBatch();
        afterReset.Buckets.Add(Bucket(5, 50, 100, 1000, believedGeneration: 0));
        writer.Commit(afterReset, resolve);

        var straggler = NewBatch();
        straggler.Buckets.Add(Bucket(110, 1100, 100, 1000, believedGeneration: 0));
        var told = writer.Commit(straggler, resolve).SessionHighWater.Single();

        // What the straggling writer was told is what the row holds, not what it proposed.
        Assert.Equal(5, told.Turns);
        Assert.Equal(1, told.Generation);

        // Its next poll reads the restarted session at 8, measured from what it was just told.
        var next = NewBatch();
        next.Buckets.Add(Bucket(8, 80, told.Turns, told.Chars, told.Generation));
        writer.Commit(next, resolve);

        using var ctx = factory.CreateDbContext();
        Assert.Equal(8, ctx.SessionHighwater.Single().Turns);
        Assert.Equal(108, ctx.StatDeltas.Sum(r => r.Turns));
    }

    /// <summary>
    /// The same property without the arranged interleave: many writers, many rounds, all on ONE row, running
    /// in whatever order the server gives them, each keeping its own mirror and updating it ONLY from what the
    /// commit reports - which is exactly how the aggregator behaves.
    ///
    /// The reported counts come from one shared monotone counter because that is what the world looks like:
    /// every writer is reading the SAME session's cumulative tally, so no two of them can honestly see it go
    /// backwards. (Eight writers each reporting their own unrelated ascending series would be modelling eight
    /// different sessions through one row, and every disagreement between them would be read as a restart.)
    ///
    /// This cannot prove the race happened, so it is not the proof - the deterministic facts above are. It
    /// catches an implementation that only survives the one interleaving those arrange.
    /// </summary>
    [RequiresPostgresFact]
    public void SessionHighWater_ManyConcurrentWriters_LeaveTheLedgerEqualToTheWatermark()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;
        const int rounds = 25;

        var reported = 0L;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                // This thread's mirror of the row, advanced ONLY from what a commit reports.
                long believedTurns = 0, believedChars = 0, believedGeneration = 0;
                start.SignalAndWait();
                for (var round = 1; round <= rounds; round++)
                {
                    var value = Interlocked.Increment(ref reported);
                    var batch = NewBatch();
                    WithIdentities(batch);
                    batch.Buckets.Add(Bucket(value, value * 10, believedTurns, believedChars, believedGeneration));
                    var committed = writer.Commit(batch, ResolveNothing);
                    var stored = committed.SessionHighWater.Single();
                    believedTurns = stored.Turns;
                    believedChars = stored.Chars;
                    believedGeneration = stored.Generation;
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");
        Assert.Empty(failures);

        using var ctx = factory.CreateDbContext();
        var row = ctx.SessionHighwater.Single();
        var highest = (long)writers * rounds;
        Assert.Equal(highest, row.Turns);
        Assert.Equal(highest * 10, row.Chars);
        // The invariant, over two hundred racing commits: what the ledger gained is what the watermark moved.
        Assert.Equal(highest, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(highest * 10, ctx.StatDeltas.Sum(r => r.Chars));
    }

    // ---- Identity: one spelling, one id, however many writers mint it at once ------------------------

    /// <summary>
    /// Concurrent mints of ONE spelling settle on ONE id, and every writer learns which id that is.
    ///
    /// The old mint inserted a row and took the id the insert generated, which is only the right id when
    /// nobody else was minting the same spelling at the same moment. When somebody was, that tenant's turns
    /// split silently across two surrogate ids and its repository appeared twice with half its work each -
    /// a wrong number that looks exactly like a right one. The mint is now an upsert against the (tenant,
    /// spelling) unique index that RETURNS the surviving id, so a writer that loses the race is told so.
    /// </summary>
    [RequiresPostgresFact]
    public void ConcurrentMintsOfOneSpelling_AllResolveToTheSameId()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;

        var ids = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                var batch = NewBatch();
                batch.NewIdentities.Add((Repo, IdentityKind.Repo));
                start.SignalAndWait();
                var committed = writer.Commit(batch, ResolveNothing);
                ids.Enqueue(committed.Identities[IdentityKind.Repo][Repo]);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        Assert.Empty(failures);
        Assert.Equal(writers, ids.Count);
        Assert.Single(ids.Distinct());
        using var ctx = factory.CreateDbContext();
        var row = Assert.Single(ctx.RepoIdentities);
        Assert.Equal(row.RepoId, ids.First());
    }

    /// <summary>
    /// TWO WRITERS MINTING THE SAME NEW IDENTITIES IN OPPOSITE ORDER DO NOT DEADLOCK.
    ///
    /// This defect was created by the fix above it. Making the mint conflict on a unique index is what made
    /// two writers contend for the SAME identity ROW instead of each quietly inserting its own - and a batch
    /// arrives in OBSERVATION order, which two containers polling one Director have no reason to agree on.
    /// Writer A minting "owner/one" then "owner/two" while writer B mints them the other way round is a lock
    /// cycle, and PostgreSQL breaks a cycle by ABORTING one transaction with SQLSTATE 40P01. That is a LOST
    /// FOLD on a live request, not a slow one.
    ///
    /// The fix is an ORDER, not a retry: both writers sort by a total, stable key before taking any lock, so
    /// the cycle cannot form. A retry would have left the cycle in the design and made it less visible, which
    /// costs the ability to notice it at first.
    ///
    /// THE ONE-SECOND TRIGGER IS WHAT MAKES THIS DETERMINISTIC rather than a matter of luck. Without it both
    /// writers would usually finish their two inserts too quickly to overlap, and the fact would pass on a
    /// cycle that never had the chance to form - green for the wrong reason. Sleeping inside the insert holds
    /// each writer's FIRST row lock while the other takes its own, which is exactly the window a real
    /// dual-container fold has and a fast test does not.
    /// </summary>
    [RequiresPostgresFact]
    public void IdentityMints_ArrivingInOppositeOrder_DoNotDeadlock()
    {
        ResetSchema();
        var factory = Factory();
        const string first = "owner/one";
        const string second = "owner/two";

        using (var ctx = factory.CreateDbContext())
        {
            ctx.Database.ExecuteSqlRaw(
                $"CREATE FUNCTION {GatewayStatsDbContext.PostgresSchema}.mint_delay() RETURNS trigger " +
                "LANGUAGE plpgsql AS 'BEGIN PERFORM pg_sleep(1); RETURN NULL; END'");
            ctx.Database.ExecuteSqlRaw(
                $"CREATE TRIGGER mint_delay AFTER INSERT ON {GatewayStatsDbContext.PostgresSchema}.repo_identity " +
                $"FOR EACH ROW EXECUTE FUNCTION {GatewayStatsDbContext.PostgresSchema}.mint_delay()");
        }

        var results = new System.Collections.Concurrent.ConcurrentQueue<StatsCommitResult>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(2);
        var threads = new[] { new[] { first, second }, new[] { second, first } }
            .Select(order => new Thread(() =>
            {
                try
                {
                    var batch = NewBatch();
                    foreach (var repo in order) batch.NewIdentities.Add((repo, IdentityKind.Repo));
                    start.SignalAndWait();
                    results.Enqueue(new GatewayStatsWriter(factory).Commit(batch, ResolveNothing));
                }
                catch (Exception ex) { failures.Enqueue(ex); }
            })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        // Name the failure rather than just counting it: a deadlock has its own SQLSTATE and saying so is the
        // difference between "something went wrong" and a report somebody can act on.
        Assert.True(failures.IsEmpty,
            "A writer failed. If this is SQLSTATE 40P01 the canonical lock order has been lost and two " +
            "writers took the same identity rows in opposite orders: " +
            string.Join(" | ", failures.Select(e => e.Message)));

        // And both writers agree about who each identity is, which is the property the mint exists for.
        Assert.Equal(2, results.Count);
        var ids = results.Select(r => r.Identities[IdentityKind.Repo]).ToList();
        Assert.Equal(ids[0][first], ids[1][first]);
        Assert.Equal(ids[0][second], ids[1][second]);
        using var check = factory.CreateDbContext();
        Assert.Equal(2, check.RepoIdentities.Count());
    }

    // ---- The membership sets and the first-fold back-fill --------------------------------------------

    [RequiresPostgresFact]
    public void MembershipWrites_AreInsertIfAbsent_UnderConcurrency()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                start.SignalAndWait();
                var batch = NewBatch();
                batch.NewWingmanSessions.Add(Session);
                batch.Seeding.Add((Session, new List<StatsWriteBatch.AgentBackfillRow>()));
                batch.StampAgentsSince = "2026-07-30T12:00:00.0000000Z";
                writer.Commit(batch, ResolveNothing);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        // A read-then-insert would have raced two writers into the same key and raised a unique violation on
        // one of them; ON CONFLICT DO NOTHING simply leaves the row that is already there.
        Assert.Empty(failures);
        using var ctx = factory.CreateDbContext();
        Assert.Equal(1, ctx.WingmanSessions.Count());
        Assert.Equal(1, ctx.AgentsSeeded.Count());
        Assert.Equal(1, ctx.Meta.Count());
    }

    /// <summary>
    /// The first-fold back-fill (issue #1633) is attributed ONCE, by whichever writer's insert claimed the
    /// mark - not once per writer that found an unmarked mirror.
    ///
    /// Eight writers all first-folding one session all find it unseeded, because none of their mirrors has
    /// the mark yet. Attributing on that belief multiplies the agent's standing count by however many
    /// containers happen to be running. The mark is insert-if-absent and the statement reports whether THIS
    /// insert created the row; only that writer attributes.
    /// </summary>
    [RequiresPostgresFact]
    public void TheFirstFoldBackfill_IsAttributedOnce_HoweverManyWritersFirstFoldTheSession()
    {
        ResetSchema();
        var factory = Factory();
        const int writers = 8;

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(writers);
        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            try
            {
                var writer = new GatewayStatsWriter(factory);
                var batch = NewBatch();
                batch.NewIdentities.Add((TheAgent, IdentityKind.Agent));
                batch.Seeding.Add((Session, new List<StatsWriteBatch.AgentBackfillRow>
                {
                    new(TheAgent, false, 40, 400),
                }));
                start.SignalAndWait();
                writer.Commit(batch, ResolveNothing);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)), "a writer never finished");

        Assert.Empty(failures);
        using var ctx = factory.CreateDbContext();
        Assert.Equal(1, ctx.AgentsSeeded.Count());
        Assert.Equal(40, ctx.AgentDeltas.Sum(r => r.Turns));
        Assert.Equal(400, ctx.AgentDeltas.Sum(r => r.Chars));
    }

    // ---- Retention: the rows archived are the rows the statement removed -----------------------------

    /// <summary>
    /// Pruning conserves the all-time totals: the expired detail leaves and its contribution stays, folded
    /// into archive rows that keep every dimension an all-time answer groups by.
    ///
    /// The archive is now folded from the rows the DELETE itself returned rather than from a separate read of
    /// the same predicate. The two-statement shape read the table twice, so anything committed between the
    /// two reads was deleted having never been archived - a silent shrink of the all-time totals, surfacing
    /// ninety days after the write that caused it. Here the set cannot differ from itself.
    /// </summary>
    [RequiresPostgresFact]
    public void Pruning_ArchivesEveryExpiredRow_AndKeepsTheAllTimeTotals()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);
        var now = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

        // Two hours of detail, both far outside the retention window, plus one inside it.
        var expired = new[] { now.AddDays(-200), now.AddDays(-150) };
        var repoId = 0L;
        foreach (var hour in expired.Append(now))
        {
            var batch = new StatsWriteBatch(Tenant, now, StatsHourKey.For(hour));
            WithIdentities(batch);
            batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
                "s-" + StatsHourKey.For(hour), "typed", "phone", false, Repo, Checkout, null, false, TheAgent,
                7, 70, 0, 0, 0));
            var committed = writer.Commit(batch, ResolveNothing);
            if (committed.Identities[IdentityKind.Repo].TryGetValue(Repo, out var id)) repoId = id;
        }

        using var ctx = factory.CreateDbContext();
        // Twenty-one turns went in and twenty-one turns are still there, whatever shape the rows are in now.
        Assert.Equal(21, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(210, ctx.StatDeltas.Sum(r => r.Chars));
        // The expired hours are gone as HOURS - they no longer name an hour of the working day.
        Assert.Empty(ctx.StatDeltas.Where(r => r.HourUtc != GatewayStatsDatabase.ArchiveMarker
                                            && r.HourUtc != StatsHourKey.For(now)).ToList());
        // And every turn that left is still counted, on rows that carry every dimension they arrived with.
        // (Each prune adds its own archive row rather than merging into an existing one - archive rows are
        // excluded from the sweep by their marker hour, so they are never re-read. Two expired hours pruned on
        // two different writes therefore leave two archive rows, and the all-time answers sum them.)
        var archived = ctx.StatDeltas.Where(r => r.HourUtc == GatewayStatsDatabase.ArchiveMarker).ToList();
        Assert.NotEmpty(archived);
        Assert.Equal(14, archived.Sum(r => r.Turns));
        Assert.Equal(140, archived.Sum(r => r.Chars));
        Assert.All(archived, r =>
        {
            Assert.Equal(repoId, r.RepoId);
            Assert.NotNull(r.CheckoutId);
            Assert.Null(r.ModelId);
            Assert.Equal(Tenant.Value, r.Tenant);
            Assert.Equal("typed", r.Modality);
            Assert.Equal("phone", r.Surface);
            Assert.False(r.IsVoice);
            Assert.False(r.Wingman);
        });
    }

    // ---- Idempotency: replaying one batch changes nothing after the first ----------------------------

    [RequiresPostgresFact]
    public void ReplayingOneBatchTenTimes_LeavesTheStoreAsOneReplayDid()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);

        // The stamp a second replay proposes is LATER than the first. Insert-if-absent means the first one
        // stands: the since-stamp is written once and never moved.
        //
        // Every replay reports the SAME counts and the same belief, which is what a re-read of an unchanged
        // roster looks like from a writer whose mirror is already current. The first replay is the only one
        // that changes anything, and the delta ledger says so - which is a stronger statement than the
        // watermark, since the ledger is append-only and has nothing to hide a repeat behind.
        for (var replay = 0; replay < 10; replay++)
        {
            var batch = NewBatch();
            WithIdentities(batch);
            batch.Buckets.Add(Bucket(9, 90, replay == 0 ? 0 : 9, replay == 0 ? 0 : 90));
            batch.Tokens.Add(new StatsWriteBatch.TokenObservation(Session, null, 500, 50, 25, 5,
                replay == 0 ? 0 : 500, replay == 0 ? 0 : 50, replay == 0 ? 0 : 25, replay == 0 ? 0 : 5, 0));
            batch.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 4, 40,
                replay == 0 ? 0 : 4, replay == 0 ? 0 : 40, 0));
            batch.NewWingmanSessions.Add(Session);
            batch.Seeding.Add((Session, new List<StatsWriteBatch.AgentBackfillRow>()));
            batch.StampAgentsSince = $"2026-07-30T12:0{replay}:00.0000000Z";
            writer.Commit(batch, ResolveNothing);
        }

        using var ctx = factory.CreateDbContext();
        var session = ctx.SessionHighwater.Single();
        Assert.Equal(9, session.Turns);
        Assert.Equal(90, session.Chars);
        var token = ctx.TokenHighwater.Single();
        Assert.Equal(500, token.InputTokens);
        Assert.Equal(5, token.CacheCreationTokens);
        var driven = ctx.AgentDrivenHighwater.Single();
        Assert.Equal(4, driven.Turns);
        Assert.Equal(1, ctx.WingmanSessions.Count());
        Assert.Equal(1, ctx.AgentsSeeded.Count());
        Assert.Equal("2026-07-30T12:00:00.0000000Z", ctx.Meta.Single().Value);

        // Nine turns, once, however many times the batch was replayed - because after the first replay the
        // raise statement says the row did not move, and a row that did not move contributes no delta.
        Assert.Equal(9, ctx.StatDeltas.Sum(r => r.Turns));
        Assert.Equal(500, ctx.TokenDeltas.Sum(r => r.InputTokens));
        Assert.Equal(4, ctx.AgentDrivenDeltas.Sum(r => r.Turns));
        // Repeated mints of the same spellings settle on one row each, not ten.
        Assert.Single(ctx.RepoIdentities);
        Assert.Single(ctx.CheckoutIdentities);
        Assert.Single(ctx.AgentIdentities);
    }

    // ---- A whole batch, on PostgreSQL, with identities minted and resolved ---------------------------

    [RequiresPostgresFact]
    public void AFullBatch_MintsIdentities_AndFilesEveryRowUnderItsTenant()
    {
        ResetSchema();
        var factory = Factory();
        var writer = new GatewayStatsWriter(factory);

        var batch = NewBatch();
        WithIdentities(batch);
        batch.NewIdentities.Add(("claude-opus-5", IdentityKind.Model));
        batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
            Session, "typed", "phone", false, Repo, Checkout, "claude-opus-5", false, TheAgent, 3, 60, 0, 0, 0));
        batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
            Session, "voice", "phone", true, Repo, Checkout, null, true, TheAgent, 1, 10, 0, 0, 0));
        batch.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(Session, TheAgent, 2, 20, 0, 0, 0));
        batch.Tokens.Add(new StatsWriteBatch.TokenObservation(Session, "claude-opus-5", 500, 50, 25, 5, 0, 0, 0, 0, 0));
        batch.NewIdentitySessions.Add((Repo, Session, IdentityKind.Repo));
        batch.NewIdentitySessions.Add((TheAgent, Session, IdentityKind.Agent));

        var committed = writer.Commit(batch, ResolveNothing);

        Assert.Equal(Repo, committed.Identities[IdentityKind.Repo].Keys.Single());
        using var ctx = factory.CreateDbContext();
        Assert.Equal(2, ctx.StatDeltas.Count());
        Assert.All(ctx.StatDeltas.ToList(), r => Assert.Equal(Tenant.Value, r.Tenant));
        // The model a Director never named is a real NULL, never an identity spelled "".
        Assert.Single(ctx.StatDeltas.Where(r => r.ModelId == null));
        Assert.Single(ctx.ModelIdentities);
        Assert.Equal(committed.Identities[IdentityKind.Repo].Values.Single(), ctx.RepoSessions.Single().RepoId);
        Assert.Equal(committed.Identities[IdentityKind.Agent].Values.Single(), ctx.AgentSessions.Single().AgentId);
        Assert.Equal(2, ctx.AgentDeltas.Count());
        Assert.Single(ctx.AgentDrivenDeltas);
        Assert.Single(ctx.TokenDeltas);
    }

    // ---- The CAUSED interleave -----------------------------------------------------------------------

    /// <summary>
    /// CAUSE the interleave rather than watch for one.
    ///
    /// A previous version of this helper OBSERVED the server and asked "is writer B blocked?". A review broke
    /// it, and the way it broke is the lesson: the question was strengthened in every dimension - it named
    /// writer B's backend, required NOT granted, restricted the lock type, required writer A among the
    /// blockers - and it still witnessed THE WRONG EVENT. Identity mints run before any watermark raise and
    /// every racing batch minted the same spellings, so writer B blocked on writer A's IDENTITY row and
    /// satisfied every clause honestly. With racesIt replaced by a no-op, so writer B touched no watermark row
    /// at all, the fact still passed. No further clause would have fixed that: the defect was the KIND of
    /// check, not its strength.
    ///
    /// So the interleave is now CONSTRUCTED, and the construction is what proves it:
    ///
    ///   1. Writer A raises the contested row and HOLDS its transaction open on the BeforeCommit seam.
    ///   2. Only then is writer B released. Its BeforeRaise seam fires as it is about to raise a row, and
    ///      asserts that row is THE CONTESTED ONE - so a writer that does no watermark work, or works on a
    ///      different row, cannot get past this point. The reviewer's no-op mutation now times out here.
    ///   3. Writer A waits until the server reports B blocked, by A, on a lock in THE CONTESTED RELATION while
    ///      holding no other write lock in the schema - the relation binding the old witness lacked.
    ///   4. A checks B has still not finished, then commits. B finishes only afterwards.
    ///
    /// The racing batches mint NOTHING - identities are pre-minted and resolved from the caller's map, the way
    /// the aggregator's mirror does it - so the only row either writer touches is the row under test.
    ///
    /// AND IT IS WHERE THE LEDGER INVARIANT LIVES, so that every interleave carries it and no future fact can
    /// be written here that checks only the watermark, which is the mistake that let a real double count sit
    /// under a green suite.
    /// </summary>
    private static void InterleaveAndCommit(
        IDbContextFactory<GatewayStatsDbContext> factory,
        Func<string, IdentityKind, long> resolveKnown,
        string contestedRow,
        string contestedTable,
        Action<StatsWriteBatch> holdsOpen,
        Action<StatsWriteBatch> racesIt,
        (long Seed, long Held, long Raced) witness,
        Func<long> readWatermark,
        Func<long> readLedger)
    {
        // REFUSE A FIXTURE THAT COULD NOT SHOW THE FAILURE, rather than trusting whoever wrote it to have
        // thought about it - the author is always the last person able to see the gap in their own fixture.
        //   raced < held   - so the losing writer's value is distinguishable from the winning one at all.
        //   seed  < raced  - so a lost update is also distinguishable from the second writer having done
        //                    nothing, and so the DOUBLE COUNT is visible: the racing writer's growth from the
        //                    seed genuinely overlaps the winning writer's.
        Assert.True(witness.Seed < witness.Raced && witness.Raced < witness.Held,
            $"This fixture cannot show a lost update: seed={witness.Seed}, raced={witness.Raced}, " +
            $"held={witness.Held}. The racing value must sit strictly between the seed and the held value, " +
            "or the assertion reads the same whether the update was lost or kept.");

        // The row must be there, and there must be exactly one of it - readWatermark uses Single(), which is
        // what lets "a lock in this relation" mean "a lock on THIS row".
        var watermarkBefore = readWatermark();
        Assert.Equal(witness.Seed, watermarkBefore);
        var ledgerBefore = readLedger();

        using var firstConnection = OpenPinned(out var firstPid);
        using var secondConnection = OpenPinned(out var secondPid);
        var firstFactory = new PinnedConnectionFactory(firstConnection);
        var secondFactory = new PinnedConnectionFactory(secondConnection);

        using var firstHoldsTheRow = new ManualResetEventSlim(false);
        using var secondReachedTheRow = new ManualResetEventSlim(false);
        using var secondFinished = new ManualResetEventSlim(false);
        Exception? secondFailure = null;

        var second = new Thread(() =>
        {
            try
            {
                if (!firstHoldsTheRow.Wait(TimeSpan.FromMinutes(1)))
                    throw new InvalidOperationException("The first writer never took the contested row.");
                var batch = NewBatch();
                racesIt(batch);
                new GatewayStatsWriter(secondFactory).Commit(batch, resolveKnown, new StatsWriteSeams
                {
                    BeforeRaise = row =>
                    {
                        Assert.Equal(contestedRow, row);
                        secondReachedTheRow.Set();
                    },
                });
            }
            catch (Exception ex) { secondFailure = ex; }
            finally { secondFinished.Set(); }
        });
        second.Start();

        var held = NewBatch();
        holdsOpen(held);
        new GatewayStatsWriter(firstFactory).Commit(held, resolveKnown, new StatsWriteSeams
        {
            BeforeCommit = () =>
            {
                firstHoldsTheRow.Set();
                Assert.True(secondReachedTheRow.Wait(TimeSpan.FromMinutes(1)),
                    $"The second writer never reached {contestedRow}. It did no work on the row this fact " +
                    "claims to race, so there was no race to prove.");
                WaitUntilBlockedOnRelation(secondPid, firstPid, contestedTable);
                Assert.False(secondFinished.IsSet,
                    "The second writer finished while the first still held the row, so they never contended.");
            },
        });

        Assert.True(second.Join(TimeSpan.FromMinutes(2)), "the second writer never finished");
        if (secondFailure is not null)
            throw new InvalidOperationException("The second writer failed: " + secondFailure.Message, secondFailure);

        // THE ASSERTION THAT REPLACES WHAT THIS ROW MEANS. Whatever the two writers proposed and in whatever
        // order they landed, the append-only ledger gained exactly what the watermark moved - no more (which
        // would be the double count) and no less (which would be a lost turn).
        Assert.Equal(readWatermark() - watermarkBefore, readLedger() - ledgerBefore);
    }

    private static void WaitUntilBlockedOnRelation(int blockedPid, int blockerPid, string table)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            if (IsBlockedOnRelation(blockedPid, blockerPid, table)) return;
            Thread.Sleep(25);
        }

        throw new InvalidOperationException(
            $"Backend {blockedPid} (the second writer) never blocked on a row of {table} held by backend " +
            $"{blockerPid} (the first writer).");
    }

    /// <summary>
    /// Is <paramref name="blockedPid"/> waiting for a ROW in <paramref name="table"/> that
    /// <paramref name="blockerPid"/> holds - and for nothing else?
    ///
    /// The relation clauses are what the previous witness lacked. Naming the backend and the lock type was
    /// satisfied honestly by a block on an unrelated identity row; requiring the blocked backend to hold a
    /// write lock on the contested relation AND no other write lock in this schema is what ties the wait to
    /// the row under test, which the fixture has already established is the only row in that table.
    ///
    /// The no-other-write-lock clause counts TABLES only (relkind 'r'). A write also takes RowExclusiveLock
    /// on the target's indexes, which are relations in the same schema - so without the relkind filter the
    /// contested table's own primary-key index disqualified a correctly-blocked writer, and this witness
    /// could never fire on any non-HOT update (issue #1190, observed live in pg_locks).
    /// </summary>
    private static bool IsBlockedOnRelation(int blockedPid, int blockerPid, string table)
    {
        using var ctx = Factory().CreateDbContext();
        return ctx.Database.SqlQueryRaw<long>(
            @"SELECT count(*) AS ""Value"" FROM pg_locks blocked
               WHERE blocked.pid = {0}
                 AND NOT blocked.granted
                 AND blocked.locktype IN ('transactionid', 'tuple')
                 AND {1} = ANY (pg_blocking_pids(blocked.pid))
                 AND EXISTS (SELECT 1 FROM pg_locks held
                              WHERE held.pid = {0} AND held.granted
                                AND held.locktype = 'relation'
                                AND held.relation = CAST({2} AS regclass))
                 AND NOT EXISTS (SELECT 1 FROM pg_locks other
                                   JOIN pg_class c ON c.oid = other.relation
                                   JOIN pg_namespace n ON n.oid = c.relnamespace
                                  WHERE other.pid = {0} AND other.granted
                                    AND other.locktype = 'relation'
                                    AND other.mode = 'RowExclusiveLock'
                                    AND c.relkind = 'r'
                                    AND n.nspname = {3}
                                    AND other.relation <> CAST({2} AS regclass))",
            blockedPid, blockerPid, table, GatewayStatsDbContext.PostgresSchema).Single() > 0;
    }

    /// <summary>
    /// VALIDATE THE DETECTOR BEFORE TRUSTING ITS VERDICTS. The relation check must NOT fire for a backend
    /// blocked on something that is not a row of the contested table - which is exactly how its predecessor
    /// certified interleaves that never reached the watermark.
    ///
    /// One connection takes an advisory lock and a second blocks on it: a real block between two real
    /// backends, wrong in only one way - it is not the row this suite is about. The check has to say no.
    /// </summary>
    [RequiresPostgresFact]
    public void TheRelationCheck_RefusesToCertify_AnUnrelatedLock()
    {
        ResetSchema();
        using var holder = OpenPinned(out var holderPid);
        using var waiter = OpenPinned(out var waiterPid);

        using (var take = holder.CreateCommand())
        {
            take.CommandText = "SELECT pg_advisory_lock(918273645)";
            take.ExecuteScalar();
        }

        var blocked = new Thread(() =>
        {
            using var wait = waiter.CreateCommand();
            wait.CommandText = "SELECT pg_advisory_lock(918273645)";
            wait.ExecuteScalar();
        });
        blocked.Start();

        try
        {
            // Wait until the server really does report the waiter blocked by the holder - otherwise this fact
            // would pass by never setting up the situation it is supposed to reject.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var reallyBlocked = false;
            while (DateTime.UtcNow < deadline && !reallyBlocked)
            {
                using var ctx = Factory().CreateDbContext();
                reallyBlocked = ctx.Database.SqlQueryRaw<long>(
                    @"SELECT count(*) AS ""Value"" FROM pg_locks
                       WHERE pid = {0} AND NOT granted AND {1} = ANY (pg_blocking_pids(pid))",
                    waiterPid, holderPid).Single() > 0;
                if (!reallyBlocked) Thread.Sleep(25);
            }
            Assert.True(reallyBlocked, "the advisory-lock waiter never blocked, so this fact tested nothing");

            Assert.False(IsBlockedOnRelation(waiterPid, holderPid, Relation("session_highwater")));
        }
        finally
        {
            using var release = holder.CreateCommand();
            release.CommandText = "SELECT pg_advisory_unlock(918273645)";
            release.ExecuteScalar();
            blocked.Join(TimeSpan.FromSeconds(30));
        }
    }
}
