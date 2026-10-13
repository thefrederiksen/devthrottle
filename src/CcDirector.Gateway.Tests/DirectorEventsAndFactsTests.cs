using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #330 (plan 1B): the doorbell event vocabulary (session-created / session-exited /
/// prompt-detected) recorded by the Gateway's per-director event ring and observable at
/// GET /directors/{id}/events, plus the machine facts (tool inventory with versions +
/// launcher presence/port) pulled through the proxy leg GET /directors/{id}/facts.
/// Boots a real Director Control API and a real Gateway in-process (the GatewayHostTests
/// harness) and exercises everything over loopback HTTP.
/// </summary>
public sealed class DirectorEventsAndFactsTests : IAsyncLifetime
{
    private const string Token = "test-token-330";

    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: _instancesDir);
        await _director.StartAsync();

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // Give the FileSystemWatcher a moment to pick up the director.
        await WaitFor(async () =>
        {
            var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
            return directors is not null && directors.Any(d => d.DirectorId == _director.DirectorId);
        }, TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _director.StopAsync();
        _sm.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ===== Events (doorbell vocabulary -> ring -> debug surface) =====

    [Fact]
    public async Task Doorbell_EventVocabulary_IsRecorded_AndObservableAtEventsRoute()
    {
        var sid = Guid.NewGuid().ToString();
        await PostDoorbell(sid, "Starting", DoorbellEvents.SessionCreated);
        await PostDoorbell(sid, "WaitingForInput", DoorbellEvents.PromptDetected);
        await PostDoorbell(sid, "Exited", DoorbellEvents.SessionExited);

        var events = await GetEventsFor(sid);

        Assert.Equal(
            new[] { DoorbellEvents.SessionCreated, DoorbellEvents.PromptDetected, DoorbellEvents.SessionExited },
            events.Select(e => e.Event));
        Assert.Equal(new[] { "Starting", "WaitingForInput", "Exited" }, events.Select(e => e.State));
        Assert.All(events, e => Assert.True(e.ReceivedAt > DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task Doorbell_LegacyShape_NoEventField_Still200_RecordsNoEvent()
    {
        var sid = Guid.NewGuid().ToString();
        // The exact pre-#330 wire shape: sessionId + newState only.
        var resp = await _http.PostAsJsonAsync($"directors/{_director.DirectorId}/doorbell",
            new { sessionId = sid, newState = "Working" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(await GetEventsFor(sid));
    }

    [Fact]
    public async Task Events_UnknownDirector_Returns404()
    {
        var resp = await _http.GetAsync($"directors/{Guid.NewGuid()}/events");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GatewayClient_NotifySessionState_WithEvent_LandsInTheRing()
    {
        // The Director-side leg (Gateway Cleanup mission, tunnel-only): the GatewayClient no longer HTTP-
        // registers, but the doorbell is an outbound front-door notify that fires as soon as a Gateway is
        // configured - no registration handshake needed. End to end, no session spawn.
        var id = Guid.NewGuid().ToString();
        var sid = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = Token };
        using var client = new GatewayClient(cfg, id, "9.9.9-test");
        client.Start();

        // In production the Director is registered via its tunnel Hello before it rings the doorbell; this
        // test drives only the GatewayClient doorbell leg, so register it the same way (tunnel-only Source).
        _gateway.Registry.RegisterFromStream(id, Environment.MachineName, "test", "9.9.9-test", 1, DateTime.UtcNow, TenantId.Local);

        client.NotifySessionState(sid, "Starting", DoorbellEvents.SessionCreated);

        await WaitFor(() => Task.FromResult(_gateway.DirectorEvents.For(TenantId.Local, id).Count > 0), TimeSpan.FromSeconds(5));
        var ev = Assert.Single(_gateway.DirectorEvents.For(TenantId.Local, id));
        Assert.Equal(sid, ev.SessionId);
        Assert.Equal(DoorbellEvents.SessionCreated, ev.Event);
        Assert.Equal("Starting", ev.State);

        await client.StopAsync();
    }

    // ===== Facts (tool inventory + launcher, pulled through the proxy leg) =====

    [Fact]
    public async Task Facts_Proxy_ReturnsToolInventoryAndLauncherFact()
    {
        // Gateway Cleanup mission (the cut): /directors/{id}/facts rides the tunnel ("facts" verb,
        // director-level). A tunnel-connected Director registered UNREACHABLE answers with a
        // DirectorFactsDto, so a working body could only have come over the tunnel. The Director-side
        // facts core (the embedded catalog inventory + launcher probe) is exercised by its own executor
        // tests; here the route -> verb -> DTO-shape contract is what is observable.
        const string factsDir = "dir-facts-330";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, factsDir, dispatch: cmd => cmd.Verb switch
        {
            "facts" => FakeTunnelDirector.Ok(new DirectorFactsDto
            {
                DirectorId = factsDir,
                MachineName = "facts-machine",
                Version = "1.0.0-test",
                Tools = new List<ToolInventoryItemDto>
                {
                    new() { Name = "cc-vault", Category = "Vault", Version = "1.2.3", IsBuilt = true },
                },
                Launcher = new LauncherFactDto { Installed = false }, // "not installed" is a valid fact
            }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var facts = await _http.GetFromJsonAsync<DirectorFactsDto>($"directors/{factsDir}/facts");

        Assert.NotNull(facts);
        Assert.Equal(factsDir, facts!.DirectorId);
        Assert.Equal("1.0.0-test", facts.Version);
        Assert.NotEmpty(facts.Tools);
        Assert.All(facts.Tools, t => Assert.False(string.IsNullOrEmpty(t.Name)));
        Assert.NotNull(facts.Launcher); // "not installed" is a valid fact, never a missing one
        Assert.NotNull(fake.LastOf("facts"));
    }

    [Fact]
    public async Task Facts_UnknownDirector_Returns404()
    {
        var resp = await _http.GetAsync($"directors/{Guid.NewGuid()}/facts");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== Helpers =====

    private async Task PostDoorbell(string sessionId, string newState, string eventName)
    {
        var resp = await _http.PostAsJsonAsync($"directors/{_director.DirectorId}/doorbell",
            new DoorbellRequest { SessionId = sessionId, NewState = newState, Event = eventName });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>This director's recorded events for ONE session id (tests share the director,
    /// so each fact filters to its own session ids).</summary>
    private async Task<List<DirectorEventDto>> GetEventsFor(string sessionId)
    {
        var resp = await _http.GetAsync($"directors/{_director.DirectorId}/events");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<EventsResponse>();
        Assert.NotNull(body);
        return body.Events.Where(e => e.SessionId == sessionId).ToList();
    }

    private sealed class EventsResponse
    {
        public string DirectorId { get; set; } = "";
        public List<DirectorEventDto> Events { get; set; } = new();
    }

    private static async Task WaitFor(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("condition not reached within timeout");
    }

}
