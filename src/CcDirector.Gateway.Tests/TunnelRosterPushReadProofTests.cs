using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;
using CcDirector.Core.Tests;   // WindowsOnlyFact, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E, Group C): the Gateway roster PULLS that used to call
/// <c>DirectorEndpointClient.ListSessions*</c> now read the PUSH store (<c>PushedSessions</c>) under stream mode,
/// per the Architect ruling (the roster is authoritative in the push store; no pull verb). This covers the three
/// pure roster reads: <c>/healthz</c> session count, the <c>/exes/list</c> per-Director session list, and the
/// <c>DELETE /directors/{id}</c> live-session safety gate.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered UNREACHABLE and its sessions are delivered ONLY via a
/// stream PushSnapshot. So a result that reflects those sessions can ONLY have come from the push store - an
/// HTTP pull to the unreachable Director would have returned nothing.
///
/// ALSO PINS THE /exes/list FOLD (defect 6). That page is here rather than in the aggregation suite because
/// this is the fixture that already has what it needs: a Director registered on THIS machine (the page is
/// local-machine only) delivering its roster over the tunnel. The fold assertions are about what the page
/// RENDERS, not about where the rows came from - but they need the rows to come from somewhere, and this is
/// where that setup lives.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelRosterPushReadProofTests : IAsyncLifetime
{
    private const string Token = "test-token-roster-push-read";
    private const string DirectorId = "dir-roster-push";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-rosterpush-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    public TunnelRosterPushReadProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-rosterpush-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // Registered UNREACHABLE, but on THIS machine so /exes/list (local-machine only) surfaces it.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59920/", // nothing listens here
            MachineName = Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    [Fact]
    public async Task Healthz_countsSessionsFromThePushStore()
    {
        await PushAsync(1L,
            new SessionDto { SessionId = Guid.NewGuid().ToString(), Status = "WaitingForInput", ActivityState = "WaitingForInput" },
            new SessionDto { SessionId = Guid.NewGuid().ToString(), Status = "Working", ActivityState = "Working" });

        var node = await _http.GetFromJsonAsync<JsonNode>("healthz");
        // An HTTP pull to the unreachable Director would count 0; the push store carries 2.
        Assert.Equal(2, node?["sessions"]?.GetValue<int>());
    }

    [WindowsOnlyFact("the /exes surface is mapped only on Windows - it builds developer slot executables by shelling out to powershell.exe against a local_builds directory, so GatewayEndpoints deliberately does not map those routes off Windows and /exes/list answers 404")]
    public async Task ExesList_readsTheDirectorSessionsFromThePushStore()
    {
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, new SessionDto
        {
            SessionId = sid,
            Name = "a pushed session",
            Agent = "ClaudeCode",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });

        var node = await _http.GetFromJsonAsync<JsonNode>("exes/list");
        var directors = node?["directors"]?.AsArray();
        var mine = directors?.FirstOrDefault(d => d?["directorId"]?.GetValue<string>() == DirectorId);
        Assert.NotNull(mine);
        var sessions = mine!["sessions"]?.AsArray();
        Assert.Equal(1, sessions?.Count);
        Assert.Equal(sid, sessions?[0]?["sessionId"]?.GetValue<string>());
        Assert.Null(mine["sessionError"]?.GetValue<string?>()); // no pull error - it never pulled
    }

    // ---------- defect 6: /exes/list runs the SAME fleet pass as every other screen ----------

    [WindowsOnlyFact("the /exes surface is mapped only on Windows - it builds developer slot executables by shelling out to powershell.exe against a local_builds directory, so GatewayEndpoints deliberately does not map those routes off Windows and /exes/list answers 404")]
    public async Task ExesList_runsTheFleetPass_soAWorkersRedIsSuppressedHereToo()
    {
        // DEFECT 6. This page folded each session on its own, straight out of the push store, with NO fleet
        // pass. SessionRole is resolved ONLY by that pass (the Director never sends it - it cannot: "is my
        // controller alive?" may be a question about another machine), so the role was null here, the Worker
        // red-suppression could not fire, and a live Worker rendered RED on this page while every other
        // screen showed it receded to "supporting" / "Sub-agent".
        var mgr = Guid.NewGuid().ToString();
        var wrk = Guid.NewGuid().ToString();
        await PushAsync(1L,
            new SessionDto
            {
                SessionId = mgr, Name = "the manager", Agent = "ClaudeCode",
                Status = "Running", ActivityState = "Working", StatusColor = "blue", RepoPath = @"D:\repo",
            },
            new SessionDto
            {
                SessionId = wrk, Name = "the worker", Agent = "ClaudeCode",
                Status = "Running", ActivityState = "WaitingForInput", StatusColor = "red", RepoPath = @"D:\repo",
                IsControlled = true, ControllerSessionId = mgr,
            });

        var node = await _http.GetFromJsonAsync<JsonNode>("exes/list");
        var mine = node?["directors"]?.AsArray()
            .FirstOrDefault(d => d?["directorId"]?.GetValue<string>() == DirectorId);
        Assert.NotNull(mine);
        var sessions = mine!["sessions"]?.AsArray();

        var workerOut = Assert.Single(sessions!, s => s?["sessionId"]?.GetValue<string>() == wrk);
        // Before the fix: "red" / "Needs you" here, the receded answer on the roster.
        Assert.Equal("supporting", workerOut?["effectiveColor"]?.GetValue<string>());
        // "Snoozed" (was "Sub-agent") since the owner ruled on 2026-09-02 that a supervised session goes to
        // on-hold when it is not working.
        Assert.Equal("Snoozed", workerOut?["stateLabel"]?.GetValue<string>());
        // NO BUCKET ASSERTION HERE, ON PURPOSE - /exes/list DOES NOT EMIT ONE. Its projection
        // (ExesEndpoints) hand-picks eight fields - sessionId, name, agent, activityState, statusColor,
        // effectiveColor, stateLabel, snoozeExpired - and triageBucket is not among them. An assertion on it
        // fails not because the fold is wrong but because this page never carried the field; that is exactly
        // the mistake that was made here first, and this note exists so the next person does not repeat it.
        //
        // The bucket IS proven over real HTTP, on the path where it matters: the ROSTER, in
        // SessionsAggregationTests. This is a developer diagnostic page, and its contract is the colour and
        // the words beside it. If it ever needs the bucket, add it to the projection first - do not assert a
        // field into existence.

        // The manager is untouched - its own working shows blue, and the law holds on this page too.
        var mgrOut = Assert.Single(sessions!, s => s?["sessionId"]?.GetValue<string>() == mgr);
        Assert.Equal("blue", mgrOut?["effectiveColor"]?.GetValue<string>());
        Assert.Equal("Working", mgrOut?["stateLabel"]?.GetValue<string>());
    }

    [WindowsOnlyFact("the /exes surface is mapped only on Windows - it builds developer slot executables by shelling out to powershell.exe against a local_builds directory, so GatewayEndpoints deliberately does not map those routes off Windows and /exes/list answers 404")]
    public async Task ExesList_carriesTheSnoozeEndedBadge()
    {
        // Inspection round 2, finding 3: /exes/list folds correctly but its projection dropped snoozeExpired,
        // so the "Snooze ended" badge - which the mission requires on every roster - never rode this page.
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, new SessionDto
        {
            SessionId = sid, Name = "returned by timer", Agent = "ClaudeCode",
            Status = "Running", ActivityState = "WaitingForInput", StatusColor = "red", RepoPath = @"D:\repo",
        });
        // An armed snooze whose clock has already elapsed: the fold stamps SnoozeExpired=true (returned by
        // its timer, not a fresh turn-end).
        _gateway.SnoozeRegistry.Snooze(sid, DateTime.UtcNow.AddMinutes(-1), DirectorId);

        var node = await _http.GetFromJsonAsync<JsonNode>("exes/list");
        var mine = node?["directors"]?.AsArray()
            .FirstOrDefault(d => d?["directorId"]?.GetValue<string>() == DirectorId);
        Assert.NotNull(mine);
        var sessions = mine!["sessions"]?.AsArray();
        var snoozed = Assert.Single(sessions!, s => s?["sessionId"]?.GetValue<string>() == sid);

        Assert.True(snoozed?["snoozeExpired"]?.GetValue<bool>()); // the badge rides /exes/list
    }

    [Fact]
    public async Task DeleteDirectorGate_readsTheLiveSessionCountFromThePushStore()
    {
        // One live session in the push store. An HTTP pull to the unreachable Director would return null and the
        // gate would be SKIPPED (deletion proceeds); a 409 citing the live count proves the push-store read.
        await PushAsync(1L, new SessionDto
        {
            SessionId = Guid.NewGuid().ToString(),
            Name = "a live session",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });

        var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{DirectorId}")
        {
            Content = JsonContent.Create(new { reason = "cleanup test", force = false }),
        };
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(1, node?["liveSessionCount"]?.GetValue<int>());
    }

}
