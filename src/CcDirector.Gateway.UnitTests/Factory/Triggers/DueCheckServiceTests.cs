using CcDirector.ControlApi.Triggers;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// A TIMED START'S DUE CHECK (Factory Definition, phase 5b, design 3.3): handed only to a capable Director on the
/// schedule's machine and claimed by it; reported only by the claim holder; decided by the Gateway - a failed check is
/// a problem, a quiet one is nothing to do, a check that counts work starts ONE session through the schedule's own
/// starter on the Director that ran it - and its state is kept only when the work was dealt with.
/// </summary>
public sealed class DueCheckServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId T = TenantId.Local;
    private const string Machine = "SOREN_NORTH";
    private const string Capable = "dir-new";
    private const string Older = "dir-old";
    private const string OtherCapable = "dir-new-2";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly CronJobStore _jobs;
    private readonly CronRunHistoryStore _runs;
    private readonly FactoryCheckStateStore _states;
    private readonly PinnedStarter _starter = new();
    private readonly List<CronRunRecord> _recorded = new();
    private DateTime _now = Now;

    public DueCheckServiceTests()
    {
        _db = _h.Open();
        _jobs = new CronJobStore(_db, _h.LegacyPath("jobs.json"));
        _runs = new CronRunHistoryStore(_db, _h.LegacyPath("runs.json"));
        _states = new FactoryCheckStateStore(_db);
    }

    public void Dispose() => _h.Dispose();

    private DueCheckService Service() => new(_runs, (tenant, id) => _jobs.Get(tenant, id), _states, _starter,
        directorRunsDefinitionChecks: (_, director) => director != Older,
        resultRecorded: (_, run) => _recorded.Add(run), nowUtc: () => _now, startLifetime: CancellationToken.None);

    /// <summary>A schedule of the mail factory with a check, and one fire of it: the run waiting on its check.</summary>
    private async Task<(CronJobDto Job, string RunId)> ADueCheck(string machine = Machine)
    {
        var created = _jobs.Create(new CronJobDto
        {
            Name = "Mail Desk",
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Factory = "mail",
            Seat = "desk",
            Target = new CronJobTarget { Machine = machine },
            Action = new CronJobAction { RepoPath = @"D:\factories\mail", Seed = "New mail: {count}. Found: {context}" },
        });
        using (var ctx = _db.CreateContext(T))
        {
            var row = ctx.CronJobs.Single(j => j.Id == created.Id);
            CronJobStore.ApplyCheck(row, new CronJobDto { CheckPath = "checks/new_mail.py", CheckSecrets = new List<string> { "mail-imap" } }, 4);
            ctx.SaveChanges();
        }
        var engine = new CronEngine(_jobs, _runs, _starter, new NoWorkLists(), new NullCronNotifier(), new FixedClock(Now));
        var fired = await engine.RunNowAsync(created.Id, CancellationToken.None);
        Assert.Equal(CronRunResults.Checking, fired.Record!.Result);
        return (_jobs.Get(created.Id)!, fired.Record.RunId);
    }

    private static TriggerCheckReport Report(string output, string? state = null, int exitCode = 0) => new()
    {
        CheckedAtUtc = Now,
        ExitCode = exitCode,
        Output = output,
        State = state,
    };

    private CronRunRecord Run(string runId) => _runs.Find(T, Guid.Parse(runId))!.Run;

    // ---------- handing out and claiming ----------

    [Fact]
    public async Task AssignmentsFor_ACapableDirector_IsHandedTheDueCheck_OneShot_WithItsStateAndAFixedTimeout()
    {
        var (job, runId) = await ADueCheck();
        _states.RecordIfChanged(T, "mail", FactoryCheckStateStore.ScheduleOwner(job.Id), """{"seen":"m-7"}""", Capable, "an-earlier-run", Now.AddDays(-1));

        var handed = Assert.Single(Service().AssignmentsFor(T, Capable, Machine));

        Assert.Equal((runId, runId, TriggerKinds.Definition), (handed.DueRunId, handed.ReportId, handed.Kind));
        Assert.Equal((TriggerCheckLimits.DueCheckTimeoutSeconds, "", ""), (handed.TimeoutSeconds, handed.CheckCommand, handed.RepoPath));
        Assert.Equal(("mail", "desk", 4, "checks/new_mail.py"), (handed.Factory, handed.Seat, handed.DefinitionVersion, handed.CheckPath));
        Assert.Equal(new[] { "mail-imap" }, handed.Secrets);
        Assert.Equal("""{"seen":"m-7"}""", handed.State);
        Assert.Equal(300, TriggerCheckLimits.DueCheckTimeoutSeconds);
    }

    [Fact]
    public async Task AssignmentsFor_AnOlderDirector_IsHandedNothing_AndClaimsNothing()
    {
        await ADueCheck();
        var service = Service();

        Assert.Empty(service.AssignmentsFor(T, Older, Machine));
        Assert.Single(service.AssignmentsFor(T, Capable, Machine));
    }

    [Fact]
    public async Task AssignmentsFor_ACheckClaimedByAnotherDirector_IsNotHandedOut_UntilTheClaimLapsesAfterSixMinutes()
    {
        await ADueCheck();
        var service = Service();
        Assert.Single(service.AssignmentsFor(T, Capable, Machine));

        _now = Now.AddMinutes(5);
        Assert.Empty(service.AssignmentsFor(T, OtherCapable, Machine));

        _now = Now.AddMinutes(6).AddSeconds(1);
        Assert.Single(service.AssignmentsFor(T, OtherCapable, Machine));
        Assert.Equal(TimeSpan.FromMinutes(6), DueCheckService.ClaimLapse);
    }

    [Fact]
    public async Task AssignmentsFor_ADirectorOnAnotherMachine_IsHandedNothing()
    {
        await ADueCheck();

        Assert.Empty(Service().AssignmentsFor(T, Capable, "SOREN_SOUTH"));
    }

    // ---------- the report ----------

    [Fact]
    public async Task Report_FromADirectorNotHoldingTheClaim_IsRefused_AndRecordsNothing()
    {
        var (_, runId) = await ADueCheck();
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);

        var refused = service.Report(T, OtherCapable, Guid.Parse(runId), Report("""{"count": 0}"""));

        Assert.Equal(DueCheckRefusal.HeldByAnotherDirector, refused.Refusal);
        Assert.Equal($"Director {Capable} runs the check of run {runId}; this report was not recorded", refused.Error);
        Assert.Equal(CronRunResults.Checking, Run(runId).Result);
    }

    [Fact]
    public async Task Report_FromAnOlderDirector_IsRefused()
    {
        var (_, runId) = await ADueCheck();

        var refused = Service().Report(T, Older, Guid.Parse(runId), Report("""{"count": 0}"""));

        Assert.Equal(DueCheckRefusal.CannotRunDefinitionChecks, refused.Refusal);
    }

    [Fact]
    public async Task Report_ACheckThatBrokeItsContract_IsACheckFailedProblem_WithTheContractsReason_AndKeepsNoState()
    {
        var (job, runId) = await ADueCheck();
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);

        var result = service.Report(T, Capable, Guid.Parse(runId), Report("not json at all", state: """{"half":"written"}"""));

        Assert.Equal(DueCheckRefusal.None, result.Refusal);
        var run = Run(runId);
        Assert.Equal((CronRunResults.Problem, CronRunProblems.CheckFailed), (run.Result, run.Problem));
        Assert.Equal("the output is not JSON: not json at all", run.ResultReason);
        Assert.Null(_states.Newest(T, "mail", FactoryCheckStateStore.ScheduleOwner(job.Id)));
        Assert.Equal(runId, Assert.Single(_recorded).RunId);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task Report_ACheckThatCountedNothing_IsNothingToDo_StartsNoSession_AndKeepsItsState()
    {
        var (job, runId) = await ADueCheck();
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);

        service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 0}""", state: """{"seen":"m-9"}"""));

        var run = Run(runId);
        Assert.Equal((CronRunResults.NothingToDo, (string?)null, 0), (run.Result, run.Problem, run.CheckCount));
        Assert.Equal(CronRunResults.NothingToDo, run.InfraStatus);
        Assert.Empty(_starter.Started);
        Assert.Equal("""{"seen":"m-9"}""", _states.StateFor(T, "mail", FactoryCheckStateStore.ScheduleOwner(job.Id)));
        Assert.Equal(runId, Assert.Single(_recorded).RunId);
    }

    [Fact]
    public async Task Report_ACheckThatCountedWork_StartsOneSessionThroughTheSchedulesStarter_PinnedToTheDirectorThatRanIt()
    {
        var (job, runId) = await ADueCheck();
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);

        var result = service.Report(T, Capable, Guid.Parse(runId),
            Report("""{"count": 2, "context": "from ada@example.com"}""", state: """{"seen":"m-12"}"""));
        Assert.Equal(2, result.StartingCount);
        var finished = await result.Starting!;

        var (startedJob, director, seed) = Assert.Single(_starter.Started);
        Assert.Equal((job.Id, Capable), (startedJob.Id, director));
        Assert.Equal("New mail: 2. Found: from ada@example.com", seed);
        Assert.Equal((CronRunResults.Pending, "sid-checked-1", Capable), (finished!.Result, finished.SessionId, finished.TargetDirectorId));
        Assert.Equal((2, "started"), (finished.CheckCount, finished.InfraStatus));
        Assert.Equal("""{"seen":"m-12"}""", _states.StateFor(T, "mail", FactoryCheckStateStore.ScheduleOwner(job.Id)));
    }

    [Fact]
    public async Task Report_ACheckThatCountedWork_WhoseStartFails_IsDidNotRun_AndKeepsNoState()
    {
        var (job, runId) = await ADueCheck();
        _starter.Fail = "the machine is offline";
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);

        var finished = await service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 1}""", state: """{"seen":"m-13"}""")).Starting!;

        Assert.Equal((CronRunResults.Problem, CronRunProblems.DidNotRun), (finished!.Result, finished.Problem));
        Assert.Equal("it did not start a session: the machine is offline", finished.ResultReason);
        Assert.Null(_states.Newest(T, "mail", FactoryCheckStateStore.ScheduleOwner(job.Id)));
        Assert.Equal(runId, Assert.Single(_recorded).RunId);
    }

    [Fact]
    public async Task Report_ASecondReportOfTheSameCheck_IsAlreadyRecorded_InTheWordTheDirectorTreatsAsFinal()
    {
        var (_, runId) = await ADueCheck();
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);
        service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 0}"""));

        var again = service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 3}"""));

        Assert.Equal(DueCheckRefusal.AlreadyRecorded, again.Refusal);
        Assert.True(DirectorTriggerRunner.IsAlreadyRecorded($"HTTP 409: {{\"code\":\"already_recorded\",\"error\":\"{again.Error}\"}}"));
        Assert.Equal(CronRunResults.NothingToDo, Run(runId).Result);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task Report_WhileTheSessionItAskedForIsStillStarting_ASecondReportIsAlreadyRecorded_AndStartsNothing()
    {
        var (_, runId) = await ADueCheck();
        _starter.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);
        var first = service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 1}"""));

        var second = service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 1}"""));
        Assert.Empty(service.AssignmentsFor(T, Capable, Machine));
        _starter.Gate.SetResult();
        await first.Starting!;

        Assert.Equal(DueCheckRefusal.AlreadyRecorded, second.Refusal);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public async Task AssignmentsFor_AScheduleWhoseMachineIsSpelledInAnotherCase_IsHandedToTheDirectorOnThatMachine()
    {
        // A seat written "Soren_North" publishes and its plain runs start there; its due check must reach it too.
        var (_, runId) = await ADueCheck(machine: "Soren_North");

        var handed = Assert.Single(Service().AssignmentsFor(T, Capable, "SOREN_NORTH"));

        Assert.Equal(runId, handed.DueRunId);
    }

    [Fact]
    public async Task Report_ACheckThatCountedWork_WhoseStartIsSlowerThanTheInterval_IsNotSuperseded_AndKeepsItsSession()
    {
        // The check counted work and its session is starting when the schedule's next time comes.
        var (job, runId) = await ADueCheck();
        _starter.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);
        var first = service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 2}"""));
        await new CronEngine(_jobs, _runs, _starter, new NoWorkLists(), new NullCronNotifier(), new FixedClock(Now.AddHours(1)))
            .RunNowAsync(job.Id, CancellationToken.None);
        Assert.Equal(CronRunResults.Checking, Run(runId).Result);

        // Act: the start returns after the next time.
        _starter.Gate.SetResult();
        await first.Starting!;

        // Assert: the run that asked for the session holds it, kept at the time it fired; nothing reads "check did not run".
        var run = Run(runId);
        Assert.Equal((CronRunResults.Pending, "sid-checked-1", (string?)null), (run.Result, run.SessionId, run.Problem));
        Assert.Equal(Now, run.FiredUtc);
        Assert.DoesNotContain(_runs.List(job.Id), r => r.Problem == CronRunProblems.CheckNotRun);
    }

    [Fact]
    public async Task Report_ARunThatIsNoLongerChecking_IsAlreadyRecorded()
    {
        var (job, runId) = await ADueCheck();
        var service = Service();
        service.AssignmentsFor(T, Capable, Machine);
        // The schedule's next time came first: the run was superseded.
        await new CronEngine(_jobs, _runs, _starter, new NoWorkLists(), new NullCronNotifier(), new FixedClock(Now.AddHours(1)))
            .RunNowAsync(job.Id, CancellationToken.None);

        var refused = service.Report(T, Capable, Guid.Parse(runId), Report("""{"count": 1}"""));

        Assert.Equal(DueCheckRefusal.AlreadyRecorded, refused.Refusal);
        Assert.Equal(CronRunProblems.CheckNotRun, Run(runId).Problem);
    }

    // ---------- fakes ----------

    private sealed class PinnedStarter : ICronSessionStarter
    {
        public List<(CronJobDto Job, string Director, string Seed)> Started { get; } = new();
        public string? Fail { get; set; }
        public TaskCompletionSource? Gate { get; set; }

        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
            => throw new InvalidOperationException("a due check's session is started pinned, through StartCheckedRunAsync");

        public async Task<CronRunStart> StartCheckedRunAsync(CronJobDto job, string directorId, string seed, CancellationToken ct)
        {
            Started.Add((job, directorId, seed));
            if (Gate is not null) await Gate.Task;
            return Fail is null
                ? new CronRunStart($"sid-checked-{Started.Count}", directorId, null, BriefVersion: null)
                : new CronRunStart(null, directorId, Fail, BriefVersion: null);
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class NoWorkLists : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("no work list in this test");
    }
}
