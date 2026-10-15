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

    [Fact]
    public void IsProblem_AResultNoFoldKnows_IsAnErrorNotAQuietOmission()
        => Assert.Throws<InvalidOperationException>(() => CronRunResultFold.IsProblem(Run("half-done", 1)));
}
