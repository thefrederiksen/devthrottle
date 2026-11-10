using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The <c>random</c> schedule kind (issue #3622) through the store, the firing engine and the REST surface: a
/// store reload keeps the next run, the engine fires each planned time and advances to the next one without
/// disabling the job, and create, list, plan and run work over real HTTP.
/// </summary>
public sealed class RandomScheduleWiringTests : IAsyncLifetime
{
    private const string Settings = "window=07:00-01:00 perDay=4 minGap=45 shape=human";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private string _legacy = "";
    private CronJobStore _store = null!;
    private CronRunHistoryStore _history = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly SettableClock _clock = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _legacy = _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json");
        _store = new CronJobStore(_db, _legacy);
        _history = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        var runRecords = new CcDirector.Gateway.Running.CronRunRecordReader(_history, new CcDirector.Gateway.History.SessionHistoryStore(_db).EndingsOf);
        CronJobEndpoints.Map(_app, _store, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            findFactory: (_, _) => null, runRecords: runRecords, accountZoneOf: _ => TimeZoneInfo.Utc,
            isDefined: (_, _) => false,
            checkFolder: (_, _, _, _) => Task.FromResult(CcDirector.Gateway.Running.ScheduleFolderVerdict.Fine), refuseMachine: (_, _, _) => null,
            timeProvider: _clock);
        var engine = new CronEngine(_store, _history, new RecordingStarter(), new UnusedWorkListRunner(),
            new NullCronNotifier(), new FakeClock(DateTime.UtcNow));
        CronRunEndpoints.Map(_app, engine, runRecords, jobById: _store.Get,
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown, accountZoneOf: _ => TimeZoneInfo.Utc, runLifetime: CancellationToken.None);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First() + "/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
    }

    private static CronJobDto RandomJob(string name = "reply") => new()
    {
        Name = name,
        ScheduleKind = CronSchedule.KindRandom,
        CronExpression = Settings,
        TimeZoneId = "America/Toronto",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    // ---- the store -----------------------------------------------------------------------------------

    [Fact]
    public void Create_RandomJob_NextRunIsTheFirstPlannedTime_AndAReloadKeepsItIdentical()
    {
        var created = _store.Create(RandomJob());

        Assert.NotNull(created.NextRunUtc);
        Assert.Equal(CronSchedule.ComputeNextRunUtc(created, DateTime.UtcNow), created.NextRunUtc);

        var reloaded = new CronJobStore(_h.Open(), _legacy).Get(created.Id);   // a fresh store recomputes on load

        Assert.NotNull(reloaded);
        Assert.Equal(created.NextRunUtc, reloaded.NextRunUtc);
        Assert.Equal(CronSchedule.KindRandom, reloaded.ScheduleKind);
        Assert.Equal(Settings, reloaded.CronExpression);
    }

    // ---- the engine ----------------------------------------------------------------------------------

    [Fact]
    public async Task EvaluateDue_RandomJob_FiresEachPlannedTime_AndAdvancesToTheNext_NeverDisabling()
    {
        var created = _store.Create(RandomJob());
        var clock = new FakeClock(created.NextRunUtc!.Value);
        var starter = new RecordingStarter();
        var engine = new CronEngine(_store, _history, starter, new UnusedWorkListRunner(), new NullCronNotifier(), clock);

        var planned = new List<DateTime>();
        var at = created.NextRunUtc.Value.AddSeconds(-1);
        for (var i = 0; i < 8; i++)
        {
            at = CronSchedule.ComputeNextRunUtc(created, at)!.Value;
            planned.Add(at);
        }

        foreach (var due in planned)
        {
            clock.UtcNow = due.AddSeconds(20);   // the engine ticks once a minute
            var fired = await engine.EvaluateDueAsync(CancellationToken.None);

            var record = Assert.Single(fired);
            Assert.Equal(due, record.ScheduledUtc);
            Assert.Equal("started", record.InfraStatus);
            var after = _store.Get(created.Id)!;
            Assert.True(after.Enabled);                                         // never the one-off branch
            Assert.Equal(CronSchedule.ComputeNextRunUtc(after, clock.UtcNow), after.NextRunUtc);

            Assert.Empty(await engine.EvaluateDueAsync(CancellationToken.None)); // nothing fires twice
        }

        Assert.Equal(planned.Count, starter.StartCount);
        Assert.Equal(planned.Count, _history.List(created.Id).Count);
    }

    // ---- the REST surface ----------------------------------------------------------------------------

    [Fact]
    public async Task Rest_CreateListPlanAndRun_RandomJob()
    {
        var body = new
        {
            name = "Reddit Autonomy - random schedule proof",
            scheduleKind = "random",
            cronExpression = Settings,
            timeZoneId = "America/Toronto",
            target = new { machine = "workstation-A" },
            action = new { repoPath = @"D:\repo", seed = "/help" },
        };
        var createResp = await _http.PostAsJsonAsync("cron/jobs", body);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = JsonSerializer.Deserialize<CronJobDto>(await createResp.Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.Equal("random", created.ScheduleKind);
        Assert.NotNull(created.NextRunUtc);

        Assert.Equal("About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)", created.ScheduleText);
        Assert.NotNull(created.RemainingToday);

        var listJson = await _http.GetStringAsync("cron/jobs?include=random");
        Assert.Contains(created.Id, listJson);
        Assert.Contains("About 4 times a day at random", listJson);

        var planResp = await _http.GetAsync($"cron/jobs/{created.Id}/plan?days=3");
        Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);
        var plan = JsonSerializer.Deserialize<CronPlanDto>(await planResp.Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.Equal(3, plan.Days);
        Assert.Equal("America/Toronto", plan.TimeZoneId);
        Assert.NotEmpty(plan.Fires);
        Assert.Equal(created.NextRunUtc, plan.Fires[0].Utc);           // the plan and the engine agree
        Assert.True(plan.Fires.Zip(plan.Fires.Skip(1)).All(p => (p.Second.Utc - p.First.Utc).TotalMinutes >= 45));

        var runResp = await _http.PostAsync($"cron/jobs/{created.Id}/run", null);
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);
        var run = JsonSerializer.Deserialize<CronRunRecord>(await runResp.Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.False(string.IsNullOrEmpty(run.SessionId));
    }

    [Fact]
    public async Task Rest_Load_ForecastsTheMachineOfARandomJob_OverTwentyFourHours()
    {
        // A random schedule starts sessions like any other, so the load strip counts it.
        //
        // The clock is the test's own, at 06:30 in Toronto. A random job plans at least one fire in every day's window,
        // and from 06:30 the next 24 hours (06:00 to 06:00) hold the whole of that day's 07:00 to 01:00 window, so at
        // least one start is certain whatever id the store gives the job. This test used to read the real clock, and
        // the claim is NOT true at every time of day: at 14:00 the forecast holds only the afternoon of today's window
        // and the morning of tomorrow's, and a job whose fires all fall in the other halves has none in it. That is
        // what failed it on the Windows build machine at about 14:00 Toronto time on 10 November 2026. The forecast
        // was right; the test asked it something that is only sometimes true.
        _clock.Fixed = TorontoTime(new DateTime(2026, 11, 10, 6, 30, 0));
        var created = _store.Create(RandomJob("load"));

        var resp = await _http.GetAsync("cron/load");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var load = JsonSerializer.Deserialize<CronLoadDto>(await resp.Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.Equal(CronLoad.DefaultCapacity, load.Capacity);
        var machine = Assert.Single(load.Machines);
        Assert.Equal("workstation-A", machine.Machine);
        Assert.Equal("America/Toronto", machine.TimeZoneId);
        Assert.Equal(24, machine.Hours.Count);
        Assert.True(machine.Hours.Sum(h => h.Starts) >= 1);
        // It has never run, so its length is a guess, and the forecast says so.
        Assert.Equal(new[] { created.Id }, machine.EstimatedJobIds);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(6, 30)]
    [InlineData(9, 0)]
    [InlineData(14, 0)]
    [InlineData(14, 30)]
    [InlineData(19, 45)]
    [InlineData(23, 59)]
    public async Task Rest_Load_CountsEachPlannedStartOfARandomJob_InTheHourItFalls(int hour, int minute)
    {
        // At any time of day the strip's starts are exactly the job's planned fires, each in the hour it falls in: no
        // fire is lost and none is invented. The plan is read from the schedule itself (CronSchedule.RandomPlan), not
        // from the forecast, so this is two answers agreeing, and it holds for whatever id the store gives the job.
        var nowUtc = TorontoTime(new DateTime(2026, 11, 10, hour, minute, 0));
        _clock.Fixed = nowUtc;
        var created = _store.Create(RandomJob("load-" + hour + "-" + minute));

        var load = JsonSerializer.Deserialize<CronLoadDto>(await _http.GetStringAsync("cron/load"), JsonOpts)!;

        var machine = Assert.Single(load.Machines);
        Assert.Equal(24, machine.Hours.Count);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, Toronto));
        var planned = CronSchedule.RandomPlan(created, today.AddDays(-1), today.AddDays(1)).Select(p => p.Utc).ToList();
        foreach (var bar in machine.Hours)
        {
            var expected = planned.Count(fire => fire >= bar.StartUtc && fire < bar.StartUtc.AddHours(1));
            Assert.True(expected == bar.Starts,
                $"the hour from {bar.Label} shows {bar.Starts} starts and the plan has {expected} fires in it");
        }
        Assert.Equal(planned.Count(fire => fire >= machine.Hours[0].StartUtc && fire < machine.Hours[0].StartUtc.AddHours(24)),
            machine.Hours.Sum(h => h.Starts));
    }

    [Fact]
    public async Task Rest_Create_AScheduleThatOverfillsAnHour_IsSavedWithALoadWarning_AndOneThatFitsHasNone()
    {
        // Three hours ahead of now on the schedules' own clock, so every run is inside the forecast whatever time the
        // test runs at, and none has already started.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        var hour = (TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Hour + 3) % 24;
        var label = $"{hour:00}:00";
        CronJobDto Seven(int i) => new()
        {
            Name = $"seven-{i}",
            ScheduleKind = "recurring",
            CronExpression = $"0 {hour} * * *",
            TimeZoneId = "America/Toronto",
            Target = new CronJobTarget { Machine = "busy-box" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        };
        for (var i = 0; i < CronLoad.DefaultCapacity; i++)
        {
            var fits = await _http.PostAsJsonAsync("cron/jobs", Seven(i));
            var fitted = JsonSerializer.Deserialize<CronJobDto>(await fits.Content.ReadAsStringAsync(), JsonOpts)!;
            Assert.Null(fitted.LoadWarning);
        }

        var resp = await _http.PostAsJsonAsync("cron/jobs", Seven(99));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = JsonSerializer.Deserialize<CronJobDto>(await resp.Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.NotNull(_store.Get(created.Id));
        Assert.StartsWith($"busy-box will have {CronLoad.DefaultCapacity + 1} scheduled sessions open at once while this one runs from {label}",
            created.LoadWarning);

        // An update through PUT - here switching it off and on again, as `schedule disable` and `enable` do - is
        // warned on the same terms: off starts nothing, on lands back in the crowd.
        created.Enabled = false;
        var off = JsonSerializer.Deserialize<CronJobDto>(
            await (await _http.PutAsJsonAsync($"cron/jobs/{created.Id}", created)).Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.Null(off.LoadWarning);
        created.Enabled = true;
        var on = JsonSerializer.Deserialize<CronJobDto>(
            await (await _http.PutAsJsonAsync($"cron/jobs/{created.Id}", created)).Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.StartsWith("busy-box will have", on.LoadWarning);
    }

    [Fact]
    public async Task Rest_List_WithoutTheOptIn_LeavesRandomJobsOut_AndStillParses()
    {
        var random = _store.Create(RandomJob());
        var recurring = RandomJob("nightly");
        recurring.ScheduleKind = CronSchedule.KindRecurring;
        recurring.CronExpression = "0 0 * * *";
        var cron = _store.Create(recurring);

        // A command-line tool released before the random kind calls the plain route and must see what it always did.
        var plain = await ListAsync("cron/jobs");
        Assert.Equal(new[] { cron.Id }, plain.Select(j => j.Id));
        Assert.All(plain, j => Assert.Contains(j.ScheduleKind, new[] { "recurring", "oneOff" }));

        // An opt-in naming some other word is no opt-in.
        Assert.Equal(new[] { cron.Id }, (await ListAsync("cron/jobs?include=everything")).Select(j => j.Id));
    }

    [Fact]
    public async Task Rest_List_WithTheOptIn_IncludesRandomJobs()
    {
        var random = _store.Create(RandomJob());
        var recurring = RandomJob("nightly");
        recurring.ScheduleKind = CronSchedule.KindRecurring;
        recurring.CronExpression = "0 0 * * *";
        var cron = _store.Create(recurring);

        foreach (var url in new[] { "cron/jobs?include=random", "cron/jobs?include=other,Random" })
        {
            var listed = await ListAsync(url);
            Assert.Equal(new[] { cron.Id, random.Id }.OrderBy(i => i), listed.Select(j => j.Id).OrderBy(i => i));
            Assert.Equal("About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)",
                listed.Single(j => j.Id == random.Id).ScheduleText);
        }

        // A direct read of a random job needs no opt-in.
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"cron/jobs/{random.Id}")).StatusCode);
    }

    private async Task<List<CronJobDto>> ListAsync(string url)
    {
        var resp = await _http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<List<CronJobDto>>(doc.RootElement.GetProperty("jobs").GetRawText(), JsonOpts)!;
    }

    [Fact]
    public async Task Rest_Plan_RefusesARecurringJob_AndABadDayCount()
    {
        var random = _store.Create(RandomJob());
        var recurring = RandomJob("nightly");
        recurring.ScheduleKind = CronSchedule.KindRecurring;
        recurring.CronExpression = "0 0 * * *";
        var cron = _store.Create(recurring);

        var notRandom = await _http.GetAsync($"cron/jobs/{cron.Id}/plan");
        Assert.Equal(HttpStatusCode.BadRequest, notRandom.StatusCode);
        Assert.Contains("only for a random schedule", await notRandom.Content.ReadAsStringAsync());

        foreach (var days in new[] { "0", "15", "two" })
        {
            var bad = await _http.GetAsync($"cron/jobs/{random.Id}/plan?days={days}");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("days must be a whole number from 1 to 14", await bad.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("cron/jobs/cj_missing/plan")).StatusCode);
    }

    [Fact]
    public async Task Rest_Create_BadRandomSettings_Is400WithTheReason()
    {
        var resp = await _http.PostAsJsonAsync("cron/jobs", new
        {
            name = "too tight",
            scheduleKind = "random",
            cronExpression = "window=07:00-09:00 perDay=4 minGap=45",
            timeZoneId = "America/Toronto",
            target = new { machine = "workstation-A" },
            action = new { repoPath = @"D:\repo", seed = "/help" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("too short for 8 fires", await resp.Content.ReadAsStringAsync());
    }

    // ---- fakes ---------------------------------------------------------------------------------------

    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    private static DateTimeOffset TorontoTime(DateTime local) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Toronto));

    /// <summary>The clock the REST routes read. Unset, it is the real clock, which every other test here was written against.</summary>
    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset? Fixed { get; set; }
        public override DateTimeOffset GetUtcNow() => Fixed ?? DateTimeOffset.UtcNow;
    }

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
