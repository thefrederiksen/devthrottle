using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// The shifts reach the schedule screen over real HTTP (Factory Control, step 3): every schedule read and write
/// carries its shifts and its shift-end warning, and every run carries the shift it started in - all counted in the
/// account's time zone, which the routes are handed.
/// </summary>
public sealed class ScheduleShiftsRoutesTests : IAsyncLifetime
{
    private readonly GatewayDbTestHarness _h = new();
    private CronJobStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private TimeZoneInfo? _zone = TimeZoneInfo.Utc;

    // The engine's clock, fixed: a run fired by hand starts at 17:00 UTC, which is the evening shift.
    private static readonly DateTime EngineNow = new(2026, 10, 12, 17, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task InitializeAsync()
    {
        var db = _h.Open();
        _store = new CronJobStore(db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var history = new CronRunHistoryStore(db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        var runRecords = new CronRunRecordReader(history, new CcDirector.Gateway.History.SessionHistoryStore(db).EndingsOf);
        CronJobEndpoints.Map(_app, _store, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            findFactory: (_, _) => null, runRecords: runRecords, accountZoneOf: _ => _zone);
        var engine = new CronEngine(_store, history, new RecordingStarter(), new UnusedWorkListRunner(),
            new NullCronNotifier(), new FakeClock(EngineNow));
        CronRunEndpoints.Map(_app, engine, runRecords, jobById: _store.Get,
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown, accountZoneOf: _ => _zone);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First() + "/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
    }

    // 15:45 every day in UTC: inside the last 30 minutes of the morning shift, and never run, so no history.
    private static CronJobDto LateMorning() => new()
    {
        Name = "late",
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "45 15 * * *",
        TimeZoneId = "UTC",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private static void AssertWarnedLateMorning(CronJobDto job)
    {
        Assert.Equal(new[] { WorkShifts.Morning }, job.Shifts);
        Assert.Equal("Morning shift", job.ShiftText);
        Assert.NotNull(job.ShiftWarning);
        Assert.StartsWith("Starts close to the end of its shift", job.ShiftWarning);
        Assert.Equal(ScheduleShifts.RuleText, job.ShiftRule);
    }

    [Fact]
    public async Task CreateListAndGet_ALateMorningSchedule_CarryItsShiftAndTheWarning()
    {
        var create = await _http.PostAsJsonAsync("cron/jobs", LateMorning());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CronJobDto>(JsonOpts))!;
        AssertWarnedLateMorning(created);

        var list = await _http.GetFromJsonAsync<JsonElement>("cron/jobs");
        var listed = list.GetProperty("jobs").Deserialize<List<CronJobDto>>(JsonOpts)!.Single();
        AssertWarnedLateMorning(listed);

        AssertWarnedLateMorning((await _http.GetFromJsonAsync<CronJobDto>($"cron/jobs/{created.Id}", JsonOpts))!);
    }

    [Fact]
    public async Task Update_MovedToTheMorning_LosesTheWarning()
    {
        var created = _store.Create(LateMorning());
        var moved = LateMorning();
        moved.CronExpression = "0 9 * * *";

        var answer = await _http.PutAsJsonAsync($"cron/jobs/{created.Id}", moved);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var updated = (await answer.Content.ReadFromJsonAsync<CronJobDto>(JsonOpts))!;
        Assert.Equal("Morning shift", updated.ShiftText);
        Assert.Null(updated.ShiftWarning);
        Assert.Null(updated.ShiftRule);
    }

    private static void AssertNoShift(CronJobDto job)
    {
        Assert.NotNull(job.Shifts);
        Assert.Empty(job.Shifts!);
        Assert.Equal("No run in the next 7 days", job.ShiftText);
        Assert.Null(job.ShiftWarning);
        Assert.Null(job.ShiftRule);
    }

    [Fact]
    public async Task ListAndGet_APausedRecurringSchedule_HasNoShiftAndNoWarning()
    {
        // The same 15:45 schedule that is warned about when it is on, switched off: it will not run.
        var paused = LateMorning();
        paused.Enabled = false;
        var created = _store.Create(paused);
        Assert.Equal(CronLifecycle.Paused, CronSchedule.LifecycleOf(_store.Get(created.Id)!));

        var list = await _http.GetFromJsonAsync<JsonElement>("cron/jobs");
        AssertNoShift(list.GetProperty("jobs").Deserialize<List<CronJobDto>>(JsonOpts)!.Single());
        AssertNoShift((await _http.GetFromJsonAsync<CronJobDto>($"cron/jobs/{created.Id}", JsonOpts))!);
    }

    [Fact]
    public async Task ListAndGet_ASpentOneOff_HasNoShiftAndNoWarning()
    {
        // A one-off that ran on 1 September; the engine switched it off as it fired it.
        var created = _store.Create(new CronJobDto
        {
            Name = "once",
            Enabled = true,
            ScheduleKind = CronSchedule.KindOneOff,
            RunAt = "2026-09-01T15:45:00",
            TimeZoneId = "UTC",
            Target = new CronJobTarget { Machine = "workstation-A" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        });
        _store.MarkFired(created.Id, new DateTime(2026, 9, 1, 15, 45, 0, DateTimeKind.Utc), "started", nextRunUtc: null, enabled: false);
        Assert.Equal(CronLifecycle.Spent, CronSchedule.LifecycleOf(_store.Get(created.Id)!));

        var list = await _http.GetFromJsonAsync<JsonElement>("cron/jobs");
        AssertNoShift(list.GetProperty("jobs").Deserialize<List<CronJobDto>>(JsonOpts)!.Single());
        AssertNoShift((await _http.GetFromJsonAsync<CronJobDto>($"cron/jobs/{created.Id}", JsonOpts))!);
    }

    [Fact]
    public async Task Runs_EachRunCarriesTheShiftItStartedIn()
    {
        var created = _store.Create(LateMorning());
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"cron/jobs/{created.Id}/run", null)).StatusCode);

        var body = await _http.GetFromJsonAsync<JsonElement>($"cron/jobs/{created.Id}/runs");
        var run = body.GetProperty("runs").Deserialize<List<CronRunRecord>>(JsonOpts)!.Single();

        Assert.Equal(EngineNow, run.FiredUtc);
        Assert.Equal(WorkShifts.Evening, WorkShifts.ShiftOf(run.FiredUtc, TimeZoneInfo.Utc).Name);
        Assert.Equal(WorkShifts.ShiftOf(run.FiredUtc, TimeZoneInfo.Utc).Text, run.ShiftText);
    }

    [Fact]
    public async Task List_NoAccountZone_AnswersWithoutShifts_RatherThanGuessingAZone()
    {
        _store.Create(LateMorning());
        _zone = null;

        var list = await _http.GetFromJsonAsync<JsonElement>("cron/jobs");
        var listed = list.GetProperty("jobs").Deserialize<List<CronJobDto>>(JsonOpts)!.Single();

        Assert.Null(listed.Shifts);
        Assert.Null(listed.ShiftText);
        Assert.Null(listed.ShiftWarning);
    }

    // ---- fakes ---------------------------------------------------------------------------------------

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; set; }
    }

    private sealed class RecordingStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult<(string?, string?, string?)>(("sid-1", "director-1", null));
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }
}
