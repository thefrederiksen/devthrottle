using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A FAILED SCHEDULE TELLS ITS OWNER (issue #4436), read from the run history exactly as the Gateway records failures:
/// a new schedule notifies without any setting changed, a missed run notifies too, every channel carries the plain
/// sentence with the raw reason as the detail, and a schedule failing every five minutes sends at most one notice an
/// hour for each cause - which a restart does not reset.
/// </summary>
public sealed class ScheduleFailureNotifierTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly CronRunHistoryStore _runs;
    private readonly Dictionary<string, CronJobDto> _jobs = new(StringComparer.Ordinal);
    private readonly List<CronRunCompletedPayload> _fleet = new();
    private readonly List<CronRunCompletedPayload> _pushed = new();
    private readonly List<(string Subject, string Body)> _emailed = new();
    private DateTime _now = new(2026, 10, 24, 6, 0, 0, DateTimeKind.Utc);

    public ScheduleFailureNotifierTests()
    {
        _db = _h.Open();
        _runs = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
    }

    public void Dispose() => _h.Dispose();

    private ScheduleFailureNotifier NewNotifier() => new(_runs, new CronFailureNoticeStore(_db),
        jobOf: (_, id) => _jobs.TryGetValue(id, out var job) ? job : null,
        machineOsOf: (_, _) => DirectorOs.MacOs,
        fleet: new FleetChannel(_fleet),
        push: (_, notice, _) => { _pushed.Add(notice); return Task.CompletedTask; },
        mail: (_, subject, body, _) => { _emailed.Add((subject, body)); return Task.FromResult<string?>(null); },
        enterScope: _ => new NoScope(),
        clock: () => _now);

    // A schedule as a person makes one, without touching its notifications.
    private CronJobDto NewSchedule(string id = "cj_morning", string name = "Morning report")
    {
        var job = new CronJobDto { Id = id, Name = name, Target = new CronJobTarget { Machine = "studio-mac" } };
        _jobs[id] = job;
        return job;
    }

    private CronRunRecord Fail(string jobId, string reason, string problem = CronRunProblems.DidNotRun)
    {
        var run = new CronRunRecord
        {
            ScheduledUtc = _now, FiredUtc = _now, Machine = "studio-mac", TargetDirectorId = "",
            InfraStatus = "not-started", TaskStatus = "unknown",
            Result = CronRunResults.Problem, Problem = problem, ResultReason = reason, ResultUtc = _now,
        };
        _runs.Append(jobId, run);
        return run;
    }

    private const string MissingFolder = "it did not start a session: director returned BadRequest: repoPath does not exist: /Users/you/site";
    private const string MachineOff = "it did not start a session: no Director on 'studio-mac' and the launcher could not start one";

    [Fact]
    public async Task ANewScheduleWhoseRunFails_TellsItsOwner_OnEveryChannel_WithoutAnySettingChanged()
    {
        var job = NewSchedule();
        Assert.Equal(CronNotify.Failure, job.NotifyOn);
        var notifier = NewNotifier();
        Fail(job.Id, MissingFolder);
        _now = _now.AddSeconds(30);

        Assert.Equal(1, await notifier.ScanAsync(CancellationToken.None));

        var expected = "The folder '/Users/you/site' does not exist on studio-mac (a Mac). Choose a folder that exists on studio-mac. Then edit the schedule.";
        var told = Assert.Single(_fleet);
        Assert.Equal(expected, told.Reason);
        Assert.Equal(MissingFolder, told.Detail);
        Assert.Equal(ScheduleFailureCauses.FolderMissing, told.Cause);
        Assert.False(told.Succeeded);
        Assert.Equal(expected, Assert.Single(_pushed).Reason);
        var (subject, body) = Assert.Single(_emailed);
        Assert.Equal("Schedule failed: Morning report", subject);
        Assert.StartsWith("Your schedule \"Morning report\" failed on studio-mac.\n\n" + expected, body);
        Assert.Contains("Details: " + MissingFolder, body);
    }

    [Fact]
    public async Task ABrokenScheduleFiringEveryFiveMinutes_SendsAtMostOneNoticeAnHour_ThenOneCountingTheRest()
    {
        var job = NewSchedule();
        var notifier = NewNotifier();

        // Twelve failures, five minutes apart: 06:00 to 06:55. The scan runs after each, as the timer would.
        for (var i = 0; i < 12; i++)
        {
            Fail(job.Id, MissingFolder);
            _now = _now.AddSeconds(30);
            await notifier.ScanAsync(CancellationToken.None);
            _now = _now.AddMinutes(5).AddSeconds(-30);
        }
        Assert.Single(_fleet);
        Assert.Single(_pushed);
        Assert.Single(_emailed);

        // 07:00: the hour has passed, so the thirteenth failure is told, standing for itself and the eleven held back.
        Fail(job.Id, MissingFolder);
        _now = _now.AddSeconds(30);
        Assert.Equal(1, await notifier.ScanAsync(CancellationToken.None));
        Assert.Equal(2, _fleet.Count);
        Assert.Equal(12, _fleet[1].Occurrences);
        Assert.EndsWith("It has failed this way 12 times since the last notice.", ScheduleFailureNotifier.PushWords(_pushed[1]).Body);
    }

    [Fact]
    public async Task TwoCausesOfOneSchedule_AreEachTold()
    {
        var job = NewSchedule();
        var notifier = NewNotifier();
        Fail(job.Id, MissingFolder);
        Fail(job.Id, MachineOff);
        _now = _now.AddSeconds(30);

        Assert.Equal(2, await notifier.ScanAsync(CancellationToken.None));
        Assert.Equal(new[] { ScheduleFailureCauses.FolderMissing, ScheduleFailureCauses.MachineOffline },
            _fleet.Select(p => p.Cause).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMissedRun_IsTold()
    {
        // A run the Gateway was down for is recorded at its next start (CronJobStore.Load) and told like any other.
        var job = NewSchedule();
        var notifier = NewNotifier();
        Fail(job.Id, "the Gateway was not running when it was due (it started again at 2026-10-24 06:00 UTC)");
        _now = _now.AddSeconds(30);

        Assert.Equal(1, await notifier.ScanAsync(CancellationToken.None));
        Assert.Equal(ScheduleFailureCauses.GatewayWasDown, Assert.Single(_fleet).Cause);
    }

    [Fact]
    public async Task AScheduleWhoseOwnerTurnedNotificationsOff_IsNotTold()
    {
        var job = NewSchedule();
        job.NotifyOn = CronNotify.None;
        var notifier = NewNotifier();
        Fail(job.Id, MissingFolder);
        _now = _now.AddSeconds(30);

        Assert.Equal(0, await notifier.ScanAsync(CancellationToken.None));
        Assert.Empty(_fleet);
        Assert.Empty(_pushed);
        Assert.Empty(_emailed);
    }

    [Fact]
    public async Task ADeletedSchedule_IsNotTold()
    {
        var job = NewSchedule();
        var notifier = NewNotifier();
        Fail(job.Id, MissingFolder);
        _jobs.Remove(job.Id);
        _now = _now.AddSeconds(30);

        Assert.Equal(0, await notifier.ScanAsync(CancellationToken.None));
        Assert.Empty(_fleet);
    }

    [Fact]
    public async Task OneFailure_IsToldOnce_AcrossScans_AndAcrossARestart()
    {
        var job = NewSchedule();
        Fail(job.Id, MissingFolder);
        _now = _now.AddSeconds(30);
        Assert.Equal(1, await NewNotifier().ScanAsync(CancellationToken.None));

        var same = NewNotifier();
        _now = _now.AddSeconds(30);
        Assert.Equal(0, await same.ScanAsync(CancellationToken.None));
        _now = _now.AddSeconds(30);
        Assert.Equal(0, await same.ScanAsync(CancellationToken.None));

        Assert.Single(_fleet);
    }

    [Fact]
    public async Task ARunThatWentFine_IsNotTold()
    {
        var job = NewSchedule();
        _runs.Append(job.Id, new CronRunRecord
        {
            ScheduledUtc = _now, FiredUtc = _now, Machine = "studio-mac", SessionId = "sid-1", InfraStatus = "started",
            TaskStatus = "unknown", Result = CronRunResults.Ok, ResultUtc = _now,
        });
        _now = _now.AddSeconds(30);

        Assert.Equal(0, await NewNotifier().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public void Claim_InsideTheHour_HoldsBack_AfterIt_SendsWithTheCount()
    {
        var store = new CronFailureNoticeStore(_db);
        var tenant = TenantId.Local;

        Assert.Equal(new CronFailureNoticeClaim(true, 1), store.Claim(tenant, "cj_a", ScheduleFailureCauses.FolderMissing, _now));
        Assert.False(store.Claim(tenant, "cj_a", ScheduleFailureCauses.FolderMissing, _now.AddMinutes(59)).Send);
        Assert.True(store.Claim(tenant, "cj_b", ScheduleFailureCauses.FolderMissing, _now.AddMinutes(1)).Send);
        Assert.Equal(new CronFailureNoticeClaim(true, 2), store.Claim(tenant, "cj_a", ScheduleFailureCauses.FolderMissing, _now.AddMinutes(60)));
    }

    private sealed class FleetChannel : ICronNotifier
    {
        private readonly List<CronRunCompletedPayload> _told;
        public FleetChannel(List<CronRunCompletedPayload> told) => _told = told;
        public string BuildSessionLink(string? sessionId) => "";
        public Task NotifyRunCompletedAsync(CronJobDto job, string directorId, CronRunCompletedPayload payload, CancellationToken ct)
        {
            _told.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }
}
