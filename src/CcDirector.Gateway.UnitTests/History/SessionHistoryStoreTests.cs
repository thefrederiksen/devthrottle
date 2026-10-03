using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The durable work-history store contract (issue #2194): a row exists from first sight and survives
/// a "restart" (a fresh database handle over the same file), endings are stamped once and ruled by
/// the Gateway, the silence rule concludes "interrupted" and presence reopens it, summaries seal or
/// generate with the sealed account winning, and reads are tenant-scoped. Runs over the real EF
/// store on a throwaway SQLite file, exactly as the Gateway runs it locally.
/// </summary>
public sealed class SessionHistoryStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private SessionHistoryStore NewStore(ITenantContext? tenant = null)
        => new(_harness.Open(tenant));

    private static SessionDto Session(string id = "s1", string? name = "Fix the parser",
        string repoPath = @"D:\repos\devthrottle", string repoName = "thefrederiksen/devthrottle",
        DateTime? createdAt = null, string activityState = "Working", string status = "Running",
        bool crashed = false, string? mission = null, string? role = null) => new()
    {
        SessionId = id,
        Name = name,
        Number = 123,
        RepoPath = repoPath,
        RepoName = repoName,
        Agent = "ClaudeCode",
        CurrentModel = "claude-fable-5",
        MachineName = "SOREN_NORTH",
        CreatedAt = createdAt ?? DateTime.UtcNow.AddHours(-1),
        LastActivityAt = DateTime.UtcNow.AddMinutes(-1),
        ActivityState = activityState,
        Status = status,
        Crashed = crashed,
        MissionName = mission,
        ExplicitRole = role,
    };

    [Fact]
    public void Supervision_facts_land_and_unknown_never_erases_them()
    {
        // internal#625 phase 4: the agent turn count and the cumulative idle clock ride the pushed
        // session onto the history row. The cases that are silent when wrong: an older Director's
        // null must not erase a known value, and a Director restart (whose counters start again at
        // zero) must not lower the run's high-water mark.
        var store = NewStore();
        var now = DateTime.UtcNow;

        var measured = Session();
        measured.TurnCount = 14;
        measured.CumulativeIdleSeconds = 2520;
        store.UpsertLive("dir-1", measured, now);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(14, row.AgentTurnCount);
        Assert.Equal(2520, row.IdleSeconds);

        // An older Director pushes the same session with no supervision facts: nothing is erased.
        var silent = Session();
        silent.TurnCount = null;
        silent.CumulativeIdleSeconds = null;
        store.UpsertLive("dir-1", silent, now.AddMinutes(6));

        row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(14, row.AgentTurnCount);
        Assert.Equal(2520, row.IdleSeconds);

        // A restarted Director reports lower counters: the high-water mark stands.
        var restarted = Session();
        restarted.TurnCount = 2;
        restarted.CumulativeIdleSeconds = 30;
        store.UpsertLive("dir-1", restarted, now.AddMinutes(12));

        row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(14, row.AgentTurnCount);
        Assert.Equal(2520, row.IdleSeconds);
    }

    [Fact]
    public void First_sight_creates_an_open_row_carrying_the_director_measured_start()
    {
        var store = NewStore();
        var created = new DateTime(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        store.UpsertLive("dir-1", Session(createdAt: created), now);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Null(row.EndingKind);
        Assert.Equal(SessionHistoryFold.ToneLive, row.EndingTone);
        // The start is the Director's measured CreatedAt, persisted - never a Gateway guess.
        Assert.Equal(created, row.StartedAtUtc);
        Assert.Equal(now, row.LastSeenUtc);
        Assert.Equal("thefrederiksen/devthrottle", row.RepoName);
        Assert.Equal("claude-fable-5", row.Model);
        // No mission and no prompt yet: the description floor is name + repository, never empty.
        Assert.Equal("Fix the parser in thefrederiksen/devthrottle", row.DescriptionLine);
    }

    [Fact]
    public void The_record_survives_a_gateway_restart()
    {
        var now = DateTime.UtcNow;
        NewStore().UpsertLive("dir-1", Session(), now);

        // A second database handle over the SAME file is the harness's restart simulation.
        var reopened = NewStore();
        var row = Assert.Single(reopened.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal("s1", row.SessionId);
        Assert.Null(row.EndingKind);
    }

    [Fact]
    public void A_farewell_ending_is_stamped_once_and_the_first_ruling_sticks()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(), now);

        store.RecordEnding("s1", SessionHistoryEndings.Closed, crashed: false, now);
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, now.AddSeconds(5));

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionHistoryEndings.Closed, row.EndingKind);
        Assert.Equal("Closed", row.EndingLabel);
        Assert.Equal("neutral", row.EndingTone);
    }

    [Fact]
    public void An_ending_for_a_session_never_seen_alive_records_nothing()
    {
        var store = NewStore();
        store.RecordEnding("ghost", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow);
        Assert.Empty(store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public void Silence_past_the_threshold_concludes_interrupted_with_last_seen_as_the_end()
    {
        var store = NewStore();
        var lastSeen = DateTime.UtcNow.AddMinutes(-30);
        store.UpsertLive("dir-1", Session(id: "silent"), lastSeen);
        store.UpsertLive("dir-1", Session(id: "fresh"), DateTime.UtcNow);

        var ruled = store.ConcludeInterrupted(DateTime.UtcNow - TimeSpan.FromMinutes(15));

        Assert.Equal(1, ruled);
        var rows = store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var silent = rows.Single(r => r.SessionId == "silent");
        Assert.Equal(SessionHistoryEndings.Interrupted, silent.EndingKind);
        // The end time is the last observation - the honest stand-in, and the label says so.
        Assert.Equal(silent.LastSeenUtc, silent.EndedAtUtc);
        Assert.Contains("last seen", silent.EndingLabel);
        Assert.Equal("attention", silent.EndingTone);
        Assert.Null(rows.Single(r => r.SessionId == "fresh").EndingKind);
    }

    [Fact]
    public void A_session_that_reappears_reopens_its_interrupted_row()
    {
        var store = NewStore();
        store.UpsertLive("dir-1", Session(), DateTime.UtcNow.AddMinutes(-30));
        store.ConcludeInterrupted(DateTime.UtcNow - TimeSpan.FromMinutes(15));

        store.UpsertLive("dir-1", Session(), DateTime.UtcNow);

        var row = Assert.Single(store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
        Assert.Null(row.EndingKind);
        Assert.Null(row.EndedAtUtc);
        Assert.Equal(SessionHistoryFold.ToneLive, row.EndingTone);
    }

    [Fact]
    public void Director_stopped_stamps_only_that_directors_open_rows()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(id: "a"), now);
        store.UpsertLive("dir-1", Session(id: "b"), now);
        store.UpsertLive("dir-2", Session(id: "other"), now);
        store.RecordEnding("a", SessionHistoryEndings.Finished, crashed: false, now); // already ruled

        var stamped = store.MarkDirectorStopped("dir-1", now.AddSeconds(1));

        Assert.Equal(1, stamped);
        var rows = store.ReadRange(now.AddDays(-1), now.AddDays(1));
        Assert.Equal(SessionHistoryEndings.Finished, rows.Single(r => r.SessionId == "a").EndingKind);
        Assert.Equal(SessionHistoryEndings.DirectorStopped, rows.Single(r => r.SessionId == "b").EndingKind);
        Assert.Null(rows.Single(r => r.SessionId == "other").EndingKind);
    }

    [Fact]
    public void A_session_absent_from_the_authoritative_snapshot_is_ruled_closed()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(id: "kept"), now);
        store.UpsertLive("dir-1", Session(id: "gone"), now);

        var closed = store.CloseAbsentSessions("dir-1",
            new HashSet<string>(StringComparer.Ordinal) { "kept" }, now.AddSeconds(1));

        Assert.Equal(1, closed);
        var rows = store.ReadRange(now.AddDays(-1), now.AddDays(1));
        Assert.Null(rows.Single(r => r.SessionId == "kept").EndingKind);
        Assert.Equal(SessionHistoryEndings.Closed, rows.Single(r => r.SessionId == "gone").EndingKind);
    }

    // The AI work-history summaries were removed in October 2026: nobody read them. The rows written before then
    // still carry summary text in their (now unused) columns, so these prove it can never reach a reader again,
    // and that the old roll-up rows still age out.

    [Fact]
    public void The_history_contracts_carry_no_summary_fields()
    {
        var banned = new[] { "Summary", "WhatWasBuilt", "LeftUnverified", "Branches", "PullRequests", "Commits" };
        var properties = typeof(WorkHistorySessionDto).GetProperties()
            .Concat(typeof(WorkHistoryDayDto).GetProperties())
            .Select(p => p.Name)
            .ToList();

        Assert.Contains(nameof(WorkHistorySessionDto.SessionId), properties);
        Assert.Contains(nameof(WorkHistoryDayDto.Sessions), properties);
        Assert.DoesNotContain(properties, name => banned.Any(b => name.Contains(b, StringComparison.Ordinal)));
        Assert.Null(typeof(WorkHistorySessionDto).Assembly.GetType("CcDirector.Gateway.Contracts.SealSessionSummaryRequest"));
    }

    [Fact]
    public void An_old_stored_summary_never_reaches_the_history_report()
    {
        const string oldSummary = "An old generated summary nobody should see again.";
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(), now);
        using (var ctx = db.CreateContext())
        {
            var row = ctx.SessionHistory.Single(e => e.SessionId == "s1");
            row.SummaryKind = "generated";
            row.SummaryText = oldSummary;
            row.WhatWasBuiltJson = "[\"an old built item\"]";
            ctx.SaveChanges();
        }

        var report = HistoryEndpoints.BuildReport(store, now.Date, now.Date);
        var json = System.Text.Json.JsonSerializer.Serialize(report);

        var session = Assert.Single(Assert.Single(Assert.Single(report.Repos).Days).Sessions);
        Assert.Equal("s1", session.SessionId);
        Assert.DoesNotContain(oldSummary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("an old built item", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_retention_prune_still_removes_old_rollup_rows()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        using (var ctx = db.CreateContext())
        {
            ctx.SessionHistoryRollups.Add(new CcDirector.Gateway.Data.Entities.SessionHistoryRollupEntity
            {
                TenantId = ctx.ActiveTenant!,
                RepoKey = "thefrederiksen/devthrottle",
                DayUtc = now.Date.AddDays(-120),
                SummaryText = "An old day paragraph.",
                InputHash = "x",
                ComputedAtUtc = now.AddDays(-120),
            });
            ctx.SaveChanges();
        }

        Assert.Equal(1, store.PurgeOlderThan(now - SessionHistorySweep.Retention));

        using var check = db.CreateContext();
        Assert.Empty(check.SessionHistoryRollups.ToList());
    }

    [Fact]
    public void The_range_read_includes_overlapping_sessions_and_excludes_the_rest()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        // Started well before the window but last seen inside it: overlaps.
        store.UpsertLive("dir-1", Session(id: "spanning", createdAt: now.AddDays(-10)), now.AddDays(-1));
        // Entirely before the window.
        store.UpsertLive("dir-1", Session(id: "ancient", createdAt: now.AddDays(-20)), now.AddDays(-15));

        var rows = store.ReadRange(now.AddDays(-3), now);

        Assert.Equal("spanning", Assert.Single(rows).SessionId);
    }

    [Fact]
    public void Two_tenants_never_see_each_others_history()
    {
        var alpha = NewStore(new FixedTenantContext(new TenantId("alpha")));
        var beta = NewStore(new FixedTenantContext(new TenantId("beta")));
        var now = DateTime.UtcNow;

        alpha.UpsertLive("dir-1", Session(), now);

        Assert.Single(alpha.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Empty(beta.ReadRange(now.AddDays(-1), now.AddDays(1)));
        // The same caller-supplied session id is beta's own row, not a collision with alpha's.
        beta.UpsertLive("dir-9", Session(name: "Beta's own work"), now);
        Assert.Equal("Fix the parser",
            Assert.Single(alpha.ReadRange(now.AddDays(-1), now.AddDays(1))).SessionName);
    }

    [Fact]
    public void The_first_prompt_is_set_once_and_becomes_the_description()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(name: null), now);

        store.SetFirstPrompt("s1", "Read the mission brief and build the History page");
        store.SetFirstPrompt("s1", "A later prompt that must not overwrite");

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal("Read the mission brief and build the History page", row.DescriptionLine);
    }

    [Fact]
    public void Retention_prunes_only_ended_rows()
    {
        var store = NewStore();
        var old = DateTime.UtcNow.AddDays(-100);
        store.UpsertLive("dir-1", Session(id: "old-ended", createdAt: old), old);
        store.RecordEnding("old-ended", SessionHistoryEndings.Finished, crashed: false, old);
        // An open row past the cutoff is the interrupted ruling's job, never retention's.
        store.UpsertLive("dir-1", Session(id: "old-open", createdAt: old), old);

        var deleted = store.PurgeOlderThan(DateTime.UtcNow.AddDays(-90));

        Assert.Equal(1, deleted);
        var rows = store.ReadRange(old.AddDays(-1), DateTime.UtcNow);
        Assert.Equal("old-open", Assert.Single(rows).SessionId);
    }
}
