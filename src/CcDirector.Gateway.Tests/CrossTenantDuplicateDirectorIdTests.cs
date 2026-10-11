using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (audit H1, gaps audit-a and audit-b): two tenants can each own a Director with the
/// SAME id - the registry key is (tenant, id), and a Director's id is chosen by the client, so a collision is
/// a legitimate hosted state, not an error. Two hosted read/write paths built their view from the FLEET-GLOBAL
/// <c>DirectorRegistry.ListDirectors()</c> and then keyed by the BARE DirectorId, so a cross-tenant duplicate:
///   - (audit-a) GET /sessions?envelope survived another tenant's DirectorDto into the caller's roster,
///     leaking its machine name and its "unreachable" reachability row; and
///   - (audit-b) POST /sessions/voice-mode/all built a Dictionary keyed by the bare DirectorId, so the
///     duplicate made ToDictionary throw - a 500 in which one tenant's Director denies another tenant's whole
///     voice-mode toggle.
///
/// Both are fixed by reading the registry's TENANT-SCOPED <c>ListDirectors(TenantId)</c> overload, which
/// confines the list to the caller's own partition where ids are unique. This drives the REAL mapped endpoints
/// over real HTTP through the real auth middleware and the real tunnel Hello (which binds each Director's
/// tenant), with two Directors that deliberately share the id "dir-shared".
///
/// Revert-prove: change either handler back to the fleet-global <c>registry.ListDirectors()</c> and the
/// duplicate id reappears - the envelope names "MB-SECRET" again (audit-a) and voice-mode/all throws a
/// duplicate-key 500 again (audit-b) - so the assertions below go RED.
/// </summary>
public sealed class CrossTenantDuplicateDirectorIdTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SharedDirectorId = "dir-shared";
    private const string MachineA = "MA";
    private const string MachineBSecret = "MB-SECRET";
    private const string SessA = "sess-a";
    private const string SessB = "sess-b";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-xt-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts, two canonically minted tenants, and two atomically bound device keys.
        _keyA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", MachineA).DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", MachineBSecret).DeviceKey;

        // The COLLISION: both Directors register under the SAME id, each authenticated with its own device key
        // so the tunnel Hello binds it into its own tenant's partition - (tenant-alice, dir-shared) on MA and
        // (tenant-bob, dir-shared) on MB-SECRET coexist in the registry. dir-a answers the voice-mode verb so
        // the same-tenant leg of voice-mode/all can complete once the duplicate-key 500 is gone.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, SharedDirectorId, MachineA,
            dispatch: cmd => cmd.Verb == "voice-mode"
                ? FakeTunnelDirector.Ok(new { ok = true })
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"));
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, SharedDirectorId, MachineBSecret);
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Sessions_envelope_never_names_another_tenants_director_that_shares_an_id()
    {
        // audit-a: read the ?envelope roster as tenant A. Its OWN session is present; tenant B's Director -
        // which shares the id "dir-shared" - must appear NOWHERE, not as a session and not as a machine name in
        // any reachability / machineError row.
        var resp = await Get("sessions?envelope=true", _keyA);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains(SessA, body);                  // A's own session is present
        Assert.DoesNotContain(SessB, body);            // never B's session
        Assert.DoesNotContain(MachineBSecret, body);   // never B's machine name (the cross-tenant leak marker)
    }

    [Fact]
    public async Task Voice_mode_all_does_not_500_on_a_cross_tenant_duplicate_director_id()
    {
        // audit-b: the fleet-global ListDirectors().ToDictionary(bare id) throws on the duplicate "dir-shared",
        // a 500 that denies tenant A its voice-mode toggle. Scoped to A's partition the id is unique, so the
        // call succeeds.
        var resp = await Post("sessions/voice-mode/all", _keyA, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // And the toggle actually reached A's own (same-tenant) session - proof the success is a real fan-out,
        // not an empty 200 that never looked at the roster.
        var payload = await resp.Content.ReadFromJsonAsync<VoiceModeAllResponse>(JsonOpts);
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Changed);
    }

    private sealed class VoiceModeAllResponse
    {
        public bool Enabled { get; set; }
        public int Total { get; set; }
        public int Changed { get; set; }
        public int Skipped { get; set; }
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> Post(string path, string deviceKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Working",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
