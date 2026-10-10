using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="CronEngine"/> (epic #479, #483). A <see cref="FakeClock"/> makes jobs
/// due without waiting on the wall clock and a fake <see cref="ICronSessionStarter"/> stands in for a
/// live Director, so the firing logic and every guard (overlap, disabled, one-off, catch-up) is
/// verified deterministically. Covers AC1, AC4, AC5, AC6, AC7 here; AC2/AC3 (run-now + history shape)
/// are covered by <see cref="CronRunEndpointsTests"/> and <see cref="CronRunHistoryStoreTests"/>.
/// </summary>
public sealed class CronEngineTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private CronJobStore NewJobStore() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
    private CronRunHistoryStore NewHistory() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    public void Dispose()
    {
        _h.Dispose();
    }

    // All tests in this file exercise SEED jobs, so the work-list runner is never invoked; this
    // helper supplies a stub for the #484 constructor parameter without changing any test. A null
    // notifier (#622) stands in for the fleet channel + webhook so the existing tests stay unchanged;
    // the notify-specific assertions live in CronNotifyTests, which drives the notifier directly.
    private static CronEngine Engine(CronJobStore store, CronRunHistoryStore history, ICronSessionStarter starter, IClock clock) =>
        new(store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(), clock);

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }

    private static CronJobDto Recurring(string name = "nightly", bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/work-list run Tonight" },
    };

    private static CronJobDto OneOff(DateTime runAtLocal) => new()
    {
        Name = "once",
        ScheduleKind = CronSchedule.KindOneOff,
        RunAt = runAtLocal.ToString("yyyy-MM-ddTHH:mm:ss"),
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    // A window schedule placed at 02:00 Toronto time, to be done by 04:00 when it has a deadline.
    private static CronJobDto WindowJob(bool deadline) => new()
    {
        Name = "night window",
        Enabled = true,
        ScheduleKind = CronSchedule.KindWindow,
        CronExpression = deadline ? "window=01:00-03:00 deadline=04:00 placed=02:00" : "window=01:00-03:00 placed=02:00",
        TimeZoneId = "America/Toronto",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    [Fact]
    public async Task EvaluateDue_AWindowRunMissedPastItsDeadline_IsNotStarted_AndMovesOnSayingWhy()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(WindowJob(deadline: true));
        Assert.NotNull(created.NextRunUtc);
        // The sweep comes round at 05:00 (the machine slept through 02:00) - an hour past the 04:00 deadline.
        var clock = new FakeClock(created.NextRunUtc.Value.AddHours(3));

        var fired = await Engine(store, history, starter, clock).EvaluateDueAsync(CancellationToken.None);

        Assert.Empty(fired);
        Assert.Equal(0, starter.StartCount);
        // Not started - but no longer skipped in silence: the run is recorded as one that did not run (Factory Control).
        var skipped = Assert.Single(history.List(created.Id));
        Assert.Null(skipped.SessionId);
        Assert.Equal(CronRunResults.Problem, skipped.Result);
        Assert.Equal(CronRunProblems.DidNotRun, skipped.Problem);
        Assert.Equal(created.NextRunUtc, skipped.ScheduledUtc);
        var after = store.Get(created.Id)!;
        Assert.Equal(CronEngine.SkippedPastDeadline, after.LastStatus);
        Assert.Null(after.LastFiredUtc);
        Assert.True(after.NextRunUtc > clock.UtcNow);
    }

    [Fact]
    public async Task EvaluateDue_AWindowRunMissedButStillBeforeItsDeadline_StillCatchesUp()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(WindowJob(deadline: true));
        // The sweep comes round at 03:00, before the 04:00 deadline.
        var clock = new FakeClock(created.NextRunUtc!.Value.AddHours(1));

        var fired = await Engine(store, history, starter, clock).EvaluateDueAsync(CancellationToken.None);

        Assert.Single(fired);
        Assert.Equal("catch-up", fired[0].InfraStatus);
    }

    [Fact]
    public async Task EvaluateDue_AWindowRunWithNoDeadline_CatchesUpHoweverLate()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(WindowJob(deadline: false));
        var clock = new FakeClock(created.NextRunUtc!.Value.AddHours(3));

        var fired = await Engine(store, history, starter, clock).EvaluateDueAsync(CancellationToken.None);

        Assert.Single(fired);
        Assert.Equal(1, starter.StartCount);
    }

    [Fact]
    public async Task EvaluateDue_RecurringJobDue_Fires_RecordsRun_AdvancesNextRun()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(Recurring());
        Assert.NotNull(created.NextRunUtc);

        var clock = new FakeClock(created.NextRunUtc.Value.AddMinutes(1));
        var engine = Engine(store, history, starter, clock);

        var fired = await engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Single(fired);                                     // AC1: it fired
        Assert.Equal(1, starter.StartCount);                       // a session start was attempted
        Assert.Equal("workstation-A", starter.LastJob?.Target.Machine);
        Assert.Single(history.List(created.Id));                   // a run was recorded
        var after = store.Get(created.Id);
        Assert.NotNull(after);
        Assert.NotNull(after.LastFiredUtc);
        Assert.NotNull(after.NextRunUtc);
        Assert.True(after.NextRunUtc.Value > clock.UtcNow);        // schedule advanced into the future
    }

    [Fact]
    public async Task EvaluateDue_DisabledJob_DoesNotFire_ThenEnablingResumes()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(Recurring(enabled: false));
        var clock = new FakeClock((created.NextRunUtc ?? DateTime.UtcNow).AddDays(1));
        var engine = Engine(store, history, starter, clock);

        var firedWhileDisabled = await engine.EvaluateDueAsync(CancellationToken.None);
        Assert.Empty(firedWhileDisabled);                          // AC5: disabled never fires
        Assert.Equal(0, starter.StartCount);

        // Enable it and re-evaluate -> it fires.
        var enabled = Recurring(enabled: true);
        store.Update(created.Id, enabled);
        var fired = await engine.EvaluateDueAsync(CancellationToken.None);
        Assert.Single(fired);
        Assert.Equal(1, starter.StartCount);
    }

    [Fact]
    public async Task EvaluateDue_MissedFireWhileDown_FiresAtMostOnce_NotPerInterval()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(Recurring());                   // daily; NextRunUtc = next midnight

        // The Gateway was "down" for ~3 days: now is far past the due time, spanning several missed
        // daily occurrences.
        var clock = new FakeClock(created.NextRunUtc!.Value.AddDays(3).AddHours(2));
        var engine = Engine(store, history, starter, clock);

        await engine.EvaluateDueAsync(CancellationToken.None);
        await engine.EvaluateDueAsync(CancellationToken.None);     // a second sweep at the same instant

        Assert.Equal(1, starter.StartCount);                        // AC6: exactly one catch-up fire
        Assert.Single(history.List(created.Id));
        var after = store.Get(created.Id);
        Assert.NotNull(after);
        Assert.True(after.NextRunUtc!.Value > clock.UtcNow);        // advanced to the next FUTURE occurrence
        Assert.Equal("catch-up", history.List(created.Id)[0].InfraStatus);
    }

    [Fact]
    public async Task EvaluateDue_OneOff_FiresOnce_ThenAutoDisables()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var starter = new RecordingStarter();
        var created = store.Create(OneOff(DateTime.Now.Date.AddDays(1)));
        var clock = new FakeClock(created.NextRunUtc!.Value.AddMinutes(1));
        var engine = Engine(store, history, starter, clock);

        await engine.EvaluateDueAsync(CancellationToken.None);
        var after = store.Get(created.Id);
        Assert.NotNull(after);
        Assert.False(after.Enabled);                                // AC7: one-off auto-disables
        Assert.Null(after.NextRunUtc);

        // A later sweep does not fire it again.
        var clock2Engine = Engine(store, history, starter, new FakeClock(clock.UtcNow.AddDays(1)));
        var firedAgain = await clock2Engine.EvaluateDueAsync(CancellationToken.None);
        Assert.Empty(firedAgain);
        Assert.Equal(1, starter.StartCount);
    }

    [Fact]
    public async Task RunNow_WhilePriorRunInFlight_SecondIsSkippedAsOverlap()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var blocking = new BlockingStarter();
        var created = store.Create(Recurring());                   // PreventOverlap defaults true
        var engine = Engine(store, history, blocking, new FakeClock(DateTime.UtcNow));

        // Start the first run; it blocks inside the starter.
        var firstRun = engine.RunNowAsync(created.Id, CancellationToken.None);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A second run-now while the first is in flight is skipped.
        var second = await engine.RunNowAsync(created.Id, CancellationToken.None);
        Assert.Equal(CronFireOutcome.SkippedOverlap, second.Outcome);   // AC4

        blocking.Release.TrySetResult();
        var first = await firstRun;
        Assert.Equal(CronFireOutcome.Fired, first.Outcome);
        Assert.Equal(1, blocking.StartCount);                            // only one session ever started
    }

    [Fact]
    public async Task RunNow_NoSuchJob_ReturnsNoSuchJob()
    {
        var engine = Engine(NewJobStore(), NewHistory(), new RecordingStarter(), new FakeClock(DateTime.UtcNow));
        var result = await engine.RunNowAsync("cj_nope", CancellationToken.None);
        Assert.Equal(CronFireOutcome.NoSuchJob, result.Outcome);
    }

    [Fact]
    public async Task Fire_WhenStarterReportsError_RecordsNotStarted()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var created = store.Create(Recurring());
        var engine = Engine(store, history, new FailingStarter(), new FakeClock(DateTime.UtcNow));

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);     // a run is still recorded
        Assert.NotNull(result.Record);
        Assert.Equal("not-started", result.Record.InfraStatus);
        Assert.Null(result.Record.SessionId);
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; set; }
    }

    private sealed class RecordingStarter : ICronSessionStarter
    {
        public int StartCount { get; private set; }
        public CronJobDto? LastJob { get; private set; }

        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            StartCount++;
            LastJob = job;
            return Task.FromResult<(string?, string?, string?)>(($"sid-{StartCount}", "director-1", null));
        }
    }

    private sealed class FailingStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult<(string?, string?, string?)>((null, null, "target director not registered"));
    }

    private sealed class BlockingStarter : ICronSessionStarter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount { get; private set; }

        public async Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            StartCount++;
            Entered.TrySetResult();
            await Release.Task;
            return ($"sid-{StartCount}", "director-1", null);
        }
    }
}
