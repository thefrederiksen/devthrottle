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
/// Window schedules over real HTTP (the owner, 2026-10-09): the create route places the minute, the list shows the
/// window and the placement in words only to a caller that asks for the kind, a schedule that runs after another
/// follows it when it moves, a fixed schedule is never moved, and a window that cannot be placed is refused with why.
/// </summary>
public sealed class WindowScheduleWiringTests : IAsyncLifetime
{
    private readonly GatewayDbTestHarness _h = new();
    private CronJobStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    // Every window here opens a few hours from now on the schedules' own clock, so nothing in it has passed whatever
    // time the test runs at.
    private static readonly int Base = (TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Toronto).Hour + 3) % 24;

    private static string At(int hoursFromBase, int minute = 0) => $"{(Base + hoursFromBase) % 24:00}:{minute:00}";

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
            findFactory: (_, _) => null, runRecords: runRecords);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First() + "/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
    }

    private static object Body(string name, string kind, string expression) => new
    {
        name,
        scheduleKind = kind,
        cronExpression = expression,
        timeZoneId = "America/Toronto",
        target = new { machine = "SOREN_NORTH" },
        action = new { repoPath = @"D:\repo", seed = "/help" },
    };

    private async Task<CronJobDto> Create(string name, string kind, string expression)
    {
        var resp = await _http.PostAsJsonAsync("cron/jobs", Body(name, kind, expression));
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<CronJobDto>(await resp.Content.ReadAsStringAsync(), JsonOpts)!;
    }

    private static int PlacedMinute(CronJobDto job) => WindowSchedule.Parse(job.CronExpression).Settings!.PlacedMinute!.Value;

    private static int MinutesAfter(int later, int earlier) => ((later - earlier) % 1440 + 1440) % 1440;

    [Fact]
    public async Task Create_AWindowSchedule_IsPlacedAndSaysWhereInWords()
    {
        var created = await Create("nightly digest", "window", $"window={At(0)}-{At(2)} placed=00:00");

        // The caller's placed= is discarded; the Gateway placed it in the middle of an empty machine's window.
        Assert.Equal($"window={At(0)}-{At(2)} placed={At(1)}", created.CronExpression);
        Assert.Equal($"window {At(0)}-{At(2)}, placed {At(1)}", created.ScheduleText);
        Assert.NotNull(created.NextRunUtc);
        Assert.Equal(At(1), TimeZoneInfo.ConvertTimeFromUtc(created.NextRunUtc!.Value, Toronto).ToString("HH:mm"));
    }

    [Fact]
    public async Task List_ShowsAWindowScheduleOnlyToACallerThatAsks()
    {
        var created = await Create("nightly digest", "window", $"window={At(0)}-{At(2)}");

        Assert.DoesNotContain(created.Id, await _http.GetStringAsync("cron/jobs"));
        Assert.DoesNotContain(created.Id, await _http.GetStringAsync("cron/jobs?include=random"));
        Assert.Contains(created.Id, await _http.GetStringAsync("cron/jobs?include=random,window"));
    }

    [Fact]
    public async Task Update_TheScheduleAnotherRunsAfter_MovesTheOtherToKeepItsGap_AndNeverMovesTheFixedOne()
    {
        // The owner's example: one about now+1h, the other at least two hours after it, named in words by the list.
        var anchor = await Create("backup", "recurring", $"0 {(Base + 1) % 24} * * *");
        var follower = await Create("digest", "window", $"window={At(1)}-{At(5)} after={anchor.Id} gap=120");
        Assert.True(MinutesAfter(PlacedMinute(follower), (Base + 1) % 24 * 60) >= 120);
        var listed = (await _http.GetStringAsync("cron/jobs?include=window"));
        Assert.Contains("at least 2h 00m after backup", listed);

        // Move the fixed one an hour later; it stays exactly where it was put, and the window follows.
        anchor.CronExpression = $"0 {(Base + 2) % 24} * * *";
        var resp = await _http.PutAsJsonAsync($"cron/jobs/{anchor.Id}", anchor);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal($"0 {(Base + 2) % 24} * * *", _store.Get(anchor.Id)!.CronExpression);
        var moved = _store.Get(follower.Id)!;
        Assert.True(MinutesAfter(PlacedMinute(moved), (Base + 2) % 24 * 60) >= 120,
            $"the digest at {WindowSchedule.Hhmm(PlacedMinute(moved))} is under two hours after the backup at {At(2)}");
    }

    [Fact]
    public async Task Update_AnAnchorMovedSoFarItsFollowerCannotKeepItsGap_KeepsTheFollowerAndSaysSo()
    {
        var anchor = await Create("backup", "recurring", $"0 {(Base + 1) % 24} * * *");
        var follower = await Create("digest", "window", $"window={At(3)}-{At(4)} after={anchor.Id} gap=120");
        var before = follower.CronExpression;

        anchor.CronExpression = $"0 {(Base + 3) % 24} * * *";
        var resp = await _http.PutAsJsonAsync($"cron/jobs/{anchor.Id}", anchor);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var answer = JsonSerializer.Deserialize<CronJobDto>(await resp.Content.ReadAsStringAsync(), JsonOpts)!;
        Assert.Contains("'digest' runs after it and keeps its old minute", answer.LoadWarning);
        Assert.Equal(before, _store.Get(follower.Id)!.CronExpression);
    }

    [Fact]
    public async Task Create_AWindowThatCannotBePlaced_IsRefusedWithTheReason()
    {
        var resp = await _http.PostAsJsonAsync("cron/jobs",
            Body("digest", "window", $"window={At(0)}-{At(0, 45)} after=cj_nobody"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("this window schedule cannot be placed: after=cj_nobody: there is no schedule with that id",
            await resp.Content.ReadAsStringAsync());
        Assert.Empty(_store.ListAll());
    }
}
