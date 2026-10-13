using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// PAUSE EVERYTHING through the REAL Gateway (issue #3910). The unit tests hand the switch to the engine themselves,
/// so they would stay green if the Gateway's own wiring forgot to. This boots a real <see cref="GatewayHost"/> on an
/// isolated root and proves the wiring a person's tap depends on: the route is mapped, the engine the Gateway runs
/// reads the switch, the switch is kept across a Gateway restart, and no schedule's own enabled flag is touched.
/// </summary>
[Collection("DirectorRoot")]
public sealed class CronPauseEverythingHostTests : IAsyncLifetime
{
    private const string Token = "test-token-pause-3910";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-cronpause-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public CronPauseEverythingHostTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-cronpause-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public Task InitializeAsync() => StartGatewayAsync();

    private async Task StartGatewayAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    private async Task StopGatewayAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
    }

    public async Task DisposeAsync()
    {
        await StopGatewayAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private async Task<CronJobDto> CreateAsync(string name, bool enabled)
    {
        var response = await _http.PostAsJsonAsync("cron/jobs", new
        {
            name,
            enabled,
            scheduleKind = "recurring",
            cronExpression = "0 0 * * *",
            timeZoneId = "America/Chicago",
            target = new { machine = "workstation-A" },
            action = new { repoPath = @"D:\repo", seed = "/help" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CronJobDto>(Web))!;
    }

    private async Task<Dictionary<string, bool>> EnabledFlagsAsync()
    {
        var body = await _http.GetFromJsonAsync<JsonElement>("cron/jobs");
        return body.GetProperty("jobs").EnumerateArray()
            .ToDictionary(j => j.GetProperty("id").GetString()!, j => j.GetProperty("enabled").GetBoolean());
    }

    private async Task<CronPauseDto> PutPauseAsync(bool paused)
    {
        var response = await _http.PutAsJsonAsync("cron/pause", new { paused });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CronPauseDto>(Web))!;
    }

    [Fact]
    public async Task TheRealGateway_PausesEverything_KeepsItAcrossARestart_AndResumesToExactlyTheStateBefore()
    {
        var on = await CreateAsync("on", enabled: true);
        var off = await CreateAsync("off", enabled: false);
        var before = await EnabledFlagsAsync();
        Assert.Equal(new Dictionary<string, bool> { [on.Id] = true, [off.Id] = false }, before);
        Assert.False((await _http.GetFromJsonAsync<CronPauseDto>("cron/pause", Web))!.Paused);

        // One tap.
        var paused = await PutPauseAsync(true);
        Assert.True(paused.Paused);
        Assert.Equal(CronPauseSwitch_PausedBanner, paused.Banner);

        // The engine the Gateway itself runs reads the switch: nothing is started, and it says why.
        var refused = await _http.PostAsync($"cron/jobs/{on.Id}/run", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(CronRunEndpoints.PausedRefusal,
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(before, await EnabledFlagsAsync());

        // A Gateway restart keeps it.
        await StopGatewayAsync();
        await StartGatewayAsync();
        var afterRestart = await _http.GetFromJsonAsync<CronPauseDto>("cron/pause", Web);
        Assert.True(afterRestart!.Paused);
        Assert.Equal(paused.SinceUtc, afterRestart.SinceUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync($"cron/jobs/{on.Id}/run", null)).StatusCode);
        Assert.Equal(before, await EnabledFlagsAsync());

        // One tap back: exactly the state before - the active one active, the paused one still paused.
        var resumed = await PutPauseAsync(false);
        Assert.False(resumed.Paused);
        Assert.Null(resumed.Banner);
        Assert.Equal(before, await EnabledFlagsAsync());
        // And schedules start again: the same run-now is no longer refused for being paused.
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"cron/jobs/{on.Id}/run", null)).StatusCode);
    }

    private const string CronPauseSwitch_PausedBanner = CcDirector.Gateway.Running.CronPauseSwitch.PausedBanner;
}
