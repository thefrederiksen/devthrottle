using System.Net;
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
/// HTTP wire tests for the cron run-now + run-history endpoints (epic #479, #483). Boots
/// <see cref="CronRunEndpoints"/> on an ephemeral loopback port over a real <see cref="CronEngine"/>
/// with a fake session starter (no live Director), and drives run-now + history over real HTTP -
/// the running-app proof for AC2 (run-now fires + records) and AC3 (the run-record shape).
/// </summary>
public sealed class CronRunEndpointsTests : IAsyncLifetime
{
    private readonly GatewayDbTestHarness _h = new();
    private readonly string _jobsPath = Path.Combine(
        Path.GetTempPath(), "cc-cronrun-ep-jobs-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _runsPath = Path.Combine(
        Path.GetTempPath(), "cc-cronrun-ep-runs-" + Guid.NewGuid().ToString("N") + ".json");

    private WebApplication _app = null!;  // built in InitializeAsync (xUnit lifecycle)
    private HttpClient _http = null!;     // built in InitializeAsync (xUnit lifecycle)
    private CronJobStore _store = null!;  // built in InitializeAsync (xUnit lifecycle)

    public async Task InitializeAsync()
    {
        // Issue #2161: bind an operating-system-assigned port, then learn the number it gave us. The
        // address cannot be built up front any more - "0" is not something a client can dial.
        var bindUrl = $"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}";

        var db = _h.Open();
        _store = new CronJobStore(db, _jobsPath);
        var history = new CronRunHistoryStore(db, _runsPath);
        var engine = new CronEngine(_store, history, new FakeStarter(), new UnusedWorkListRunner(), new NullCronNotifier(), new SystemClock());

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(bindUrl);
        CronRunEndpoints.Map(_app, engine,
            new CcDirector.Gateway.Running.CronRunRecordReader(history, new CcDirector.Gateway.History.SessionHistoryStore(db).EndingsOf), jobById: _ => null, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);
        await _app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{BoundPort.Of(_app)}";

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
        foreach (var p in new[] { _jobsPath, _runsPath })
            if (File.Exists(p)) File.Delete(p);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private CronJobDto SeedJob() => _store.Create(new CronJobDto
    {
        Name = "nightly",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/work-list run Tonight" },
    });

    [Fact]
    public async Task RunNow_FiresAndRecords_WithFullRecordShape()
    {
        var job = SeedJob();

        var resp = await _http.PostAsync($"/cron/jobs/{job.Id}/run", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var record = JsonSerializer.Deserialize<CronRunRecord>(await resp.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(record);                              // AC2: run-now produced a record
        Assert.Equal("workstation-A", record.Machine);       // #503: the target is the MACHINE
        Assert.Equal("director-1", record.TargetDirectorId); // ...and the Director it resolved to
        Assert.Equal("fake-sid", record.SessionId);
        Assert.Equal("started", record.InfraStatus);         // AC3: infra status present
        Assert.Equal("unknown", record.TaskStatus);          // AC3: task status separate from infra
        Assert.NotEqual(default, record.FiredUtc);
    }

    [Fact]
    public async Task Runs_ReturnsHistory_AfterRunNow()
    {
        var job = SeedJob();
        await _http.PostAsync($"/cron/jobs/{job.Id}/run", content: null);

        var resp = await _http.GetAsync($"/cron/jobs/{job.Id}/runs");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(job.Id, doc.RootElement.GetProperty("jobId").GetString());
        var runs = doc.RootElement.GetProperty("runs");
        Assert.Equal(1, runs.GetArrayLength());
        Assert.Equal("workstation-A", runs[0].GetProperty("machine").GetString());
    }

    [Fact]
    public async Task RunNow_NoSuchJob_Returns404()
    {
        var resp = await _http.PostAsync("/cron/jobs/cj_nope/run", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Runs_NoSuchJob_ReturnsEmptyList()
    {
        var resp = await _http.GetAsync("/cron/jobs/cj_nope/runs");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("runs").GetArrayLength());
    }

    private sealed class FakeStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult<(string?, string?, string?)>(("fake-sid", "director-1", null));
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("seed-job endpoint test must not trigger the work-list runner");
    }

}
