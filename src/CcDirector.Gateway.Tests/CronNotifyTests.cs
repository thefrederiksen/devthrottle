using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Events;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the per-job run-complete notification (issue #622): the firing engine notifies on
/// success and on failure when the job opts in, honors the always-vs-failure policy, stays silent by
/// default, and the production notifier rides the existing fleet event ring AND posts the optional
/// webhook with the same payload. Covers AC1, AC2, AC3, AC5, and the AC4 default-OFF guard.
/// </summary>
public sealed class CronNotifyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-cronnotify-tests-" + Guid.NewGuid().ToString("N"));

    private CronJobStore NewJobStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));
    private CronRunHistoryStore NewHistory() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".runs.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CronEngine Engine(CronJobStore store, CronRunHistoryStore history, ICronSessionStarter starter, ICronNotifier notifier) =>
        new(store, history, starter, new UnusedWorkListRunner(), notifier, new FakeClock(DateTime.UtcNow));

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

    // ---- AC2: a job whose fire FAILS still notifies, with the failure reason ----

    [Fact]
    public async Task NotifyAlways_FailedFire_Notifies_WithFailureReason()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var notifier = new RecordingCronNotifier();
        var created = store.Create(SeedJob(CronNotify.Always));
        var engine = Engine(store, history, new FailingStarter("target director not registered"), notifier);

        var result = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);       // a run is still recorded
        Assert.Equal(1, notifier.CallCount);                       // AC2: a failed fire notifies
        var p = notifier.Last!;
        Assert.False(p.Succeeded);
        Assert.Equal("not-started", p.InfraStatus);
        Assert.Null(p.SessionId);
        Assert.Equal("", p.SessionLink);                           // no session -> no link
        Assert.Equal("target director not registered", p.Reason);
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

        // failure under "failure" -> notifies.
        var notifierB = new RecordingCronNotifier();
        var failJob = store.Create(SeedJob(CronNotify.Failure));
        await Engine(store, history, new FailingStarter("boom"), notifierB).RunNowAsync(failJob.Id, CancellationToken.None);
        Assert.Equal(1, notifierB.CallCount);                      // AC3: failure notifies
        Assert.False(notifierB.Last!.Succeeded);
    }

    // ---- AC4 (default-OFF guard): a job with no notify policy never notifies ----

    [Fact]
    public async Task NotifyNone_NeitherSuccessNorFailure_Notifies()
    {
        var store = NewJobStore();
        var history = NewHistory();

        var notifierOk = new RecordingCronNotifier();
        var okJob = store.Create(SeedJob(CronNotify.None));
        await Engine(store, history, new OkStarter(), notifierOk).RunNowAsync(okJob.Id, CancellationToken.None);
        Assert.Equal(0, notifierOk.CallCount);                     // default OFF: silent on success

        var notifierFail = new RecordingCronNotifier();
        var failJob = store.Create(SeedJob(CronNotify.None));
        await Engine(store, history, new FailingStarter("x"), notifierFail).RunNowAsync(failJob.Id, CancellationToken.None);
        Assert.Equal(0, notifierFail.CallCount);                   // default OFF: silent on failure too
    }

    [Fact]
    public async Task UnsetNotifyOn_DefaultsToNone_StaysSilent()
    {
        var store = NewJobStore();
        var history = NewHistory();
        var notifier = new RecordingCronNotifier();
        // A job created without ever touching NotifyOn defaults to "none" on the DTO.
        var job = SeedJob(CronNotify.Always);
        job.NotifyOn = "";                                          // simulate an old/silent job
        var created = store.Create(job);
        await Engine(store, history, new OkStarter(), notifier).RunNowAsync(created.Id, CancellationToken.None);
        Assert.Equal(0, notifier.CallCount);
    }

    // ---- CronNotify policy helper ----

    [Theory]
    [InlineData(CronNotify.None, true, false)]
    [InlineData(CronNotify.None, false, false)]
    [InlineData(CronNotify.Always, true, true)]
    [InlineData(CronNotify.Always, false, true)]
    [InlineData(CronNotify.Failure, true, false)]
    [InlineData(CronNotify.Failure, false, true)]
    [InlineData("", true, false)]                                  // unset -> none
    [InlineData("BOGUS", false, false)]                            // unknown -> none (no notify)
    public void ShouldNotify_HonorsPolicy(string policy, bool succeeded, bool expected)
    {
        Assert.Equal(expected, CronNotify.ShouldNotify(policy, succeeded));
    }

    [Theory]
    [InlineData("always", "always")]
    [InlineData("FAILURE", "failure")]
    [InlineData("  None  ", "none")]
    [InlineData("", "none")]
    [InlineData(null, "none")]
    [InlineData("nonsense", "none")]
    public void Normalize_MapsToCanonicalOrNone(string? input, string expected)
    {
        Assert.Equal(expected, CronNotify.Normalize(input));
    }

    // ---- AC5: the production notifier rides the existing event ring AND posts the webhook ----

    [Fact]
    public async Task GatewayNotifier_RecordsOntoExistingEventRing_AndPostsWebhook_SamePayload()
    {
        var events = new DirectorEventLog();
        var capture = new CapturingHandler();
        using var http = new HttpClient(capture);
        var notifier = new GatewayCronNotifier(
            events,
            directorId => directorId == "director-1" ? "https://host.ts.net" : null,
            "http://127.0.0.1:7879",
            http);

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
            SessionLink = notifier.BuildSessionLink("director-1", "sid-1"),
            FiredUtc = DateTime.UtcNow,
        };

        await notifier.NotifyRunCompletedAsync(job, "director-1", payload, CancellationToken.None);

        // Leg 1: it landed on the EXISTING per-Director event ring under the resolved director, with
        // the cron-run-completed event name (the fleet channel, not a new mechanism). AC5.
        var ring = events.For("director-1");
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
        var notifier = new GatewayCronNotifier(events, _ => null, "http://127.0.0.1:7879", http);

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
            FiredUtc = DateTime.UtcNow,
        };

        await notifier.NotifyRunCompletedAsync(job, "", payload, CancellationToken.None);

        // No director resolved -> the failed run is still observable, filed under the job id.
        var ring = events.For("cj_fail");
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
        var notifier = new GatewayCronNotifier(events, _ => "https://host.ts.net", "http://127.0.0.1:7879", http);

        var job = SeedJob(CronNotify.Always, webhook: null);
        job.Id = "cj_nohook";
        var payload = new CronRunCompletedPayload
        {
            JobId = "cj_nohook", JobName = "y", Succeeded = true,
            InfraStatus = "started", TaskStatus = "unknown", Machine = "m", SessionId = "sid-9",
            FiredUtc = DateTime.UtcNow,
        };

        await notifier.NotifyRunCompletedAsync(job, "director-1", payload, CancellationToken.None);

        Assert.Single(events.For("director-1"));     // ring still gets it
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
