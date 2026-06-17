using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the cron-job endpoints (epic #479, #482). Boots only
/// <see cref="CronJobEndpoints"/> on an ephemeral loopback port with a fresh store (no Tailscale,
/// no registry, no auth) and drives the full CRUD contract over real HTTP - the running-app proof
/// for this slice (the Gateway exposes no UI). Asserts the 201/400/404 statuses and JSON shapes the
/// firing engine (#483) and the eventual Cockpit UI depend on.
/// </summary>
public sealed class CronJobEndpointsTests : IAsyncLifetime
{
    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), "cc-cronjob-ep-tests-" + Guid.NewGuid().ToString("N") + ".json");

    private WebApplication _app = null!; // built in InitializeAsync (xUnit lifecycle)
    private HttpClient _http = null!;    // built in InitializeAsync (xUnit lifecycle)

    public async Task InitializeAsync()
    {
        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        CronJobEndpoints.Map(_app, new CronJobStore(_storePath));
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static object ValidBody(string name = "nightly") => new
    {
        name,
        scheduleKind = "recurring",
        cronExpression = "0 0 * * *",
        timeZoneId = "America/Chicago",
        target = new { directorId = "workstation-A" },
        action = new { repoPath = @"D:\repo", seed = "/work-list run Tonight" },
    };

    private async Task<CronJobDto> CreateJob(string name = "nightly")
    {
        var resp = await _http.PostAsJsonAsync("/cron/jobs", ValidBody(name));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = JsonSerializer.Deserialize<CronJobDto>(await resp.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(dto);
        return dto;
    }

    [Fact]
    public async Task Post_ValidJob_Returns201_WithIdAndNextRun()
    {
        var resp = await _http.PostAsJsonAsync("/cron/jobs", ValidBody());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = JsonSerializer.Deserialize<CronJobDto>(await resp.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(dto);
        Assert.StartsWith("cj_", dto.Id);
        Assert.NotNull(dto.NextRunUtc);
    }

    [Fact]
    public async Task Post_InvalidCron_Returns400_AndIsNotStored()
    {
        var bad = new
        {
            name = "bad",
            scheduleKind = "recurring",
            cronExpression = "not a cron",
            timeZoneId = "America/Chicago",
            target = new { directorId = "workstation-A" },
            action = new { repoPath = @"D:\repo", seed = "/help" },
        };

        var resp = await _http.PostAsJsonAsync("/cron/jobs", bad);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var list = await _http.GetAsync("/cron/jobs");
        var body = await list.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Empty(doc.RootElement.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Get_ById_ReturnsJob_AndMissingReturns404()
    {
        var created = await CreateJob();

        var ok = await _http.GetAsync($"/cron/jobs/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var missing = await _http.GetAsync("/cron/jobs/cj_nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task GetAll_ListsCreatedJobs()
    {
        await CreateJob("a");
        await CreateJob("b");

        var resp = await _http.GetAsync("/cron/jobs");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("jobs").GetArrayLength());
    }

    [Fact]
    public async Task Put_UpdatesJob_AndMissingReturns404()
    {
        var created = await CreateJob();

        var edit = new
        {
            name = "renamed",
            scheduleKind = "recurring",
            cronExpression = "30 9 * * 1-5",
            timeZoneId = "America/Chicago",
            target = new { directorId = "workstation-A" },
            action = new { repoPath = @"D:\repo", seed = "/help" },
        };
        var put = await _http.PutAsJsonAsync($"/cron/jobs/{created.Id}", edit);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var dto = JsonSerializer.Deserialize<CronJobDto>(await put.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(dto);
        Assert.Equal("renamed", dto.Name);
        Assert.Equal("30 9 * * 1-5", dto.CronExpression);

        var missing = await _http.PutAsJsonAsync("/cron/jobs/cj_nope", edit);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesJob_AndMissingReturns404()
    {
        var created = await CreateJob();

        var del = await _http.DeleteAsync($"/cron/jobs/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var gone = await _http.GetAsync($"/cron/jobs/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        var missing = await _http.DeleteAsync("/cron/jobs/cj_nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
