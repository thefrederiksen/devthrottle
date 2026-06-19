using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests that the <see cref="CronEngine"/> DISPATCHES correctly by action type (epic #479, #484): a
/// work-list job goes to the <see cref="ICronWorkListRunner"/> (and records a worklist-* run), a seed
/// job goes to the <see cref="ICronSessionStarter"/>, and a recurring work-list job still advances
/// its schedule.
/// </summary>
public sealed class CronEngineWorkListTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-croneng-wl-tests-" + Guid.NewGuid().ToString("N"));

    private CronJobStore NewJobStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));
    private CronRunHistoryStore NewHistory() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".runs.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CronJobDto WorkListJob() => new()
    {
        Name = "drain",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", WorkListName = "Tonight" },
    };

    private static CronJobDto SeedJob() => new()
    {
        Name = "seed",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    [Fact]
    public async Task WorkListJob_Dispatches_ToWorkListRunner_RecordsWorklistStarted_NoSession()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var runner = new RecordingWorkListRunner(CronWorkListOutcome.Started);
        var starter = new ThrowingStarter();
        var created = store.Create(WorkListJob());
        var engine = new CronEngine(store, history, starter, runner, new FakeClock(DateTime.UtcNow));

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);
        Assert.Equal(1, runner.TriggerCount);                 // routed to the work-list runner
        Assert.NotNull(result.Record);
        Assert.Equal("worklist-started", result.Record.InfraStatus);
        Assert.Null(result.Record.SessionId);                 // a drain starts many sessions, not one
        Assert.Single(history.List(created.Id));
    }

    [Fact]
    public async Task SeedJob_Dispatches_ToSessionStarter_NotWorkListRunner()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var runner = new RecordingWorkListRunner(CronWorkListOutcome.Started);
        var starter = new RecordingStarter();
        var created = store.Create(SeedJob());
        var engine = new CronEngine(store, history, starter, runner, new FakeClock(DateTime.UtcNow));

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);
        Assert.Equal(1, starter.StartCount);                  // routed to the session starter
        Assert.Equal(0, runner.TriggerCount);
        Assert.NotNull(result.Record);
        Assert.Equal("started", result.Record.InfraStatus);
        Assert.Equal("sid-1", result.Record.SessionId);
    }

    [Fact]
    public async Task WorkListJob_EmptyList_RecordsWorklistEmpty()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var runner = new RecordingWorkListRunner(CronWorkListOutcome.EmptyList);
        var created = store.Create(WorkListJob());
        var engine = new CronEngine(store, history, new ThrowingStarter(), runner, new FakeClock(DateTime.UtcNow));

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.NotNull(result.Record);
        Assert.Equal("worklist-empty", result.Record.InfraStatus);
    }

    [Fact]
    public async Task WorkListJob_Recurring_Due_Fires_AndAdvancesSchedule()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var runner = new RecordingWorkListRunner(CronWorkListOutcome.Started);
        var created = store.Create(WorkListJob());
        var clock = new FakeClock(created.NextRunUtc!.Value.AddMinutes(1));
        var engine = new CronEngine(store, history, new ThrowingStarter(), runner, clock);

        var fired = await engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Single(fired);
        Assert.Equal(1, runner.TriggerCount);
        var after = store.Get(created.Id);
        Assert.NotNull(after);
        Assert.True(after.NextRunUtc!.Value > clock.UtcNow);  // schedule advanced
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; }
    }

    private sealed class RecordingWorkListRunner : ICronWorkListRunner
    {
        private readonly CronWorkListOutcome _outcome;
        public int TriggerCount { get; private set; }
        public RecordingWorkListRunner(CronWorkListOutcome outcome) => _outcome = outcome;

        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct)
        {
            TriggerCount++;
            return Task.FromResult(_outcome);
        }
    }

    private sealed class RecordingStarter : ICronSessionStarter
    {
        public int StartCount { get; private set; }
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            StartCount++;
            return Task.FromResult<(string?, string?, string?)>(($"sid-{StartCount}", "director-1", null));
        }
    }

    private sealed class ThrowingStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a work-list-job test must not start a single session");
    }
}
