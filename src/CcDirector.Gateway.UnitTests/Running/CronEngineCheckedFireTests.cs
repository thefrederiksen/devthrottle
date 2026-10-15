using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// THE FIRE OF A SCHEDULE WITH A CHECK (Factory Definition, phase 5b, design 3.3): it starts no session and calls no
/// Director - it writes the due check and moves on - so a sweep never waits on a check; a due check nobody ran before
/// the next time becomes one "check did not run" problem; and a schedule with no check fires exactly as before.
/// </summary>
public sealed class CronEngineCheckedFireTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    public void Dispose() => _h.Dispose();

    private CronJobStore NewJobStore() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
    private CronRunHistoryStore NewHistory() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    private static CronEngine Engine(CronJobStore store, CronRunHistoryStore history, ICronSessionStarter starter, IClock clock,
        List<CronRunRecord>? announced = null, bool factoryAgentsOn = true)
        => new(store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(), clock,
            onResultRecorded: (_, _, run) => announced?.Add(run), factoryAgentsOn: _ => factoryAgentsOn);

    private static CronJobDto Hourly(string name) => new()
    {
        Name = name,
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 * * * *",
        TimeZoneId = "UTC",
        Factory = "mail",
        Seat = "desk",
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\factories\mail", Seed = "New mail: {count}." },
    };

    // A schedule is given its check only by a publish (CronJobStore.ApplyCheck); the test writes it the same way.
    private CronJobDto WithCheck(CronJobStore store, CronJobDto created, string checkPath = "checks/new_mail.py")
    {
        using (var ctx = Db.CreateContext(TenantId.Local))
        {
            var row = ctx.CronJobs.Single(j => j.Id == created.Id);
            CronJobStore.ApplyCheck(row, new CronJobDto { CheckPath = checkPath, CheckSecrets = new List<string> { "mail-imap" } }, 3);
            ctx.SaveChanges();
        }
        return store.Get(created.Id)!;
    }

    private static void MakeDue(CronJobStore store, string id, DateTime dueUtc)
    {
        var job = store.Get(id)!;
        store.MarkFired(id, dueUtc.AddHours(-1), "started", dueUtc, job.Enabled);
    }

    [Fact]
    public async Task EvaluateDueAsync_ACheckedJobAndTwoOrdinaryJobs_FiresAllThreeOnTime_WhileTheCheckIsUnreported()
    {
        // Arrange: three schedules due at 13:00, one of them with a check nobody has run.
        var store = NewJobStore();
        var history = NewHistory();
        var checkedJob = WithCheck(store, store.Create(Hourly("Mail Desk")));
        var first = store.Create(Hourly("Scout"));
        var second = store.Create(Hourly("Writer"));
        foreach (var id in new[] { checkedJob.Id, first.Id, second.Id }) MakeDue(store, id, Now);
        var starter = new RecordingStarter();
        var engine = Engine(store, history, starter, new FakeClock(Now));

        // Act
        var fired = await engine.EvaluateDueAsync(CancellationToken.None);

        // Assert: all three fired at 13:00; the two ordinary ones started their sessions, the checked one started none.
        Assert.Equal(3, fired.Count);
        Assert.Equal(new[] { "Scout", "Writer" }, starter.Started.Select(j => j.Name).OrderBy(n => n));
        var due = Assert.Single(history.List(checkedJob.Id));
        Assert.Equal((CronRunResults.Checking, CronEngine.CheckDueStatus, (string?)null), (due.Result, due.InfraStatus, due.SessionId));
        Assert.Equal(Now, due.FiredUtc);
        Assert.All(new[] { first.Id, second.Id }, id =>
            Assert.Equal(CronRunResults.Pending, Assert.Single(history.List(id)).Result));
        // Every schedule moved on to its next time, the checked one included.
        Assert.All(new[] { checkedJob.Id, first.Id, second.Id }, id => Assert.Equal(Now.AddHours(1), store.Get(id)!.NextRunUtc));
    }

    [Fact]
    public async Task EvaluateDueAsync_ACheckedJobWhosePreviousRunIsStillChecking_RecordsCheckNotRun_AndWritesTheNewDueCheck()
    {
        // Arrange: the 13:00 due check was never run by any Director.
        var store = NewJobStore();
        var history = NewHistory();
        var job = WithCheck(store, store.Create(Hourly("Mail Desk")));
        MakeDue(store, job.Id, Now);
        var clock = new FakeClock(Now);
        var announced = new List<CronRunRecord>();
        var engine = Engine(store, history, new RecordingStarter(), clock, announced);
        await engine.EvaluateDueAsync(CancellationToken.None);

        // Act: its next time comes.
        clock.UtcNow = Now.AddHours(1);
        await engine.EvaluateDueAsync(CancellationToken.None);

        // Assert
        var runs = history.List(job.Id);
        Assert.Equal(2, runs.Count);
        Assert.Equal(CronRunResults.Checking, runs[0].Result);
        Assert.Equal((CronRunResults.Problem, CronRunProblems.CheckNotRun), (runs[1].Result, runs[1].Problem));
        Assert.Equal("no Director on SOREN_NORTH ran the check of Mail Desk before its next time", runs[1].ResultReason);
        Assert.Equal(runs[1].RunId, Assert.Single(announced).RunId);
    }

    [Fact]
    public async Task EvaluateDueAsync_OnAnAccountWithFactoryAgentsSwitchedOff_SaysTheSwitch_NotThatNoDirectorRanIt()
    {
        // Arrange: the account's Factory Agents switch is off, so no Director can be handed the check.
        var store = NewJobStore();
        var history = NewHistory();
        var job = WithCheck(store, store.Create(Hourly("Mail Desk")));
        MakeDue(store, job.Id, Now);
        var clock = new FakeClock(Now);
        var engine = Engine(store, history, new RecordingStarter(), clock, factoryAgentsOn: false);
        await engine.EvaluateDueAsync(CancellationToken.None);

        // Act: its next time comes.
        clock.UtcNow = Now.AddHours(1);
        await engine.EvaluateDueAsync(CancellationToken.None);

        // Assert
        var superseded = history.List(job.Id)[1];
        Assert.Equal(CronRunProblems.CheckNotRun, superseded.Problem);
        Assert.Equal("factory agents are switched off for this account, so no Director is handed the check; the check of Mail Desk did not run before its next time",
            superseded.ResultReason);
        Assert.Equal("check-due: factory agents are switched off for this account, so no Director is handed the check",
            store.Get(job.Id)!.LastStatus);
        Assert.Equal(CronEngine.CheckDueSwitchedOffStatus, history.List(job.Id)[0].InfraStatus);
    }

    [Fact]
    public async Task EvaluateDueAsync_ADueCheckOffForThreeTimes_IsThreeProblems_NotAPileOfWaitingRows()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var job = WithCheck(store, store.Create(Hourly("Mail Desk")));
        MakeDue(store, job.Id, Now);
        var clock = new FakeClock(Now);
        var engine = Engine(store, history, new RecordingStarter(), clock);

        for (var hour = 0; hour < 4; hour++)
        {
            clock.UtcNow = Now.AddHours(hour);
            await engine.EvaluateDueAsync(CancellationToken.None);
        }

        var runs = history.List(job.Id);
        Assert.Equal(1, runs.Count(r => r.Result == CronRunResults.Checking));
        Assert.Equal(3, runs.Count(r => r.Problem == CronRunProblems.CheckNotRun));
    }

    [Fact]
    public async Task RunNowAsync_ACheckedJob_WritesADueCheckAndStartsNoSession()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var job = WithCheck(store, store.Create(Hourly("Mail Desk")));
        var starter = new RecordingStarter();

        var result = await Engine(store, history, starter, new FakeClock(Now)).RunNowAsync(job.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);
        Assert.Equal(CronRunResults.Checking, result.Record!.Result);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task EvaluateDueAsync_AScheduleWithNoCheck_FiresAndRecordsExactlyAsBefore()
    {
        // Goal 14: a schedule a publish never gave a check is untouched by phase 5b.
        var store = NewJobStore();
        var history = NewHistory();
        var job = store.Create(Hourly("Scout"));
        MakeDue(store, job.Id, Now);
        var starter = new RecordingStarter();

        await Engine(store, history, starter, new FakeClock(Now)).EvaluateDueAsync(CancellationToken.None);

        var run = Assert.Single(history.List(job.Id));
        Assert.Equal((CronRunResults.Pending, "started", "sid-1", "director-1"), (run.Result, run.InfraStatus, run.SessionId, run.TargetDirectorId));
        Assert.Null(run.CheckCount);
        Assert.Null(store.Get(job.Id)!.CheckPath);
        Assert.Equal("New mail: {count}.", Assert.Single(starter.Started).Action.Seed);
    }

    [Fact]
    public void CreateAndUpdate_ABodyNamingACheck_GiveNoScheduleACheck_AndAnUpdateKeepsTheCheckAPublishWrote()
    {
        // The schedule routes write through Create and Update; a check is written only by a publish (design 3.3).
        var store = NewJobStore();
        var asked = Hourly("Mail Desk");
        asked.CheckPath = "checks/new_mail.py";
        asked.CheckSecrets = new List<string> { "mail-imap" };

        var created = store.Create(asked);
        var plain = store.Create(Hourly("Scout"));
        var updatedPlain = store.Update(plain.Id, asked)!;
        var published = WithCheck(store, store.Create(Hourly("Writer")));
        var edited = Hourly("Writer");
        edited.Action.Seed = "Write {count}.";
        var updatedPublished = store.Update(published.Id, edited)!;

        Assert.Equal((false, false), (created.HasCheck, updatedPlain.HasCheck));
        Assert.Equal(("checks/new_mail.py", 3, "Write {count}."),
            (updatedPublished.CheckPath, updatedPublished.DefinitionVersion, updatedPublished.Action.Seed));
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }

    private sealed class RecordingStarter : ICronSessionStarter
    {
        public List<CronJobDto> Started { get; } = new();

        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            Started.Add(job);
            return Task.FromResult<(string?, string?, string?)>(($"sid-{Started.Count}", "director-1", null));
        }
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }
}
