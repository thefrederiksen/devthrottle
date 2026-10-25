using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Events;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the per-job run-complete notification (issue #622): the firing engine notifies a started run when the job
/// asks for every run, a failed run is told by the failure notifier (issue #4436: on failure is the default), the
/// policy honors always vs failure vs none, and the production notifier rides the existing fleet event ring AND posts
/// the optional webhook with the same payload. Covers AC1, AC2, AC3, AC5, and the AC4 turned-off guard.
/// </summary>
public sealed class CronNotifyTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private CronJobStore NewJobStore() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
    /// <summary>The one moment these tests run at: the engine's clock, the failure notifier's "now" and every fire time.</summary>
    private static readonly DateTime Now = new(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

    private CronRunHistoryStore NewHistory() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    public void Dispose()
    {
        _h.Dispose();
    }

    private static CronEngine Engine(CronJobStore store, CronRunHistoryStore history, ICronSessionStarter starter, ICronNotifier notifier) =>
        new(store, history, starter, new UnusedWorkListRunner(), notifier, new FakeClock(Now));

    private static CronJobDto SeedJob(string notifyOn, string? webhook = null, bool enabled = true) => new()
    {
        Name = "nightly drain",
        Enabled = enabled,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        NotifyOn = notifyOn,
        NotifyWebhookUrl = webhook,
    };

    // ---- AC1: a notify-enabled job notifies on a successful finish, carrying the run details ----

    [Fact]
    public async Task NotifyAlways_SuccessfulFire_Notifies_WithJobNameOutcomeMachineSessionAndLink()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var notifier = new RecordingCronNotifier();
        var created = store.Create(SeedJob(CronNotify.Always));
        var engine = Engine(store, history, new OkStarter(), notifier);

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);
        Assert.Equal(1, notifier.CallCount);                       // AC1: it notified
        var p = notifier.Last!;
        Assert.Equal(created.Id, p.JobId);
        Assert.Equal("nightly drain", p.JobName);
        Assert.True(p.Succeeded);
        Assert.Equal("started", p.InfraStatus);
        Assert.Equal("unknown", p.TaskStatus);
        Assert.Equal("workstation-A", p.Machine);
        Assert.Equal("sid-1", p.SessionId);
        Assert.Contains("sid-1", p.SessionLink);                   // a working link to the session
        Assert.Null(p.Reason);
    }

    // ---- AC2: a job whose fire FAILS tells its owner, with a plain sentence and the raw reason as the detail ----
    // Issue #4436: a failure is no longer told by the engine at fire time but by ScheduleFailureNotifier from the run
    // history - one path for every failure, missed runs included, limited to one notice an hour for each cause.

    private ScheduleFailureNotifier FailureNotifier(CronJobStore store, CronRunHistoryStore history, ICronNotifier fleet) =>
        new(history, new CronFailureNoticeStore(Db), (_, id) => store.Get(id), (_, _) => null, fleet,
            (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.FromResult<string?>("no account to email in this test"),
            _ => new NoScope(), () => Now);

    [Fact]
    public async Task NotifyAlways_FailedFire_IsToldByTheFailureNotifier_WithAPlainSentence()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var notifier = new RecordingCronNotifier();
        var created = store.Create(SeedJob(CronNotify.Always));
        var engine = Engine(store, history, new FailingStarter("target director not registered"), notifier);

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);       // a run is still recorded
        Assert.Equal(0, notifier.CallCount);                       // the engine no longer tells a failure itself
        Assert.Equal(1, await FailureNotifier(store, history, notifier).ScanAsync(CancellationToken.None));
        var p = notifier.Last!;
        Assert.False(p.Succeeded);
        Assert.Equal("not-started", p.InfraStatus);
        Assert.Null(p.SessionId);
        Assert.Equal("", p.SessionLink);                           // no session -> no link
        Assert.Equal("This run did not start on workstation-A. The details below say why.", p.Reason);
        Assert.Equal("it did not start a session: target director not registered", p.Detail);
    }

    // ---- AC3: notifyOn honors always vs failure ----

    [Fact]
    public async Task NotifyFailure_Success_DoesNotNotify_Failure_Notifies()
    {
        var store = NewJobStore();
        var history = NewHistory();

        // success under "failure" -> nothing.
        var notifierA = new RecordingCronNotifier();
        var okJob = store.Create(SeedJob(CronNotify.Failure));
        await Engine(store, history, new OkStarter(), notifierA).RunNowAsync(okJob.Id, CancellationToken.None);
        Assert.Equal(0, notifierA.CallCount);                      // AC3: success under failure is silent
        Assert.Equal(0, await FailureNotifier(store, history, notifierA).ScanAsync(CancellationToken.None));

        // failure under "failure" -> notifies.
        var notifierB = new RecordingCronNotifier();
        var failJob = store.Create(SeedJob(CronNotify.Failure));
        await Engine(store, history, new FailingStarter("boom"), notifierB).RunNowAsync(failJob.Id, CancellationToken.None);
        Assert.Equal(1, await FailureNotifier(store, history, notifierB).ScanAsync(CancellationToken.None));
        Assert.False(notifierB.Last!.Succeeded);
    }

    // ---- AC4: a job whose owner turned notifications off never notifies ----

    [Fact]
    public async Task NotifyNone_NeitherSuccessNorFailure_Notifies()
    {
        var store = NewJobStore();
        var history = NewHistory();

        var notifierOk = new RecordingCronNotifier();
        var okJob = store.Create(SeedJob(CronNotify.None));
        await Engine(store, history, new OkStarter(), notifierOk).RunNowAsync(okJob.Id, CancellationToken.None);
        Assert.Equal(0, notifierOk.CallCount);                     // off: silent on success

        var notifierFail = new RecordingCronNotifier();
        var failJob = store.Create(SeedJob(CronNotify.None));
        await Engine(store, history, new FailingStarter("x"), notifierFail).RunNowAsync(failJob.Id, CancellationToken.None);
        Assert.Equal(0, await FailureNotifier(store, history, notifierFail).ScanAsync(CancellationToken.None));
        Assert.Equal(0, notifierFail.CallCount);                   // off: silent on failure too
    }

    [Fact]
    public async Task UnsetNotifyOn_DefaultsToFailure_SilentOnSuccess_ToldOnFailure()
    {
        // Issue #4436: a schedule nobody chose notifications for tells its owner when a run fails.
        var store = NewJobStore();
        var history = NewHistory();
        var job = SeedJob(CronNotify.Always);
        job.NotifyOn = "";
        var created = store.Create(job);
        Assert.Equal(CronNotify.Failure, created.NotifyOn);
        Assert.Equal(CronNotify.Failure, new CronJobDto().NotifyOn);

        var okNotifier = new RecordingCronNotifier();
        await Engine(store, history, new OkStarter(), okNotifier).RunNowAsync(created.Id, CancellationToken.None);
        Assert.Equal(0, okNotifier.CallCount);

        var failNotifier = new RecordingCronNotifier();
        await Engine(store, history, new FailingStarter("x"), failNotifier).RunNowAsync(created.Id, CancellationToken.None);
        Assert.Equal(1, await FailureNotifier(store, history, failNotifier).ScanAsync(CancellationToken.None));
    }

    // ---- CronNotify policy helper ----

    [Theory]
    [InlineData(CronNotify.None, true, false)]
    [InlineData(CronNotify.None, false, false)]
    [InlineData(CronNotify.Always, true, true)]
    [InlineData(CronNotify.Always, false, true)]
    [InlineData(CronNotify.Failure, true, false)]
    [InlineData(CronNotify.Failure, false, true)]
    [InlineData("", true, false)]                                  // unset -> failure (the default, issue #4436)
    [InlineData("", false, true)]
    [InlineData("BOGUS", false, true)]                             // unknown -> failure
    public void ShouldNotify_HonorsPolicy(string policy, bool succeeded, bool expected)
    {
        Assert.Equal(expected, CronNotify.ShouldNotify(policy, succeeded));
    }

    [Theory]
    [InlineData("always", "always")]
    [InlineData("FAILURE", "failure")]
    [InlineData("  None  ", "none")]
    [InlineData("", "failure")]
    [InlineData(null, "failure")]
    [InlineData("nonsense", "failure")]
    public void Normalize_MapsToCanonicalOrTheDefault(string? input, string expected)
    {
        Assert.Equal(expected, CronNotify.Normalize(input));
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }

    // ---- AC5: the production notifier rides the existing event ring AND posts the webhook ----

    [Fact]
    public async Task GatewayNotifier_RecordsOntoExistingEventRing_AndPostsWebhook_SamePayload()
    {
        var events = new DirectorEventLog();
        var capture = new CapturingHandler();
        using var http = new HttpClient(capture);
        var notifier = new GatewayCronNotifier(events, () => "http://127.0.0.1:7879", http);

        var job = SeedJob(CronNotify.Always, webhook: "https://example.com/hook");
        job.Id = "cj_test";
        var payload = new CronRunCompletedPayload
        {
            JobId = "cj_test",
            JobName = "nightly drain",
            Succeeded = true,
            InfraStatus = "started",
            TaskStatus = "unknown",
            Machine = "workstation-A",
            SessionId = "sid-1",
            SessionLink = notifier.BuildSessionLink("sid-1"),
            FiredUtc = Now,
        };

        await notifier.NotifyRunCompletedAsync(job, "director-1", payload, CancellationToken.None);

        // Leg 1: it landed on the EXISTING per-Director event ring under the resolved director, with
        // the cron-run-completed event name (the fleet channel, not a new mechanism). AC5.
        var ring = events.For(TenantId.Local, "director-1");
        Assert.Single(ring);
        Assert.Equal(DoorbellEvents.CronRunCompleted, ring[0].Event);
        Assert.Equal("sid-1", ring[0].SessionId);
        Assert.Equal("started", ring[0].State);

        // Leg 2: the webhook received a POST with the SAME payload. AC5.
        Assert.NotNull(capture.LastBody);
        var posted = JsonSerializer.Deserialize<CronRunCompletedPayload>(
            capture.LastBody!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(posted);
        Assert.Equal("cj_test", posted!.JobId);
        Assert.Equal("nightly drain", posted.JobName);
        Assert.True(posted.Succeeded);
        Assert.Equal("workstation-A", posted.Machine);
        Assert.Equal("sid-1", posted.SessionId);
        Assert.Contains("sid-1", posted.SessionLink);
        Assert.Equal("https://example.com/hook", capture.LastUri?.ToString());
    }

    [Fact]
    public async Task GatewayNotifier_FailedFire_RecordsUnderJobId_WhenNoDirectorResolved()
    {
        var events = new DirectorEventLog();
        using var http = new HttpClient(new CapturingHandler());
        var notifier = new GatewayCronNotifier(events, () => "http://127.0.0.1:7879", http);

        var job = SeedJob(CronNotify.Always);
        job.Id = "cj_fail";
        var payload = new CronRunCompletedPayload
        {
            JobId = "cj_fail",
            JobName = "x",
            Succeeded = false,
            InfraStatus = "not-started",
            TaskStatus = "unknown",
            Machine = "workstation-A",
            SessionId = null,
            Reason = "no director",
            FiredUtc = Now,
        };

        await notifier.NotifyRunCompletedAsync(job, "", payload, CancellationToken.None);

        // No director resolved -> the failed run is still observable, filed under the job id.
        var ring = events.For(TenantId.Local, "cj_fail");
        Assert.Single(ring);
        Assert.Equal(DoorbellEvents.CronRunCompleted, ring[0].Event);
        Assert.Equal("not-started", ring[0].State);
    }

    [Fact]
    public async Task GatewayNotifier_NoWebhookUrl_StillRecordsRing_NoPost()
    {
        var events = new DirectorEventLog();
        var capture = new CapturingHandler();
        using var http = new HttpClient(capture);
        var notifier = new GatewayCronNotifier(events, () => "http://127.0.0.1:7879", http);

        var job = SeedJob(CronNotify.Always, webhook: null);
        job.Id = "cj_nohook";
        var payload = new CronRunCompletedPayload
        {
            JobId = "cj_nohook", JobName = "y", Succeeded = true,
            InfraStatus = "started", TaskStatus = "unknown", Machine = "m", SessionId = "sid-9",
            FiredUtc = Now,
        };

        await notifier.NotifyRunCompletedAsync(job, "director-1", payload, CancellationToken.None);

        Assert.Single(events.For(TenantId.Local, "director-1"));     // ring still gets it
        Assert.Null(capture.LastBody);               // no webhook posted
    }

    // ---- fakes ----

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; }
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }

    private sealed class OkStarter : ICronSessionStarter
    {
        private int _n;
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            _n++;
            return Task.FromResult<(string?, string?, string?)>(($"sid-{_n}", "director-1", null));
        }
    }

    private sealed class FailingStarter : ICronSessionStarter
    {
        private readonly string _reason;
        public FailingStarter(string reason) => _reason = reason;
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult<(string?, string?, string?)>((null, null, _reason));
    }

    /// <summary>Captures the last webhook POST (uri + body) and returns 200.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
