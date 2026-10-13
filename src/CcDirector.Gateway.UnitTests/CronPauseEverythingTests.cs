using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// PAUSE EVERYTHING (issue #3910). The owner's symptom: there was no way to stop the whole scheduler with one tap
/// and get back to exactly the state before. These tests drive the real switch, the real schedule store and the
/// real engine - through the HTTP routes the Cockpit calls where the route is the thing under test - and prove:
/// while paused nothing is started and no schedule is written at all; the switch survives a restart; resuming
/// leaves every schedule's own enabled flag as it was and starts no burst of late runs; and a session can read the
/// switch but never set it.
/// </summary>
public sealed class CronPauseEverythingTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId OtherAccount = new("22222222-2222-2222-2222-222222222222");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private WebApplication? _app;
    private HttpClient? _http;
    private HttpClient Http => _http ?? throw new InvalidOperationException("StartAsync has not run");

    private CronJobStore? _store;
    private CronJobStore Store => _store ??= new(Db, _h.LegacyPath("jobs.json"));
    private CronRunHistoryStore? _history;
    private CronRunHistoryStore History => _history ??= new(Db, _h.LegacyPath("runs.json"));
    private readonly RecordingStarter _starter = new();
    // No test here reads the real clock: a schedule's next run is the store's, and the clock is set from it.
    private static readonly DateTime FixedNow = new(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc);
    private readonly FakeClock _clock = new(FixedNow);

    private CronPauseSwitch Switch() => new(new TenantSettingsStore(Db));

    private CronEngine Engine(CronPauseSwitch pause) =>
        new(Store, History, _starter, new UnusedWorkListRunner(), new NullCronNotifier(), _clock, isPaused: pause.IsPaused);

    public enum Caller { OwnerBrowser, SessionKey, MachineToken }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.StopAsync();
        _h.Dispose();
    }

    private static CronJobDto Recurring(string name, bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    /// <summary>Every stored schedule, every field, as one string: two equal snapshots mean nothing was written.</summary>
    private string Snapshot() => JsonSerializer.Serialize(Store.ListAll(), Web);

    private Dictionary<string, bool> EnabledFlags() => Store.ListAll().ToDictionary(j => j.Id, j => j.Enabled);

    /// <summary>Two switched-on schedules and one paused one, with the clock set after the next midnight so the two are due.</summary>
    private (CronJobDto A, CronJobDto B, CronJobDto Off) SeedThreeWithTwoDue()
    {
        var a = Store.Create(Recurring("a"));
        var b = Store.Create(Recurring("b"));
        var off = Store.Create(Recurring("off", enabled: false));
        _clock.UtcNow = a.NextRunUtc!.Value.AddMinutes(1);
        return (a, b, off);
    }

    private async Task StartAsync(Caller caller, CronPauseSwitch pause)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use(async (ctx, next) =>
        {
            if (caller == Caller.SessionKey)
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse("11111111-2222-4333-8444-555555555555"), TenantId.Local, "dir-1");
            if (caller == Caller.OwnerBrowser)
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
            await next();
        });
        CronPauseEndpoint.Map(app, pause, Store, resolveTenant: _ => TenantId.Local, nowUtc: () => _clock.UtcNow);
        CronRunEndpoints.Map(app, Engine(pause),
            new CronRunRecordReader(History, _ => new Dictionary<string, SessionEndingFact>()),
            jobById: id => Store.Get(id),
            sessionFactoryOf: _ => SessionFactoryLookup.NotKnown);
        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
    }

    private async Task<CronPauseDto> PutAsync(bool paused)
    {
        var response = await Http.PutAsJsonAsync("cron/pause", new { paused }, Web);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CronPauseDto>(Web))!;
    }

    // ---- the symptom: one tap stops the machine, one tap puts it back exactly as it was -----------------------

    [Fact]
    public async Task PauseEverything_ThenResume_StartsNothing_WritesNoSchedule_AndLeavesEveryEnabledFlagAsItWas()
    {
        var pause = Switch();
        var (a, b, off) = SeedThreeWithTwoDue();
        var flagsBefore = EnabledFlags();
        var storedBefore = Snapshot();
        await StartAsync(Caller.OwnerBrowser, pause);
        var engine = Engine(pause);

        var paused = await PutAsync(true);

        Assert.True(paused.Paused);
        Assert.Equal(CronPauseSwitch.PausedBanner, paused.Banner);
        Assert.Equal(CronPauseSwitch.ResumeLabel, paused.ActionLabel);

        // Two schedules are due. The sweep comes round three times and starts neither.
        for (var i = 0; i < 3; i++)
            Assert.Empty(await engine.EvaluateDueAsync(CancellationToken.None));
        Assert.Equal(0, _starter.StartCount);
        // Not one field of one schedule was written while paused: not the flag, not the next run, not the status.
        Assert.Equal(storedBefore, Snapshot());

        var resumed = await PutAsync(false);

        Assert.False(resumed.Paused);
        Assert.Null(resumed.Banner);
        Assert.Equal(CronPauseSwitch.PauseLabel, resumed.ActionLabel);
        // Exactly the state before: a and b switched on, the paused one still paused.
        Assert.Equal(flagsBefore, EnabledFlags());
        Assert.True(flagsBefore[a.Id]);
        Assert.True(flagsBefore[b.Id]);
        Assert.False(flagsBefore[off.Id]);
        // The runs missed while paused are not started late in a burst: each is moved on to its next run.
        Assert.Empty(await engine.EvaluateDueAsync(CancellationToken.None));
        Assert.Equal(0, _starter.StartCount);
        Assert.All(new[] { a.Id, b.Id }, id => Assert.True(Store.Get(id)!.NextRunUtc > _clock.UtcNow));
        // And nothing else about them changed: no fire and no status was recorded for a run that never happened.
        Assert.All(Store.ListAll(), j => { Assert.Null(j.LastFiredUtc); Assert.Null(j.LastStatus); });
        // The schedule that was paused on its own was not written by either direction.
        Assert.Equal(JsonSerializer.Serialize(off, Web), JsonSerializer.Serialize(Store.Get(off.Id), Web));

        // Resumed for real: when the next run comes due it starts.
        _clock.UtcNow = Store.Get(a.Id)!.NextRunUtc!.Value.AddSeconds(1);
        Assert.Equal(2, (await engine.EvaluateDueAsync(CancellationToken.None)).Count);
        Assert.Equal(2, _starter.StartCount);
    }

    [Fact]
    public async Task NotPaused_TheSameDueSchedulesDoStart()
    {
        // The control for the test above: without the switch the two due schedules start, so "nothing started"
        // there is the switch and not a fixture that could never fire.
        var pause = Switch();
        SeedThreeWithTwoDue();

        var fired = await Engine(pause).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(2, fired.Count);
        Assert.Equal(2, _starter.StartCount);
    }

    [Fact]
    public async Task Paused_SurvivesAGatewayRestart()
    {
        SeedThreeWithTwoDue();
        var since = new DateTime(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc);
        Switch().Pause(TenantId.Local, since);

        // A restart: a new switch and a new engine over the same database.
        var afterRestart = Switch();

        Assert.True(afterRestart.IsPaused(TenantId.Local));
        Assert.Equal(since, afterRestart.PausedSinceUtc(TenantId.Local));
        Assert.Empty(await Engine(afterRestart).EvaluateDueAsync(CancellationToken.None));
        Assert.Equal(0, _starter.StartCount);
    }

    [Fact]
    public async Task Paused_RunNowStartsNothing_AndSaysWhy()
    {
        var pause = Switch();
        var (a, _, _) = SeedThreeWithTwoDue();
        await StartAsync(Caller.OwnerBrowser, pause);
        await PutAsync(true);
        var storedBefore = Snapshot();

        var response = await Http.PostAsync($"cron/jobs/{a.Id}/run", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(CronRunEndpoints.PausedRefusal,
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(0, _starter.StartCount);
        Assert.Equal(storedBefore, Snapshot());

        await PutAsync(false);
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync($"cron/jobs/{a.Id}/run", null)).StatusCode);
        Assert.Equal(1, _starter.StartCount);
    }

    // ---- a paused fleet never looks like a broken one ---------------------------------------------------------

    /// <summary>A schedule whose next run is already past, as it is found after the Gateway has been down.</summary>
    private CronJobDto SeedOneAlreadyPastDue()
    {
        var created = Store.Create(Recurring("a"));
        using var ctx = Db.CreateContext();
        ctx.CronJobs.Single(e => e.Id == created.Id).NextRunUtc = FixedNow.AddDays(-2);
        ctx.SaveChanges();
        return created;
    }

    [Fact]
    public void ARestartWhileEverythingIsPaused_RecordsNoMissedRun_AndStillMovesTheScheduleOn()
    {
        var job = SeedOneAlreadyPastDue();
        Switch().Pause(TenantId.Local, FixedNow.AddDays(-3));

        // The Gateway starts again: the store loads.
        var reloaded = new CronJobStore(Db, _h.LegacyPath("jobs.json"));

        // The runs that fell due during the pause are not problems - they were never going to start.
        Assert.Empty(History.List(job.Id));
        var after = reloaded.Get(job.Id)!;
        Assert.True(after.NextRunUtc > FixedNow);
        Assert.True(after.Enabled);
    }

    [Fact]
    public void ARestartWhenNothingIsPaused_StillRecordsTheRunsItMissed()
    {
        // The control for the test above: without the pause the same load records the missed runs, so "none recorded"
        // there is the switch and not a fixture the load would never have recorded for.
        var job = SeedOneAlreadyPastDue();

        _ = new CronJobStore(Db, _h.LegacyPath("jobs.json"));

        Assert.NotEmpty(History.List(job.Id));
        Assert.All(History.List(job.Id), r => Assert.Equal(CronRunProblems.DidNotRun, r.Problem));
    }

    [Fact]
    public async Task ResumingEverything_RecordsNoMissedRun_ForTheTimesThatPassedWhilePaused()
    {
        var pause = Switch();
        var (a, b, _) = SeedThreeWithTwoDue();
        await StartAsync(Caller.OwnerBrowser, pause);
        await PutAsync(true);
        // Three days pass while everything is paused.
        _clock.UtcNow = _clock.UtcNow.AddDays(3);

        await PutAsync(false);
        await Engine(pause).EvaluateDueAsync(CancellationToken.None);

        Assert.Empty(History.List(a.Id));
        Assert.Empty(History.List(b.Id));
        Assert.Equal(0, _starter.StartCount);
    }

    // ---- the switch itself --------------------------------------------------------------------------------------

    [Fact]
    public void Switch_IsOffUntilPaused_AndPausingTwiceKeepsWhenItBegan()
    {
        var pause = Switch();
        Assert.False(pause.IsPaused(TenantId.Local));
        Assert.False(pause.Describe(TenantId.Local).Paused);
        var first = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        pause.Pause(TenantId.Local, first);
        pause.Pause(TenantId.Local, first.AddHours(3));

        Assert.Equal(first, pause.PausedSinceUtc(TenantId.Local));
        Assert.Equal(first, pause.Describe(TenantId.Local).SinceUtc);
        Assert.True(pause.Resume(TenantId.Local));
        Assert.False(pause.IsPaused(TenantId.Local));
        // Resuming what is not paused is not an error and reports that nothing was paused.
        Assert.False(pause.Resume(TenantId.Local));
    }

    [Fact]
    public void Switch_IsPerAccount()
    {
        var pause = Switch();

        pause.Pause(TenantId.Local, FixedNow);

        Assert.True(pause.IsPaused(TenantId.Local));
        Assert.False(pause.IsPaused(OtherAccount));
    }

    [Fact]
    public void Switch_AStoredValueThatIsNotATime_IsAnError_NeverAQuietNotPaused()
    {
        new TenantSettingsStore(Db).Set(TenantId.Local, TenantSettingKeys.SchedulesPaused, "yes", FixedNow);

        Assert.Throws<InvalidOperationException>(() => Switch().IsPaused(TenantId.Local));
    }

    [Fact]
    public void TenantSettingKeys_SchedulesPaused_IsAStorableKey()
        => Assert.Contains(TenantSettingKeys.SchedulesPaused, TenantSettingKeys.All);

    // ---- moving missed runs on --------------------------------------------------------------------------------

    [Fact]
    public void MoveMissedRunsOn_MovesOnlySwitchedOnRepeatingSchedulesWhoseRunIsPast()
    {
        var (a, b, off) = SeedThreeWithTwoDue();
        var oneOffAt = _clock.UtcNow.AddMinutes(-30);
        var oneOff = Store.Create(new CronJobDto
        {
            Name = "once",
            Enabled = true,
            ScheduleKind = CronSchedule.KindOneOff,
            RunAt = oneOffAt.ToString("yyyy-MM-ddTHH:mm:ss"),
            TimeZoneId = "UTC",
            Target = new CronJobTarget { Machine = "workstation-A" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        });
        var flagsBefore = EnabledFlags();
        var offBefore = JsonSerializer.Serialize(Store.Get(off.Id), Web);
        var oneOffBefore = JsonSerializer.Serialize(Store.Get(oneOff.Id), Web);

        var moved = Store.MoveMissedRunsOn(TenantId.Local, _clock.UtcNow);

        Assert.Equal(2, moved);
        Assert.All(new[] { a.Id, b.Id }, id => Assert.True(Store.Get(id)!.NextRunUtc > _clock.UtcNow));
        Assert.Equal(flagsBefore, EnabledFlags());
        // A paused schedule and a one-off are not written: a one-off keeps its moment, as it does across a restart.
        Assert.Equal(offBefore, JsonSerializer.Serialize(Store.Get(off.Id), Web));
        Assert.Equal(oneOffBefore, JsonSerializer.Serialize(Store.Get(oneOff.Id), Web));
    }

    [Fact]
    public void MoveMissedRunsOn_NothingMissed_WritesNothing()
    {
        var a = Store.Create(Recurring("a"));
        var before = Snapshot();

        // A minute before its next run: nothing has been missed.
        Assert.Equal(0, Store.MoveMissedRunsOn(TenantId.Local, a.NextRunUtc!.Value.AddMinutes(-1)));
        Assert.Equal(before, Snapshot());
    }

    // ---- a person's control only ------------------------------------------------------------------------------

    [Fact]
    public async Task ASession_MayReadTheSwitch_AndMayNotSetIt()
    {
        var pause = Switch();
        await StartAsync(Caller.SessionKey, pause);

        var set = await Http.PutAsJsonAsync("cron/pause", new { paused = true }, Web);

        Assert.Equal(HttpStatusCode.Forbidden, set.StatusCode);
        Assert.Equal(CronPauseEndpoint.SessionRefusal,
            (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(pause.IsPaused(TenantId.Local));

        // And it may not resume what the owner paused.
        pause.Pause(TenantId.Local, FixedNow);
        var resume = await Http.PutAsJsonAsync("cron/pause", new { paused = false }, Web);
        Assert.Equal(HttpStatusCode.Forbidden, resume.StatusCode);
        Assert.True(pause.IsPaused(TenantId.Local));

        var read = await Http.GetFromJsonAsync<CronPauseDto>("cron/pause", Web);
        Assert.True(read!.Paused);
    }

    [Fact]
    public async Task TheMachineToken_MaySetIt_LikeAnyFactorySchedule()
    {
        var pause = Switch();
        await StartAsync(Caller.MachineToken, pause);

        Assert.True((await PutAsync(true)).Paused);
        Assert.True(pause.IsPaused(TenantId.Local));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"paused\":null}")]
    public async Task Put_WithoutSayingWhich_IsRefused_AndChangesNothing(string body)
    {
        var pause = Switch();
        await StartAsync(Caller.OwnerBrowser, pause);

        var response = await Http.PutAsync("cron/pause",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(pause.IsPaused(TenantId.Local));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("PUT", false)]
    [InlineData("POST", false)]
    [InlineData("DELETE", false)]
    public void SessionKeyGuard_LetsASessionReadTheSwitch_AndNothingElse(string verb, bool allowed)
        => Assert.Equal(allowed, SessionKeyGuard.Check(verb, "/cron/pause").Allowed);

    // ---- fakes --------------------------------------------------------------------------------------------------

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; set; }
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

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }
}
