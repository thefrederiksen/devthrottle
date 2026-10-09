using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// How a scheduled run's session ended, and whether a schedule's sessions close themselves (the owner, 2026-10-09).
/// Until this existed every one of 852 recorded runs said "unknown", so no screen could tell a schedule that cleans up
/// after itself from one whose sessions wait for someone to close them.
/// </summary>
public sealed class CronRunEndingTests : IDisposable
{
    private static readonly DateTime Fired = new(2026, 10, 9, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Fired.AddHours(5);

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CronRunRecord Run(string? sessionId, string taskStatus = "unknown", DateTime? fired = null) => new()
    {
        ScheduledUtc = fired ?? Fired,
        FiredUtc = fired ?? Fired,
        Machine = "SOREN_NORTH",
        TargetDirectorId = "dir-1",
        SessionId = sessionId,
        InfraStatus = sessionId is null ? "not-started" : "started",
        TaskStatus = taskStatus,
    };

    private static Dictionary<string, SessionEndingFact> Facts(params (string Id, string? Kind, DateTime? Ended)[] facts) =>
        facts.ToDictionary(f => f.Id, f => new SessionEndingFact(f.Kind, f.Ended), StringComparer.Ordinal);

    // ---- who asked -------------------------------------------------------------------------------------------

    [Fact]
    public void EndingForRequest_TheSessionAskingAboutItself_ClosedItself()
    {
        var sid = Guid.NewGuid().ToString();

        Assert.Equal(CronRunEndings.ClosedItself, CronRunEndingFold.EndingForRequest(sid.ToUpperInvariant(), sid));
    }

    [Fact]
    public void EndingForRequest_AnotherSessionsKey_StoppedBySession()
    {
        Assert.Equal(CronRunEndings.StoppedBySession,
            CronRunEndingFold.EndingForRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
    }

    [Fact]
    public void EndingForRequest_NoSessionKey_StoppedByYou()
    {
        // The desktop, the Cockpit and the phone call with the owner's credentials, never a session key.
        Assert.Equal(CronRunEndings.StoppedByYou, CronRunEndingFold.EndingForRequest(null, Guid.NewGuid().ToString()));
    }

    // ---- one run ---------------------------------------------------------------------------------------------

    [Fact]
    public void EndingOf_AFireThatStartedNoSession_IsNoSession()
    {
        Assert.Equal(CronRunEndings.NoSession, CronRunEndingFold.EndingOf(Run(null), Facts()).Ending);
    }

    [Fact]
    public void EndingOf_AnOpenSession_IsStillOpenEvenWhenItAskedToClose()
    {
        // Flagged for deletion but not yet reaped, or a deletion cancelled in its grace window: it is still open.
        var ending = CronRunEndingFold.EndingOf(Run("s1", CronRunEndings.ClosedItself), Facts(("s1", null, null)));

        Assert.Equal(CronRunEndings.StillOpen, ending.Ending);
    }

    [Fact]
    public void EndingOf_ARecordedEnding_WinsOverTheHistoryRuling()
    {
        // The history rules every deliberate removal "closed"; only the route knows the session asked for it.
        var ended = Fired.AddMinutes(7);
        var ending = CronRunEndingFold.EndingOf(Run("s1", CronRunEndings.StoppedByYou),
            Facts(("s1", SessionHistoryEndings.Closed, ended)));

        Assert.Equal(CronRunEndings.StoppedByYou, ending.Ending);
        Assert.Equal(ended, ending.EndedUtc);
    }

    [Theory]
    [InlineData(SessionHistoryEndings.Finished, CronRunEndings.ClosedItself)]
    [InlineData(SessionHistoryEndings.Closed, CronRunEndings.ClosedNotRecorded)]
    [InlineData(SessionHistoryEndings.DirectorStopped, CronRunEndings.DirectorStopped)]
    [InlineData(SessionHistoryEndings.Interrupted, CronRunEndings.Interrupted)]
    public void EndingOf_NothingRecorded_ReadsTheHistoryRuling(string historyKind, string expected)
    {
        var ending = CronRunEndingFold.EndingOf(Run("s1"), Facts(("s1", historyKind, Fired.AddMinutes(3))));

        Assert.Equal(expected, ending.Ending);
    }

    [Fact]
    public void EndingOf_NoHistoryRowAndNothingRecorded_IsUnknownNotStillOpen()
    {
        Assert.Equal(CronRunEndings.Unknown, CronRunEndingFold.EndingOf(Run("s1"), Facts()).Ending);
    }

    [Fact]
    public void Stamp_WritesTheEndingInWords()
    {
        var runs = new[] { Run("s1", CronRunEndings.ClosedItself), Run("s2", fired: Now.AddMinutes(-10)), Run("s3") };

        CronRunEndingFold.Stamp(runs,
            Facts(("s1", SessionHistoryEndings.Closed, Fired.AddMinutes(6)), ("s2", null, null), ("s3", null, null)), Now);

        Assert.Equal("closed itself after 6 min", runs[0].EndingText);
        Assert.Equal("running - 10 min so far", runs[1].EndingText);
        Assert.Equal("left open - 5h 00m so far", runs[2].EndingText);
    }

    // ---- a schedule's record ---------------------------------------------------------------------------------

    private static CronRunRecordSummaryDto Summarize(CronRunRecord[] runs, Dictionary<string, SessionEndingFact> facts)
    {
        CronRunEndingFold.Stamp(runs, facts, Now);
        return CronRunEndingFold.Summarize(runs, facts, Now);
    }

    [Fact]
    public void Summarize_EveryRunClosedItself_IsOkWithTheTypicalLength()
    {
        var summary = Summarize(
            new[] { Run("a", CronRunEndings.ClosedItself), Run("b"), Run("c", CronRunEndings.ClosedItself) },
            Facts(("a", SessionHistoryEndings.Closed, Fired.AddMinutes(4)),
                  ("b", SessionHistoryEndings.Finished, Fired.AddMinutes(8)),
                  ("c", SessionHistoryEndings.Closed, Fired.AddMinutes(30))));

        Assert.Equal("ok", summary.Verdict);
        Assert.Equal("closes itself - 3 of 3, about 8 min", summary.Text);
    }

    [Fact]
    public void Summarize_RunsStoppedOrLeftOpen_IsBadAndSaysWhich()
    {
        var summary = Summarize(
            new[] { Run("a"), Run("b", CronRunEndings.StoppedByYou), Run("c", CronRunEndings.StoppedBySession), Run("d", CronRunEndings.ClosedItself) },
            Facts(("a", null, null),
                  ("b", SessionHistoryEndings.Closed, Fired.AddHours(2)),
                  ("c", SessionHistoryEndings.Closed, Fired.AddHours(3)),
                  ("d", SessionHistoryEndings.Closed, Fired.AddMinutes(5))));

        Assert.Equal("bad", summary.Verdict);
        Assert.Equal("1 of 4 closed itself - 1 stopped by you, 1 stopped by another session, 1 left open", summary.Text);
    }

    [Fact]
    public void Summarize_ARunStillYoung_DoesNotCountAgainstTheSchedule()
    {
        var summary = Summarize(
            new[] { Run("young", fired: Now.AddMinutes(-5)), Run("a", CronRunEndings.ClosedItself) },
            Facts(("young", null, null), ("a", SessionHistoryEndings.Closed, Fired.AddMinutes(9))));

        Assert.Equal("ok", summary.Verdict);
        Assert.Equal("closes itself - 1 of 1, about 9 min", summary.Text);
    }

    [Fact]
    public void Summarize_OnlyRunsWhoseCloserWasNotRecorded_SaysNothingRecordedYet()
    {
        // Every run that ended before endings were recorded reads this way: neither good nor bad.
        var summary = Summarize(new[] { Run("a"), Run("b") },
            Facts(("a", SessionHistoryEndings.Closed, Fired.AddMinutes(9)), ("b", SessionHistoryEndings.Closed, Fired.AddHours(4))));

        Assert.Equal("none", summary.Verdict);
        Assert.Equal("nothing recorded yet", summary.Text);
    }

    [Fact]
    public void Summarize_NoRuns_IsNoRunsYet()
    {
        var summary = Summarize(Array.Empty<CronRunRecord>(), Facts());

        Assert.Equal("none", summary.Verdict);
        Assert.Equal("no runs yet", summary.Text);
    }

    [Fact]
    public void Summarize_FiresThatFailedToStart_CountAgainstTheSchedule()
    {
        // Review finding: a schedule whose every fire failed to start used to read "no runs yet", contradicting its
        // own run table.
        var summary = Summarize(new[] { Run(null), Run(null) }, Facts());

        Assert.Equal("bad", summary.Verdict);
        Assert.Equal("0 of 2 closed itself - 2 did not start", summary.Text);
    }

    [Fact]
    public void EndingOf_AWorkListDrain_IsAWorkListNotAFailedStart()
    {
        // Review finding: a drain records no session by design (it starts many), so it is not "did not start".
        var drain = Run(null);
        drain.InfraStatus = "worklist-started";

        Assert.Equal(CronRunEndings.WorkList, CronRunEndingFold.EndingOf(drain, Facts()).Ending);
        Assert.Equal("none", Summarize(new[] { drain }, Facts()).Verdict);
    }

    [Theory]
    [InlineData("worklist-no-list")]
    [InlineData("worklist-already-claimed")]
    [InlineData("worklist-no-director")]
    [InlineData("worklist-machine-busy")]
    [InlineData("worklist-unknown")]
    public void EndingOf_AWorkListFireThatDidNotRun_IsAFailedStart(string infraStatus)
    {
        // Second review finding: these outcomes drained nothing, so they must not read as a drain.
        var fire = Run(null);
        fire.InfraStatus = infraStatus;

        Assert.Equal(CronRunEndings.NoSession, CronRunEndingFold.EndingOf(fire, Facts()).Ending);
    }

    [Fact]
    public void EndingOf_AnEmptyWorkList_IsAWorkListRunNotAFailure()
    {
        var fire = Run(null);
        fire.InfraStatus = "worklist-empty";

        Assert.Equal(CronRunEndings.WorkList, CronRunEndingFold.EndingOf(fire, Facts()).Ending);
    }

    // ---- the stored half -------------------------------------------------------------------------------------

    private CronRunHistoryStore NewRuns() => new(_h.Open(), _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    [Fact]
    public void StampEnding_WritesOnlyThatSessionsRuns_AndTheLatestRequestWins()
    {
        var runs = NewRuns();
        runs.Append("job-a", Run("s1"));
        runs.Append("job-a", Run("s2"));

        Assert.Equal(1, runs.StampEnding(TenantId.Local, "s1", CronRunEndings.ClosedItself));
        Assert.Equal(1, runs.StampEnding(TenantId.Local, "s1", CronRunEndings.StoppedByYou));

        var stored = runs.List("job-a");
        Assert.Equal(CronRunEndings.StoppedByYou, stored.Single(r => r.SessionId == "s1").TaskStatus);
        Assert.Equal("unknown", stored.Single(r => r.SessionId == "s2").TaskStatus);
    }

    [Fact]
    public void StampEnding_ASessionNoScheduleStarted_WritesNothing()
    {
        Assert.Equal(0, NewRuns().StampEnding(TenantId.Local, "hand-started", CronRunEndings.ClosedItself));
    }

    [Fact]
    public void StampEnding_AnEndingNoRouteRecords_Throws()
    {
        Assert.Throws<ArgumentException>(() => NewRuns().StampEnding(TenantId.Local, "s1", CronRunEndings.StillOpen));
    }

    [Fact]
    public void ClearEnding_ACancelledDeletion_TakesTheEndingBack()
    {
        var runs = NewRuns();
        runs.Append("job-a", Run("s1"));
        runs.StampEnding(TenantId.Local, "s1", CronRunEndings.ClosedItself);

        Assert.Equal(1, runs.ClearEnding(TenantId.Local, "s1"));

        Assert.Equal("unknown", runs.List("job-a").Single().TaskStatus);
    }

    [Fact]
    public void RecentByJob_TakesTheNewestRunsOfEachJob()
    {
        var runs = NewRuns();
        for (var i = 0; i < 4; i++)
            runs.Append("job-a", Run($"a{i}"));
        runs.Append("job-b", Run("b0"));

        runs.Append("job-deleted", Run("d0"));

        var recent = runs.RecentByJob(new[] { "job-a", "job-b" }, 2);

        Assert.Equal(new[] { "a3", "a2" }, recent["job-a"].Select(r => r.SessionId));
        Assert.Equal(new[] { "b0" }, recent["job-b"].Select(r => r.SessionId));
        Assert.False(recent.ContainsKey("job-deleted"));
    }

    // ---- end to end through the reader -----------------------------------------------------------------------

    [Fact]
    public void Reader_ASessionThatClosedItself_ReadsAsAScheduleThatClosesItself()
    {
        var db = _h.Open();
        var runs = new CronRunHistoryStore(db, _h.LegacyPath("e2e.runs.json"));
        var history = new SessionHistoryStore(db);
        var sid = Guid.NewGuid().ToString();
        var fired = DateTime.UtcNow.AddMinutes(-20);
        runs.Append("job-a", Run(sid, fired: fired));
        history.UpsertLive("dir-1", new SessionDto
        {
            SessionId = sid, Name = "Mail Desk", RepoPath = @"D:\repo", Agent = "ClaudeCode",
            MachineName = "SOREN_NORTH", CreatedAt = fired, ActivityState = "Working", Status = "Running",
        }, fired);
        var reader = new CronRunRecordReader(runs, history.EndingsOf);

        // While it runs, it is running.
        Assert.Equal(CronRunEndings.StillOpen, reader.RunsOf("job-a", DateTime.UtcNow).Single().Ending);

        // It runs `cc-devthrottle session done` (the route stamps it), and the Director then removes it.
        runs.StampEnding(TenantId.Local, sid, CronRunEndings.ClosedItself);
        history.RecordEnding(sid, SessionHistoryEndings.Closed, crashed: false, fired.AddMinutes(6));

        var run = reader.RunsOf("job-a", DateTime.UtcNow).Single();
        Assert.Equal(CronRunEndings.ClosedItself, run.Ending);
        Assert.Equal("closed itself after 6 min", run.EndingText);
        var summary = reader.SummariesOf(new[] { "job-a" }, DateTime.UtcNow)["job-a"];
        Assert.Equal("ok", summary.Verdict);
        Assert.Equal("closes itself - 1 of 1, about 6 min", summary.Text);
    }

    [Fact]
    public void Reader_RunLengthsOf_TakesTheMiddleOfTheRunsThatEnded_AndLeavesOutAScheduleWithNone()
    {
        var db = _h.Open();
        var runs = new CronRunHistoryStore(db, _h.LegacyPath("lengths.runs.json"));
        var history = new SessionHistoryStore(db);
        var fired = DateTime.UtcNow.AddHours(-3);

        void Session(string jobId, int? endedAfterMinutes)
        {
            var sid = Guid.NewGuid().ToString();
            runs.Append(jobId, Run(sid, fired: fired));
            history.UpsertLive("dir-1", new SessionDto
            {
                SessionId = sid, Name = jobId, RepoPath = @"D:\repo", Agent = "ClaudeCode",
                MachineName = "SOREN_NORTH", CreatedAt = fired, ActivityState = "Working", Status = "Running",
            }, fired);
            if (endedAfterMinutes is { } m)
                history.RecordEnding(sid, SessionHistoryEndings.Closed, crashed: false, fired.AddMinutes(m));
        }

        // Three that ended - one of them left open for 40 minutes, which really held a session that long - and one
        // still open, which says nothing about its length yet.
        Session("job-a", 6);
        Session("job-a", 10);
        Session("job-a", 40);
        Session("job-a", null);
        Session("job-open", null);
        var reader = new CronRunRecordReader(runs, history.EndingsOf);

        var lengths = reader.RunLengthsOf(new[] { "job-a", "job-open" });

        Assert.Equal(TimeSpan.FromMinutes(10), lengths["job-a"]);
        Assert.False(lengths.ContainsKey("job-open"));
    }
}
