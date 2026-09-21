using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Triggers;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE FACTORY TRIGGERS THROUGH A REALLY BOOTED GATEWAY (the Website Business Factory mission, product track), over
/// HTTP, with the Director's real <see cref="GatewayClient"/> on the other end.
///
/// SWITCH OFF: the trigger routes are not mapped. A POST to one answers 404. A GET falls through to whatever every
/// unmapped GET answers - 404, or the Cockpit's page when the Cockpit is built into this host - and never JSON; the
/// Director's client reads either as "the Gateway serves no triggers", and a runner over it runs NOTHING, proven by a
/// check runner that counts its calls.
///
/// SWITCH ON: a trigger is created, read and paused over the routes, and the Director half runs end to end - the real
/// client fetches its checks for its registered machine and reports a result, and the Gateway writes the run rows. No
/// Director is connected to the tunnel, so a check that counts work reaches the spawner and is recorded as a failed
/// START with the spawner's own words - which is itself the proof the decision reached the start.
/// </summary>
public sealed class FactoryTriggerHostTests : IAsyncLifetime
{
    private const string Token = "factory-trigger-host-token";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-factory-trigger-host-" + Guid.NewGuid().ToString("N"));
    private readonly string _directorId = Guid.NewGuid().ToString();
    private GatewayHost _on = null!;
    private GatewayHost _off = null!;
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        // Every Gateway here keeps its database under its OWN temporary root. Without this a test run from inside a
        // Director session writes into that Director's real folder, which the session's CC_DIRECTOR_ROOT names. The
        // database path is read when a host is constructed, so each host gets a separate one. This suite runs its
        // tests one at a time (TestParallelization.cs), so moving the process-wide setting is safe.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Path.Combine(_instancesDir, "root-off"));
        _off = Boot(factoryAgentsEnabled: false, "off");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Path.Combine(_instancesDir, "root-on"));
        _on = Boot(factoryAgentsEnabled: true, "on");
        await _off.StartAsync();
        await _on.StartAsync();
        _on.Registry.RegisterFromStream(_directorId, Environment.MachineName, "test", "9.9.9-test", 1, DateTime.UtcNow, TenantId.Local);
        _off.Registry.RegisterFromStream(_directorId, Environment.MachineName, "test", "9.9.9-test", 1, DateTime.UtcNow, TenantId.Local);
    }

    private GatewayHost Boot(bool factoryAgentsEnabled, string name)
        => new(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_instancesDir, name),
            workListsPath: Path.Combine(_instancesDir, name, "worklists", "worklists.json"),
            streamMode: true, directorLaunchTimeout: TimeSpan.FromSeconds(2),
            factoryAgentsEnabled: factoryAgentsEnabled);

    public async Task DisposeAsync()
    {
        await _on.StopAsync();
        await _off.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (IOException) { /* best effort */ }
    }

    private static HttpClient Http(GatewayHost host)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http;
    }

    private static TriggerDefinitionRequest Definition() => new()
    {
        Name = "website-new-mail",
        Factory = "website-factory",
        FactoryAgent = "Front Desk",
        Machine = Environment.MachineName,
        RepoPath = Path.GetTempPath(),
        CheckCommand = "cc-website-factory mail-waiting --json",
        IntervalSeconds = 300,
        Prompt = "New mail: {count} threads. Handle them.",
    };

    private GatewayClient DirectorClient(GatewayHost host)
        => new(new GatewayConfig { Url = $"http://127.0.0.1:{host.Port}", Token = Token }, _directorId, "9.9.9-test");

    [Fact]
    public async Task SwitchOff_TheRoutesAreNotMapped_AndTheDirectorRunsNothing()
    {
        Assert.False(_off.FactoryAgentsEnabled);
        using var http = Http(_off);

        var post = await http.PostAsJsonAsync("triggers", Definition());
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        var report = await http.PostAsJsonAsync($"directors/{_directorId}/triggers/website-new-mail/checks",
            new TriggerCheckReport { ExitCode = 0, Output = "{\"count\": 2}" });
        Assert.Equal(HttpStatusCode.NotFound, report.StatusCode);
        var pause = await http.PostAsync("triggers/website-new-mail/pause", null);
        Assert.Equal(HttpStatusCode.NotFound, pause.StatusCode);

        foreach (var path in new[] { "triggers", $"directors/{_directorId}/triggers" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var get = await http.SendAsync(request);
            Assert.True(get.StatusCode == HttpStatusCode.NotFound
                        || get.Content.Headers.ContentType?.MediaType == "text/html",
                $"GET {path} with the switch off answered {(int)get.StatusCode} {get.Content.Headers.ContentType?.MediaType}");
            Assert.NotEqual("application/json", get.Content.Headers.ContentType?.MediaType);
        }

        using var client = DirectorClient(_off);
        var fetch = await client.FetchTriggersAsync(_directorId, CancellationToken.None);
        Assert.Equal(TriggerFetchKind.Off, fetch.Kind);

        var checks = 0;
        var runner = new DirectorTriggerRunner(_directorId, () => client,
            (_, _) => { Interlocked.Increment(ref checks); return Task.FromResult(new TriggerCheckReport()); },
            () => DateTime.UtcNow);
        for (var i = 0; i < 3; i++)
            Assert.Empty(await runner.TickAsync(CancellationToken.None));
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task SwitchOn_CreateReadPause_AndTheDirectorFetchesAndReports_EndToEnd()
    {
        Assert.True(_on.FactoryAgentsEnabled);
        using var http = Http(_on);

        var created = await http.PostAsJsonAsync("triggers", Definition());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var trigger = (await created.Content.ReadFromJsonAsync<TriggerDto>())!;
        Assert.Equal("website-new-mail", trigger.Name);
        Assert.Equal("machine token", trigger.CreatedBy);
        Assert.Equal("waiting for the first check", trigger.StatusText);

        var duplicate = await http.PostAsJsonAsync("triggers", Definition());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var tooOften = Definition();
        tooOften.Name = "too-often";
        tooOften.IntervalSeconds = 30;
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("triggers", tooOften)).StatusCode);

        // The Director half, over the real client.
        using var client = DirectorClient(_on);
        var fetch = await client.FetchTriggersAsync(_directorId, CancellationToken.None);
        Assert.Equal(TriggerFetchKind.Assigned, fetch.Kind);
        var assigned = Assert.Single(fetch.Triggers);
        Assert.Equal(trigger.Id, assigned.Id);
        Assert.Equal(150, assigned.TimeoutSeconds);

        // An empty check: nothing to do.
        Assert.Null(await client.ReportTriggerCheckAsync(_directorId, assigned.Id,
            new TriggerCheckReport { CheckedAtUtc = DateTime.UtcNow, ExitCode = 0, Output = "{\"count\": 0}" }, CancellationToken.None));
        // A broken check: failed, and the trigger is red.
        Assert.Null(await client.ReportTriggerCheckAsync(_directorId, assigned.Id,
            new TriggerCheckReport { CheckedAtUtc = DateTime.UtcNow, ExitCode = 1, ErrorOutput = "not signed in" }, CancellationToken.None));
        var red = (await http.GetFromJsonAsync<TriggerDto>("triggers/website-new-mail"))!;
        Assert.Equal(TriggerStatusKind.Red, red.Status);
        Assert.Equal("check failed: exit code 1: not signed in", red.StatusText);

        // Paused: counted work, started nothing.
        var paused = await http.PostAsync("triggers/website-new-mail/pause", null);
        Assert.True((await paused.Content.ReadFromJsonAsync<TriggerDto>())!.Paused);
        Assert.Null(await client.ReportTriggerCheckAsync(_directorId, assigned.Id,
            new TriggerCheckReport { CheckedAtUtc = DateTime.UtcNow, ExitCode = 0, Output = "{\"count\": 2}" }, CancellationToken.None));
        await http.PostAsync("triggers/website-new-mail/resume", null);

        // Work, resumed: the Gateway goes to start a session. No Director is on the tunnel, so the start fails in
        // the spawner's own words - recorded, and red.
        Assert.Null(await client.ReportTriggerCheckAsync(_directorId, assigned.Id,
            new TriggerCheckReport { CheckedAtUtc = DateTime.UtcNow, ExitCode = 0, Output = "{\"count\": 2}" }, CancellationToken.None));

        var runs = (await http.GetFromJsonAsync<TriggerRunListResponse>("triggers/website-new-mail/runs"))!.Runs;
        Assert.Equal(new[] { TriggerRunOutcome.Failed, TriggerRunOutcome.Paused, TriggerRunOutcome.Failed, TriggerRunOutcome.NothingToDo },
            runs.Select(r => r.Outcome).ToArray());
        Assert.StartsWith(Factory.Triggers.TriggerStatusFold.StartFailedPrefix, runs[0].Reason);
        Assert.Equal(2, runs[0].Count);
        Assert.Equal(2, runs[1].Count);
        Assert.Equal("exit code 1: not signed in", runs[2].Reason);
        Assert.All(runs, r => Assert.Equal(_directorId, r.DirectorId));

        var list = (await http.GetFromJsonAsync<TriggerListResponse>("triggers"))!;
        var listed = Assert.Single(list.Triggers);
        Assert.StartsWith("start failed: ", listed.StatusText);
    }

    [Fact]
    public async Task SwitchOn_AReportFromADirectorTheAccountDoesNotHave_Is404()
    {
        using var http = Http(_on);
        var body = new StringContent("{\"exitCode\":0,\"output\":\"{}\"}", Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"directors/{Guid.NewGuid()}/triggers/anything/checks", body);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("director_not_found", await resp.Content.ReadAsStringAsync());
    }
}
