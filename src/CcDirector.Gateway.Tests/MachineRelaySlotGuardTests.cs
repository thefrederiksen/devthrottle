using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the slot-guard matrix in the machine relay:
///   - main build (cc-director.exe)     -> REFUSED without confirmProtected
///   - slots 1-4 (cc-director[1-4].exe) -> REFUSED without confirmProtected
///   - slot 5+ (cc-director5+.exe)      -> ALLOWED without confirm (agent slots)
///   - confirmProtected=true             -> bypasses guard for main + 1-4
///   - no exePath in body               -> guard is not applied (launcher decides)
///
/// Issue #331.
/// </summary>
public sealed class MachineRelaySlotGuardTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-slotguard-test-" + Guid.NewGuid().ToString("N"));

    // A registered launcher with NO stream connection: a request that passes the guard ends in the
    // not-connected 502 refusal, which is the proof the guard was what decided (phase 6 deleted the
    // REST dial-out, so there is no port to be dead).
    private const string Machine = "GUARD-TEST";

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        // Register a launcher (no stream joined - only guard tests that PASS the guard will 502).
        await _http.PostAsJsonAsync("launchers/register", new LauncherRegistrationRequest
        {
            MachineName = Machine,
            Pid = 1,
            Version = "1.0.0",
            StartedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    // -------------------------------------------------------------------------
    // Matrix: paths that trigger the guard
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(@"cc-director.exe")]                           // main, no path
    [InlineData(@"C:\Program Files\cc-director\cc-director.exe")] // main with path
    [InlineData(@"local_builds\cc-director1.exe")]             // slot 1
    [InlineData(@"local_builds\cc-director2.exe")]             // slot 2
    [InlineData(@"local_builds\cc-director3.exe")]             // slot 3
    [InlineData(@"local_builds\cc-director4.exe")]             // slot 4
    public async Task SlotGuard_ProtectedPaths_RefuseWithout_ConfirmFlag(string exePath)
    {
        var resp = await PostRelay("director/restart", exePath, confirmProtected: null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("slot_guard", body);
    }

    [Theory]
    [InlineData(@"local_builds\cc-director5.exe")]    // slot 5 = first agent slot
    [InlineData(@"local_builds\cc-director6.exe")]    // slot 6
    [InlineData(@"local_builds\cc-director99.exe")]   // high slot
    public async Task SlotGuard_AgentPaths_AllowWithout_ConfirmFlag(string exePath)
    {
        // Guard passes -> dispatch is attempted -> 502 because no stream is connected.
        var resp = await PostRelay("director/restart", exePath, confirmProtected: null);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Theory]
    [InlineData(@"cc-director.exe")]
    [InlineData(@"local_builds\cc-director1.exe")]
    [InlineData(@"local_builds\cc-director4.exe")]
    public async Task SlotGuard_ConfirmProtectedTrue_BypassesGuard(string exePath)
    {
        // confirmProtected=true bypasses the guard -> dispatch is attempted -> 502 (not connected).
        var resp = await PostRelay("director/restart", exePath, confirmProtected: true);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task SlotGuard_NoExePath_GuardNotApplied()
    {
        // No exePath -> guard does not run -> dispatch is attempted -> 502 (not connected).
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart", null);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Guard also applies to /stop (not just /restart)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SlotGuard_AppliesTo_Stop()
    {
        var resp = await PostRelay("director/stop", @"cc-director.exe", confirmProtected: null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task SlotGuard_DoesNotApplyTo_Start()
    {
        // start never kills, so guard is not applied -> 502 (not connected).
        var resp = await PostRelay("director/start", @"cc-director.exe", confirmProtected: null);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // onlyIfEmpty belongs to restart, and is REFUSED elsewhere rather than ignored
    // -------------------------------------------------------------------------

    /// <summary>
    /// A caller sending onlyIfEmpty to stop is asking not to interrupt live work. Dropping the flag and
    /// stopping the Director anyway would answer that request with the exact outcome it was trying to
    /// prevent - and report success. So it is a 400 that says the flag was not applied and nothing was
    /// done.
    /// </summary>
    [Theory]
    [InlineData("director/stop")]
    [InlineData("director/start")]
    public async Task OnlyIfEmpty_OnAnythingButRestart_IsRefusedRatherThanIgnored(string verb)
    {
        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/{verb}", new { onlyIfEmpty = true });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("only_if_empty_not_supported", body);
    }

    /// <summary>
    /// And on restart it passes: the request reaches dispatch, which here ends in the not-connected 502
    /// because this rig registers a launcher that holds no stream. A 400 would mean the flag never got
    /// past the route at all.
    /// </summary>
    [Fact]
    public async Task OnlyIfEmpty_OnRestart_ReachesDispatch()
    {
        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = true });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostRelay(string verb, string? exePath, bool? confirmProtected)
    {
        object? body = exePath is null
            ? null
            : (object)new { exePath, confirmProtected };

        return body is null
            ? await _http.PostAsync($"machines/{Machine}/{verb}", null)
            : await _http.PostAsJsonAsync($"machines/{Machine}/{verb}", body);
    }

}
