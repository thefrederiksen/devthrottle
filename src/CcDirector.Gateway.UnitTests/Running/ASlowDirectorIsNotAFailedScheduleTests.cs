using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// SLOW IS NOT FAILED (issue #4748). A Director that takes longer than the Gateway waits to answer a start leaves the
/// run's start outcome not known - the session may still start, and in the issue it did, minutes later. The fire is
/// recorded by the real <see cref="CronEngine"/> and read by the real <see cref="ScheduleFailureNotifier"/>, so this
/// watches the record the engine writes, not one the test builds.
/// </summary>
public sealed class ASlowDirectorIsNotAFailedScheduleTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly List<CronRunCompletedPayload> _fleet = new();
    private readonly List<CronRunCompletedPayload> _pushed = new();

    public ASlowDirectorIsNotAFailedScheduleTests() => _db = _h.Open();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task AFireWhoseDirectorDidNotAnswerInTime_TellsTheOwnerNothing_WhileTheSessionMayStillStart()
    {
        // Arrange: a schedule due now, on a machine whose Director does not answer the start in time.
        var store = new CronJobStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var history = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var job = store.Create(new CronJobDto
        {
            Name = "Morning report",
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = "0 * * * *",
            TimeZoneId = "UTC",
            Target = new CronJobTarget { Machine = "studio-mac" },
            Action = new CronJobAction { RepoPath = "/Users/you/site", Seed = "Write the report." },
        });
        store.MarkFired(job.Id, Now.AddHours(-1), "started", Now, job.Enabled);
        var clock = new FakeClock(Now);
        var engine = new CronEngine(store, history, new SlowDirectorStarter(), new UnusedWorkListRunner(),
            new NullCronNotifier(), clock);
        var inbox = new YourInboxStore(_db);
        var notifier = new ScheduleFailureNotifier(history, new CronFailureNoticeStore(_db),
            jobOf: (_, id) => store.Get(id),
            machineOsOf: (_, _) => DirectorOs.MacOs,
            fleet: new FleetChannel(_fleet),
            push: (_, notice, _) => { _pushed.Add(notice); return Task.CompletedTask; },
            inbox: (tenant, message) => inbox.Add(tenant, message),
            enterScope: _ => new NoScope(),
            clock: () => clock.UtcNow);

        // Act: the schedule fires, and the notifier scans as it does every 30 seconds.
        await engine.EvaluateDueAsync(CancellationToken.None);
        clock.UtcNow = Now.AddSeconds(30);
        var sent = await notifier.ScanAsync(CancellationToken.None);

        // Assert: the run is recorded as start-not-known, and no channel told the owner it failed.
        Assert.Equal(CronRunResults.StartUnknown, Assert.Single(history.List(job.Id)).Result);
        Assert.Equal(0, sent);
        Assert.Empty(_fleet);
        Assert.Empty(_pushed);
        Assert.Empty(inbox.List(TenantId.Local, YourInboxWords.Views.Inbox, YourInboxStore.MaxList).Messages);
    }

    // The starter as DirectorCronSessionStarter answers when the Gateway stopped waiting for the Director's answer.
    private sealed class SlowDirectorStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("the engine calls StartRunAsync");

        public Task<CronRunStart> StartRunAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult(new CronRunStart(null, "director-1", "the Director did not answer within 30 seconds", null)
            {
                OutcomeUnknown = true,
                SessionName = "scheduled-run-1",
            });
    }

    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }

    private sealed class FleetChannel(List<CronRunCompletedPayload> told) : ICronNotifier
    {
        public string BuildSessionLink(string? sessionId) => "";
        public Task NotifyRunCompletedAsync(CronJobDto job, string directorId, CronRunCompletedPayload payload, CancellationToken ct)
        {
            told.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }
}
