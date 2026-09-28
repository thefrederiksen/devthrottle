using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Boots a real Director Control API and a real Gateway in-process, then exercises
/// the gateway's REST API over loopback. This covers the discovery flow, proxy
/// routing, auth middleware, and error paths.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class GatewayHostTests : IAsyncLifetime
{
    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private int _gatewayPort;

    // Isolated discovery dir: the test Director and Gateway find each other here, and a real
    // Director running on the dev machine can never leak into (or see) these test hosts.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// An in-memory <see cref="IProtectedTokenStore"/> so the test host's credential service never
    /// touches the real Windows Data Protection store (the AccountStatusEndpointTests pattern).
    /// </summary>
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    public async Task InitializeAsync()
    {
        // Boot a director
        _sm = new SessionManager(new AgentOptions());
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: _instancesDir);
        await _director.StartAsync();

        // Boot a gateway on an ephemeral port (port 0). The credential service is injected over an
        // in-memory store so the test host never reads the developer machine's REAL Windows Data
        // Protection credential blob - without this, the signed-out assertions depend on whether the
        // machine happens to be signed in to DevThrottle.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            // Isolate the device registry to this test's temp dir. Without this the DeviceRegistry falls
            // back to the REAL per-user path and this test's /devices/register calls leak
            // "regression-test-device" rows into the developer's live registry (found 2026-07-04).
            devicesPath: Path.Combine(_instancesDir, "devices.json"),
            // Gateway Cleanup mission (Wave 4b): isolate the Gateway-native mission store to this test's temp
            // dir so /missions calls never touch the developer machine's real missions.json.
            missionsPath: Path.Combine(_instancesDir, "missions.json"),
            account: GatewayAccountFactory.Build(new InMemoryTokenStore()));
        await _gateway.StartAsync();
        _gatewayPort = _gateway.Port;

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gatewayPort}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");

        // Give the FileSystemWatcher a moment to pick up the director
        await WaitForDirectorCount(1, TimeSpan.FromSeconds(5));
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

    [Fact]
    public async Task Healthz_returns_director_and_session_counts()
    {
        var dto = await _http.GetFromJsonAsync<HealthDto>("healthz");
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Status);
        Assert.True(dto.Directors >= 1, $"expected at least 1 director, got {dto.Directors}");
        Assert.Equal(0, dto.Sessions);
    }

    [Fact]
    public async Task Directors_lists_our_director()
    {
        var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
        Assert.NotNull(directors);
        Assert.Contains(directors!, d => d.DirectorId == _director.DirectorId);
    }

    [Fact]
    public async Task Sessions_returns_empty_when_no_sessions()
    {
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>("sessions");
        Assert.NotNull(sessions);
        Assert.Empty(sessions!);
    }

    [Fact]
    public async Task Sessions_unknown_returns_404()
    {
        var resp = await _http.GetAsync($"sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Prompt_requires_auth_when_present()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Prompt_with_wrong_token_returns_401()
    {
        using var wrongClient = new HttpClient { BaseAddress = _http.BaseAddress };
        wrongClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var resp = await wrongClient.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Prompt_with_correct_token_but_a_session_not_located_is_held_not_404()
    {
        // The token is accepted. The session is not located now, and since Voice Delivery phase 5 (contract section 8) a
        // typed prompt to a session the Gateway cannot prove ended is HELD waiting for its Director, never "gone".
        var resp = await _http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("waiting-for-director", body.GetProperty("directorState").GetString());
    }

    [Fact]
    public async Task Delete_unknown_director_returns_404()
    {
        var resp = await _http.DeleteAsync($"directors/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // Gateway Cleanup mission (Wave 4b): the Gateway-native mission surface. Create -> list -> get round-trip
    // over the real loopback Gateway, plus a blank-name 400 and an unknown-id 404. Missions live at the
    // Gateway now (the source of truth), so these routes must serve them like the Director's own /missions.
    [Fact]
    public async Task Missions_create_list_get_roundtrip()
    {
        // Create.
        var createResp = await _http.PostAsJsonAsync("missions", new NewMissionRequest { MissionName = "Gateway Cleanup" });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<MissionDto>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.MissionId);
        Assert.Equal("Gateway Cleanup", created.MissionName);

        // List includes it.
        var list = await _http.GetFromJsonAsync<List<MissionDto>>("missions");
        Assert.NotNull(list);
        Assert.Contains(list!, m => m.MissionId == created.MissionId && m.MissionName == "Gateway Cleanup");

        // Get by id returns the same record.
        var one = await _http.GetFromJsonAsync<MissionDto>($"missions/{created.MissionId}");
        Assert.NotNull(one);
        Assert.Equal(created.MissionId, one!.MissionId);
        Assert.Equal("Gateway Cleanup", one.MissionName);
    }

    [Fact]
    public async Task Missions_create_blank_name_returns_400()
    {
        var resp = await _http.PostAsJsonAsync("missions", new NewMissionRequest { MissionName = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Missions_get_unknown_returns_404()
    {
        var resp = await _http.GetAsync($"missions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Healthz_does_not_require_auth()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Directors_get_requires_auth_now()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("directors");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GatewaySettings_returns_the_per_account_snapshot()
    {
        // Issue #2022: the machine settings left the web page, so this snapshot carries only the per-account
        // settings the collapsed page renders - no process diagnostics, no brain block, no autostart state.
        // The diagnostics relocated to GET /gateway/about (proved by GatewayAbout_returns_runtime_diagnostics).
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        Assert.NotNull(obj);
        Assert.True(obj!.ContainsKey("snoozeDefaultMinutes"));
        Assert.True(obj.ContainsKey("snoozePresets"));
        Assert.True(obj.ContainsKey("timeZone"));
        // The machine fields are GONE - a regression that re-added any of them would redden here.
        Assert.False(obj.ContainsKey("state"));
        Assert.False(obj.ContainsKey("brain"));
        Assert.False(obj.ContainsKey("autostart"));
        Assert.False(obj.ContainsKey("addressingMode"));
    }

    [Fact]
    public async Task GatewayAbout_returns_runtime_diagnostics()
    {
        // Issue #2022: the live process diagnostics the "This machine" tab used to show live read-only on the
        // About page, on both surfaces. Self-hosted, the listen port IS a real reachable port, so it serves.
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/about");
        Assert.NotNull(obj);
        Assert.Equal(_gatewayPort, (int?)obj!["port"]);
        Assert.False(string.IsNullOrEmpty((string?)obj["version"]));
        Assert.Equal("Self-hosted", (string?)obj["deployment"]);
        Assert.True(obj.ContainsKey("uptimeSeconds"));
        Assert.True(obj.ContainsKey("serverTime"));
    }

    [Fact]
    public async Task GatewayAbout_carries_no_director_or_host_internals()
    {
        // Owner ruling 2026-07-26: About is a page about the three SERVER-SIDE products, not a report on the
        // box. The Director has its own About box and its own Cockpit screen, so its facts left this payload -
        // and with them the host internals. This is a PAYLOAD assertion, not a page one: the install root
        // (which spells out the operating-system user name, e.g. C:\Users\soren\AppData\Local\cc-director) was
        // readable by any enrolled device even while the page rendered it, so removing the row alone would not
        // have stopped it leaving the Gateway. A regression that re-adds any of these reddens here.
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/about");
        Assert.NotNull(obj);
        Assert.False(obj!.ContainsKey("installRoot"));
        Assert.False(obj.ContainsKey("machineName"));
        Assert.False(obj.ContainsKey("installedComponents"));
        Assert.False(obj.ContainsKey("directors"));
        Assert.False(obj.ContainsKey("product"));
        Assert.False(obj.ContainsKey("mode"));
        Assert.False(obj.ContainsKey("state"));
    }

    [Fact]
    public async Task GatewayAbout_bundle_stamps_are_either_absent_or_named()
    {
        // The bundle stamps are present exactly when a built bundle is staged in wwwroot, which depends on the
        // build configuration: a Debug test run stages nothing, a Release one stages both. So this asserts the
        // invariant that holds EITHER WAY - a stamp is either absent, or it names a build - rather than a
        // nullness that would pass in Debug and redden on a Release CI run for no real defect. The exact
        // absent/present/corrupt behaviour is proved deterministically in BundleStampTests.
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/about");
        Assert.NotNull(obj);
        foreach (var key in new[] { "cockpit", "mobile" })
        {
            Assert.True(obj!.ContainsKey(key));
            if (obj[key] is JsonObject stamp)
                Assert.False(string.IsNullOrWhiteSpace((string?)stamp["commit"]));
        }
    }

    [Fact]
    public async Task GatewaySettings_get_requires_auth()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("gateway/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Issue #854: the account device-list proxy is wired into the real GatewayHost (near the other
    // /account routes) and gated by the host-wide auth middleware. This test host's credential service
    // is an injected empty in-memory store, so the route is reachable with the gateway token and answers
    // the explicit signed-out envelope - proving the wiring line in GatewayHost.cs, not just the
    // endpoint in isolation.
    [Fact]
    public async Task AccountDevices_isWired_andSignedOutHostReturnsExplicitSignedInFalse()
    {
        var resp = await _http.GetAsync("account/devices");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var obj = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(obj);
        Assert.False((bool?)obj["signedIn"]);
        // Signed out -> no fabricated device list.
        Assert.False(obj.ContainsKey("devices"));
    }

    [Fact]
    public async Task AccountDevices_get_requires_auth()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("account/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // The 4-digit local pairing code and its POST /devices/register route were removed with the
    // Gateway's user interface (the code was only ever shown on the Gateway host's own screen, which a
    // headless Gateway does not have). The route must be GONE, not merely gated: a live-but-gated route
    // answers 401, so only a 404 proves removal.
    //
    // The GET /devices control is what stops this passing for the wrong reason. A 404 alone would also
    // appear if this client simply were not reaching the host - so the sibling route mapped by the same
    // DeviceEnrollmentEndpoint.Map call is asserted to still answer, pinning the 404 to the removal.
    [Fact]
    public async Task PairingRegisterRoute_isRemoved_whileDeviceListingSurvives()
    {
        var gone = await _http.PostAsJsonAsync("devices/register", new { deviceId = "d1", pairingCode = "1234" });
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Control: the sibling route is still mapped and reachable by this same client.
        var alive = await _http.GetAsync("devices");
        Assert.NotEqual(HttpStatusCode.NotFound, alive.StatusCode);
    }

    // POST /account/sign-in was removed with the Gateway's user interface. It ran the browser LOOPBACK
    // sign-in unconditionally - a browser on the GATEWAY HOST's desktop waiting on 127.0.0.1 - so the
    // Cockpit's Sign in button hung forever on any machine but the host. /account/sign-in-start replaced
    // it and branches on the caller, redirecting a remote browser to the cloud instead.
    //
    // Gone, not merely gated: this client authenticates, so a live route would answer something other
    // than 404. The /account/sign-in-start control pins the 404 to the removal rather than to the client
    // failing to reach the host - and asserts the REPLACEMENT is still mapped, so this can never pass by
    // deleting both.
    [Fact]
    public async Task LoopbackSignInRoute_isRemoved_whileTheStartFrontDoorSurvives()
    {
        var gone = await _http.PostAsync("account/sign-in", content: null);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        var alive = await _http.GetAsync("account/sign-in-start");
        Assert.NotEqual(HttpStatusCode.NotFound, alive.StatusCode);
    }

    // ===== Issue #1292: the fleet-wide session-number endpoint over the wire =====

    [Fact]
    public async Task SessionNumberAllocate_GivesDistinctNumbersAcrossDirectors_AndIsIdempotent()
    {
        // Two sessions on two different Directors, both reaching THIS Gateway, get distinct numbers.
        var a = await _http.PostAsJsonAsync("session-numbers/allocate",
            new SessionNumberAllocateRequest { SessionId = "sess-A", DirectorId = "dir-1" });
        var b = await _http.PostAsJsonAsync("session-numbers/allocate",
            new SessionNumberAllocateRequest { SessionId = "sess-B", DirectorId = "dir-2" });
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);

        var na = (await a.Content.ReadFromJsonAsync<SessionNumberAllocateResponse>())!.Number;
        var nb = (await b.Content.ReadFromJsonAsync<SessionNumberAllocateResponse>())!.Number;
        Assert.NotNull(na);
        Assert.NotNull(nb);
        Assert.NotEqual(na, nb);
        // Both come from the coordinated low band (issue #1292 refinement).
        Assert.InRange(na!.Value, Discovery.FleetSessionNumberAllocator.MinNumber, Discovery.FleetSessionNumberAllocator.CoordinatedMaxNumber);

        // Asking again for the SAME session returns the SAME number (idempotent).
        var again = await _http.PostAsJsonAsync("session-numbers/allocate",
            new SessionNumberAllocateRequest { SessionId = "sess-A", DirectorId = "dir-1" });
        var naAgain = (await again.Content.ReadFromJsonAsync<SessionNumberAllocateResponse>())!.Number;
        Assert.Equal(na, naAgain);

        // Releasing frees it for reuse.
        var del = await _http.DeleteAsync("session-numbers/sess-A");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        // Self-host: the endpoint resolves the request to the Local partition, so that is where to look.
        Assert.Null(_gateway.SessionNumbers.NumberFor(Core.Tenancy.TenantId.Local, "sess-A"));
    }

    private async Task WaitForDirectorCount(int target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.Registry.ListDirectors(CcDirector.Gateway.Tenancy.SystemScope.Grant()).Count >= target) return;
            await Task.Delay(100);
        }
        // Don't fail here - tests will fail with clearer assertions if discovery didn't work
    }

}
