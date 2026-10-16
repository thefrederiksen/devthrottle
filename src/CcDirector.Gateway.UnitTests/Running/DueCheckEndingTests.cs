using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A RUN OF A SCHEDULE WITH A CHECK THAT STARTED NO SESSION ON PURPOSE (Factory Definition, phase 5b): one still
/// waiting on its check, or one whose check found nothing to do, reads as such on the run table and never counts against
/// the schedule as a run that did not start - and every fold that reads a result names both.
/// </summary>
public sealed class DueCheckEndingTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, SessionEndingFact> NoEndings =
        new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal);

    private static CronRunRecord Run(string result, int minutesAgo) => new()
    {
        RunId = Guid.NewGuid().ToString("D"),
        ScheduledUtc = Now.AddMinutes(-minutesAgo),
        FiredUtc = Now.AddMinutes(-minutesAgo),
        Machine = "SOREN_NORTH",
        InfraStatus = result == CronRunResults.Checking ? CronEngine.CheckDueStatus : result,
        TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
        Result = result,
    };

    [Theory]
    [InlineData(CronRunResults.Checking, CronRunEndings.Checking, "checking - its check has not reported yet")]
    [InlineData(CronRunResults.NothingToDo, CronRunEndings.NothingToDo, "nothing to do - its check found no work, so no session was needed")]
    public void Stamp_ARunWithNoSessionOnPurpose_ReadsAsItsOwnEnding_NotDidNotStart(string result, string ending, string text)
    {
        var run = Run(result, 5);

        CronRunEndingFold.Stamp(new[] { run }, NoEndings, Now);

        Assert.Equal((ending, text), (run.Ending, run.EndingText));
        Assert.Equal(CronRunEndingFold.RunJudgement.NotJudged, CronRunEndingFold.Judge(run, Now));
    }

    [Theory]
    [InlineData(CronRunResults.Checking, null, "checking")]
    [InlineData(CronRunResults.NothingToDo, null, "nothing-to-do")]
    [InlineData(CronRunResults.Problem, CronRunProblems.CheckFailed, "check-failed")]
    [InlineData(CronRunResults.Problem, CronRunProblems.CheckNotRun, "check-not-run")]
    public void Stamp_ACheckedRunWithNoSession_ReadsItsTaskStatusAsWhatItsCheckDid_NotUnknown(string result, string? problem, string expected)
    {
        // Arrange: stored as every fire stores it, "unknown" (the QA report's finding 3: `schedule runs` printed Task
        // "unknown" for a nothing-to-do run).
        var run = Run(result, 5);
        run.Problem = problem;

        // Act
        CronRunEndingFold.Stamp(new[] { run }, NoEndings, Now);

        // Assert
        Assert.Equal(expected, run.TaskStatus);
    }

    [Fact]
    public void Stamp_ARunWithNoCheck_KeepsItsStoredTaskStatus()
    {
        // Arrange: a schedule with no check that failed to start a session (today's behaviour, goal 14), and one whose
        // session a route recorded as closed by itself.
        var didNotRun = Run(CronRunResults.Problem, 5);
        didNotRun.Problem = CronRunProblems.DidNotRun;
        var started = Run(CronRunResults.Ok, 5);
        started.SessionId = "s1";
        started.TaskStatus = CronRunEndings.ClosedItself;

        // Act
        CronRunEndingFold.Stamp(new[] { didNotRun, started }, NoEndings, Now);

        // Assert
        Assert.Equal((CronRunHistoryStore.TaskStatusUnknown, CronRunEndings.ClosedItself), (didNotRun.TaskStatus, started.TaskStatus));
    }

    [Fact]
    public void RunsOf_ANothingToDoRunInTheStore_IsReadWithItsTrueTaskStatus_AndTheStoreKeepsUnknown()
    {
        // Arrange: the run table's own reader over the real store, the way GET /cron/jobs/{id}/runs reads it.
        using var h = new CcDirector.Gateway.Tests.Data.GatewayDbTestHarness();
        var db = h.Open();
        var store = new CronRunHistoryStore(db, h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        store.Append("cj_quiet", Run(CronRunResults.NothingToDo, 5));
        var reader = new CronRunRecordReader(store, new SessionHistoryStore(db).EndingsOf);

        // Act
        var read = Assert.Single(reader.RunsOf("cj_quiet", Now));

        // Assert: folded on the read, never written back.
        Assert.Equal("nothing-to-do", read.TaskStatus);
        Assert.Equal(CronRunHistoryStore.TaskStatusUnknown, Assert.Single(store.List("cj_quiet")).TaskStatus);
    }

    [Fact]
    public void Summarize_ASchedulesQuietChecks_AreNotRunsThatDidNotStart()
    {
        var runs = new[] { Run(CronRunResults.NothingToDo, 60), Run(CronRunResults.NothingToDo, 120), Run(CronRunResults.Checking, 1) };
        CronRunEndingFold.Stamp(runs, NoEndings, Now);

        var summary = CronRunEndingFold.Summarize(runs, NoEndings, Now);

        Assert.Equal(("none", "nothing recorded yet"), (summary.Verdict, summary.Text));
    }

    [Theory]
    [InlineData(CronRunResults.Checking)]
    [InlineData(CronRunResults.NothingToDo)]
    public void GatewayProblemFor_ARunWithNoReportOwed_EarnsNoProblem_EvenAfterItsShift(string result)
    {
        var run = Run(result, 60 * 30);
        var shift = WorkShifts.ShiftOf(run.FiredUtc, TimeZoneInfo.Utc);

        Assert.Null(CronRunResultFold.GatewayProblemFor(run, CronRunEndings.NoSession, "", shift, Now));
    }

    [Theory]
    [InlineData(CronRunResults.Checking, false)]
    [InlineData(CronRunResults.NothingToDo, false)]
    [InlineData(CronRunResults.Problem, true)]
    public void IsProblem_NamesEveryResult(string result, bool expected)
        => Assert.Equal(expected, CronRunResultFold.IsProblem(Run(result, 1)));

    private static CcDirector.Gateway.Data.Entities.CronRunEntity Row(string result, string? problem = null) => new()
    {
        JobId = "cj_mail", Result = result, Problem = problem, InfraStatus = result, TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
    };

    [Theory]
    [InlineData(CronRunProblems.CheckFailed, true)]
    [InlineData(CronRunProblems.CheckNotRun, true)]
    [InlineData(CronRunProblems.Reported, false)]
    public void PrunableBeyondCap_AProblemALaterNothingToDoCleared_IsPrunedOnlyWhenItIsACheckProblem(string problem, bool pruned)
    {
        // Arrange: the oldest row is the problem; a nothing-to-do comes after it and no run ever reported ok; the history
        // is one row over its cap.
        var rows = new List<CcDirector.Gateway.Data.Entities.CronRunEntity> { Row(CronRunResults.Problem, problem), Row(CronRunResults.NothingToDo) };
        while (rows.Count < CronRunHistoryStore.MaxRecordsPerJob + 1) rows.Add(Row(CronRunResults.Checking));

        // Act
        var gone = CronRunHistoryStore.PrunableBeyondCap(rows);

        // Assert: a quiet check clears a check problem, as the fold says, so the cap may remove it; a problem a session
        // reported stays open and is kept, and the oldest closed row goes instead.
        var removed = Assert.Single(gone);
        Assert.Equal(pruned, ReferenceEquals(rows[0], removed));
        if (!pruned) Assert.Same(rows[1], removed);
    }

    [Fact]
    public void IsProblem_AResultNoFoldKnows_IsAnErrorNotAQuietOmission()
        => Assert.Throws<InvalidOperationException>(() => CronRunResultFold.IsProblem(Run("half-done", 1)));
}
