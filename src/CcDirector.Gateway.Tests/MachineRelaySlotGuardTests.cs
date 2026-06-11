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

    // A launcher with a dead port so the guard test never dials out.
    private const string Machine = "GUARD-TEST";
    private const int DeadPort = 1; // never reachable

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        // Register a launcher (dead port - only guard tests that PASS the guard will 502).
        await _http.PostAsJsonAsync("launchers/register", new LauncherRegistrationRequest
        {
            MachineName = Machine,
            Port = DeadPort,
            Token = "relay-token",
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
        // Guard passes -> relay fires -> 502 because the launcher port is dead.
        var resp = await PostRelay("director/restart", exePath, confirmProtected: null);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Theory]
    [InlineData(@"cc-director.exe")]
    [InlineData(@"local_builds\cc-director1.exe")]
    [InlineData(@"local_builds\cc-director4.exe")]
    public async Task SlotGuard_ConfirmProtectedTrue_BypassesGuard(string exePath)
    {
        // confirmProtected=true bypasses the guard -> relay fires -> 502 (dead port).
        var resp = await PostRelay("director/restart", exePath, confirmProtected: true);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task SlotGuard_NoExePath_GuardNotApplied()
    {
        // No exePath -> guard does not run -> relay fires -> 502 (dead port).
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
        // start never kills, so guard is not applied -> 502 (dead port).
        var resp = await PostRelay("director/start", @"cc-director.exe", confirmProtected: null);
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

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
