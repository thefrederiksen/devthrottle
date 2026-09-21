using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Wingman;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The reads narrowed for devthrottle_internal#2199, run against a REAL PostgreSQL - the provider production uses.
/// The unit tests prove the behaviour over SQLite; these prove the three changed query shapes translate and answer
/// the same on Postgres: the verdict snapshot's column projection, the conversation's tail read, and the history
/// sweep's narrow read.
///
/// GATING. Like the other proofs in this folder, the class is gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and
/// reports SKIPPED when it is unset. Skipped is not passed.
/// </summary>
public sealed class GatewayReadCutsPostgresTests
{
    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (!PostgresProofDatabase.IsConfigured)
                Skip = $"Set {PostgresProofDatabase.ConnectionEnvVar} to a Postgres connection string to run the " +
                       "real-Postgres proof of the narrowed Gateway reads.";
        }
    }

    /// <summary>Open the Gateway database on this run's own Postgres database, through the runtime constructor. The
    /// provider is chosen by a process-global variable, so it is set and put back while no other test is opening a
    /// database.</summary>
    private static GatewayDatabase OpenPostgres()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        GatewayDatabase? db = null;
        GatewayDbEnvironmentGate.WhileNobodyIsOpeningADatabase(() =>
        {
            var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, PostgresProofDatabase.Connection);
            try { db = new GatewayDatabase(new SingleTenantContext()); }
            finally { Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous); }
        });
        return db!;
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    private static TurnVerdictDto Verdict(DateTime judgedAt, string verdictId) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = judgedAt,
        TurnEndObservedAtUtc = judgedAt.AddSeconds(-12),
        ScreenHash = "screen",
        Model = "devthrottle/wingman",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Verdict = Core.Wingman.TurnVerdictVocabulary.Finished,
        Confidence = "high",
        Evidence = "done",
        Label = "Finished",
        Summary = "Finished.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "Finished.",
    };

    [RequiresPostgresFact]
    public void The_verdict_snapshot_projection_answers_the_newest_verdict_without_the_answer()
    {
        using var db = OpenPostgres();
        var store = new TurnVerdictStore(db);
        var tenant = TenantId.Local;
        var sid = Unique("sid");
        var t0 = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
        store.Store(tenant, sid, Verdict(t0, Unique("tv-old")));
        var newest = Unique("tv-new");
        store.Store(tenant, sid, Verdict(t0.AddMinutes(1), newest));
        Assert.True(store.MarkAnswered(tenant, new TurnVerdictStoredAnswer(newest, t0.AddMinutes(1).AddSeconds(-12), new[] { 0 }, "yes"), t0.AddMinutes(2)));

        Assert.Equal(newest, store.SnapshotLatest(tenant)[sid].VerdictId);
        using var ctx = db.CreateContext(tenant);
        var row = Assert.Single(TurnVerdictStore.SnapshotLatestCore(ctx), r => r.SessionId == sid);
        Assert.Null(row.AnswerJson);
        Assert.Contains(newest, row.VerdictJson);
    }

    [RequiresPostgresFact]
    public void The_conversation_tail_read_answers_the_whole_conversation_from_the_new_rows_only()
    {
        using var db = OpenPostgres();
        var store = new SessionTurnStore(db);
        var sid = Unique("s");
        var started = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);
        TurnPushBatch Batch(int start, params string[] texts) => new()
        {
            SessionId = sid,
            Generation = @"C:\transcripts\" + sid + ".jsonl",
            GenerationStartedUtc = started,
            Agent = "ClaudeCode",
            StartOrdinal = start,
            TotalCount = start + texts.Length,
            Turns = texts.Select((t, i) => new PushedTurn
            {
                Ordinal = start + i,
                Role = (start + i) % 2 == 0 ? "User" : "Assistant",
                Parts = { new HistoryPartDto { Kind = "Text", Text = t } },
            }).ToList(),
        };

        store.Append("d1", Batch(0, "one", "two"), started);
        Assert.Equal(new[] { "one", "two" }, store.ReadCurrent(sid)!.Value.Messages.Select(m => m.Parts[0].Text));

        // The held prefix is deleted behind the store's back; a store that re-read it would lose it.
        using (var ctx = db.CreateContext())
            ctx.SessionTurns.Where(t => t.SessionId == sid).ExecuteDelete();
        store.Append("d1", Batch(2, "three"), started.AddSeconds(5));

        Assert.Equal(new[] { "one", "two", "three" }, store.ReadCurrent(sid)!.Value.Messages.Select(m => m.Parts[0].Text));
    }

    [RequiresPostgresFact]
    public void The_history_sweeps_narrow_read_groups_and_hashes_like_the_full_read()
    {
        using var db = OpenPostgres();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        var repo = Unique("repo");
        SessionDto Session(string id, DateTime startedAt) => new()
        {
            SessionId = id,
            Name = "A session",
            RepoPath = @"D:\repos\" + repo,
            RepoName = "thefrederiksen/" + repo,
            Agent = "ClaudeCode",
            CreatedAt = startedAt,
            ActivityState = "Working",
            Status = "Running",
            MissionName = "Cut the burn",
        };
        var a = Unique("h");
        var b = Unique("h");
        store.UpsertLive("dir-1", Session(a, now.AddDays(-2)), now);
        store.UpsertLive("dir-1", Session(b, now.AddHours(-1)), now);
        var from = now.Date.AddDays(-7);
        var to = now.Date.AddDays(1).AddTicks(-1);

        var narrow = SessionHistorySummarizer.RollupGroups(store.ReadRollupInputs(from, to), from, now.Date)
            .Where(g => g.RepoKey.Contains(repo, StringComparison.Ordinal)).Select(g => (g.Day, g.InputHash)).OrderBy(x => x.Day).ToList();
        var full = SessionHistorySummarizer.RollupGroups(store.ReadRange(from, to), from, now.Date)
            .Where(g => g.RepoKey.Contains(repo, StringComparison.Ordinal)).Select(g => (g.Day, g.InputHash)).OrderBy(x => x.Day).ToList();

        Assert.True(full.Count >= 3);
        Assert.Equal(full, narrow);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), store.ReadMany(new[] { a, b }).Select(s => s.SessionId).OrderBy(x => x));
    }
}
