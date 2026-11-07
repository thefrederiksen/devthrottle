using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A scheduled run's own session may not flag itself for deletion before it has reported (6 November 2026): two seats
/// ran <c>cc-devthrottle session done</c> instead of <c>cc-devthrottle run result ok</c> and real work was recorded as
/// "did not report". The run is read through the REAL run store's lookup by session, the one the deletion route uses.
/// </summary>
public sealed class ScheduledRunDeletionGuardTests : IDisposable
{
    private const string RunSession = "6eb77ea1-5b0c-4881-9aac-e502330dd53d";
    private const string OtherSession = "0afc9439-222b-44c4-97d6-e9a10d667c96";

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private CronRunHistoryStore Store() =>
        new(_h.Open(), _h.LegacyPath("cronruns-" + Guid.NewGuid().ToString("N") + ".json"));

    private static CronRunRecord Run(string result) => new()
    {
        ScheduledUtc = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc),
        FiredUtc = new DateTime(2026, 11, 6, 10, 0, 3, DateTimeKind.Utc),
        Machine = "devlinux",
        TargetDirectorId = "director-1",
        SessionId = RunSession,
        InfraStatus = "started",
        TaskStatus = "unknown",
        Result = result,
    };

    private static Func<string, CronRunRecord?> LookupIn(CronRunHistoryStore store) =>
        id => store.FindBySession(TenantId.Local, id)?.Run;

    [Fact]
    public void SessionDone_PendingScheduledRunOwnSession_RefusesAndFlagsNothing()
    {
        var store = Store();
        store.Append("cj_8e16c1", Run(CronRunResults.Pending));

        var refusal = ScheduledRunDeletionGuard.RefusalFor(RunSession, RunSession, LookupIn(store));

        Assert.Equal(ScheduledRunDeletionGuard.NotReported, refusal);
        // Nothing was written to the run by the refusal: it still waits on its report, with no ending stamped.
        var run = store.FindBySession(TenantId.Local, RunSession)!.Run;
        Assert.Equal(CronRunResults.Pending, run.Result);
        Assert.Null(run.Ending);
    }

    [Theory]
    [InlineData(CronRunResults.Ok)]
    [InlineData(CronRunResults.Problem)]
    public void SessionDone_ReportedRun_Flags(string reported)
    {
        var store = Store();
        store.Append("cj_8e16c1", Run(reported));

        Assert.Null(ScheduledRunDeletionGuard.RefusalFor(RunSession, RunSession, LookupIn(store)));
    }

    [Fact]
    public void SessionDone_AskedByAnotherSession_Flags()
    {
        var store = Store();
        store.Append("cj_8e16c1", Run(CronRunResults.Pending));
        var asked = 0;

        var refusal = ScheduledRunDeletionGuard.RefusalFor(OtherSession, RunSession, id => { asked++; return LookupIn(store)(id); });

        Assert.Null(refusal);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void SessionDone_AskedByAPerson_Flags()
    {
        var store = Store();
        store.Append("cj_8e16c1", Run(CronRunResults.Pending));

        Assert.Null(ScheduledRunDeletionGuard.RefusalFor(null, RunSession, LookupIn(store)));
    }

    [Fact]
    public void SessionDone_SessionNoScheduleStarted_Flags()
    {
        var store = Store();

        Assert.Null(ScheduledRunDeletionGuard.RefusalFor(RunSession, RunSession, LookupIn(store)));
    }

    [Fact]
    public void SessionDone_OwnIdInOtherCase_StillRefuses()
    {
        var store = Store();
        store.Append("cj_8e16c1", Run(CronRunResults.Pending));

        Assert.NotNull(ScheduledRunDeletionGuard.RefusalFor(RunSession.ToUpperInvariant(), RunSession, LookupIn(store)));
    }

    [Fact]
    public void NotReported_SaysTheWayOutAndThatNothingWasFlagged()
    {
        Assert.Equal(
            "This session is a scheduled run that has not reported. Run `cc-devthrottle run result ok` (it closes this session for you) or `cc-devthrottle run result problem \"<one line why>\"`. Nothing was flagged for deletion.",
            ScheduledRunDeletionGuard.NotReported);
        Assert.All(ScheduledRunDeletionGuard.NotReported, c => Assert.InRange(c, ' ', '~'));
    }
}
