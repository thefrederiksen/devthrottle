using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A RUN WHOSE START IS NOT KNOWN (Factory Definition, phase 5, review of P10, finding 1): the create was sent and the
/// Gateway never learned how it came out, so the session may have started. Every fold that reads a run names it, reads
/// it as "may have started" and never as a run that did not start, and gives its status a tone the clients render as
/// given (CLAUDE.md rule 7).
/// </summary>
public sealed class StartUnknownFoldTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 6, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, SessionEndingFact> NoEndings =
        new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal);

    private static CronRunRecord Unknown(int minutesAgo) => new()
    {
        RunId = Guid.NewGuid().ToString("D"),
        ScheduledUtc = Now.AddMinutes(-minutesAgo),
        FiredUtc = Now.AddMinutes(-minutesAgo),
        Machine = "SOREN_NORTH",
        InfraStatus = CronEngine.StartUnknownStatus,
        TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
        Result = CronRunResults.StartUnknown,
        ResultReason = "whether it started a session is not known: The Director did not answer within 30 seconds.",
    };

    [Fact]
    public void Stamp_AStartWhoseOutcomeIsUnknown_ReadsMayHaveStarted_WithAWarningTone()
    {
        var run = Unknown(5);

        CronRunEndingFold.Stamp(new[] { run }, NoEndings, Now);

        Assert.Equal(CronRunEndings.StartUnknown, run.Ending);
        Assert.Equal("may have started - the Gateway did not learn whether its session started", run.EndingText);
        Assert.Equal("warn", run.InfraTone);
        Assert.Equal(CronRunEndingFold.RunJudgement.NotJudged, CronRunEndingFold.Judge(run, Now));
    }

    [Theory]
    [InlineData("started", "ok")]
    [InlineData("worklist-started", "ok")]
    [InlineData("catch-up", "warn")]
    [InlineData("start-unknown", "warn")]
    [InlineData("worklist-no-list", "warn")]
    [InlineData("not-started", "err")]
    [InlineData("did-not-run", "dim")]
    [InlineData("check-due", "dim")]
    public void ToneOf_IsTheRulingTheCockpitUsedToMake_PlusAStartWhoseOutcomeIsUnknown(string status, string tone)
        => Assert.Equal(tone, CronRunEndingFold.ToneOf(status));

    [Fact]
    public void Summarize_AStartWhoseOutcomeIsUnknown_IsNotARunThatDidNotStart()
    {
        var runs = new[] { Unknown(60), Unknown(120) };
        CronRunEndingFold.Stamp(runs, NoEndings, Now);

        var summary = CronRunEndingFold.Summarize(runs, NoEndings, Now);

        Assert.Equal(("none", "nothing recorded yet"), (summary.Verdict, summary.Text));
    }

    [Fact]
    public void GatewayProblemFor_AStartWhoseOutcomeIsUnknown_EarnsNoProblem_EvenAfterItsShift()
    {
        var run = Unknown(60 * 30);
        var shift = WorkShifts.ShiftOf(run.FiredUtc, TimeZoneInfo.Utc);

        Assert.Null(CronRunResultFold.GatewayProblemFor(run, CronRunEndings.StartUnknown, "", shift, Now));
    }

    [Fact]
    public void IsProblem_AStartWhoseOutcomeIsUnknown_IsNotAProblem()
        => Assert.False(CronRunResultFold.IsProblem(Unknown(1)));

    [Fact]
    public void FactoryDays_AStartWhoseOutcomeIsUnknown_IsCountedAsNeitherDoneNorDidNotRun()
    {
        var job = new CronJobDto { Id = "cj_boss", Name = "DevThrottle - Boss", Factory = "devthrottle", Seat = "boss" };
        var runs = new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal) { ["cj_boss"] = new[] { Unknown(10) } };

        var day = Assert.Single(CronRunResultFold.FactoryDays(runs,
            new Dictionary<string, CronJobDto>(StringComparer.Ordinal) { ["cj_boss"] = job },
            _ => CronRunEndings.StartUnknown, Array.Empty<CronRunProblemDto>(), Now.AddDays(-1), Now,
            Array.Empty<(CcDirector.Gateway.Data.Entities.TriggerEntity, IReadOnlyList<CcDirector.Gateway.Data.Entities.TriggerRunEntity>)>()));

        Assert.Equal((0, 0, 0), (day.Done, day.DidNotRun, day.NothingToDo));
    }
}
