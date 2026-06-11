using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the launcher registration surface:
///   POST /launchers/register
///   POST /launchers/{machine}/heartbeat
///   DELETE /launchers/{machine}
///   GET  /launchers
///
/// And the machine relay routes:
///   POST /machines/{machine}/director/restart|start|stop
///   POST /machines/{machine}/launch
///
/// Issue #331.
/// </summary>
public sealed class LauncherRegistryEndpointTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-launcher-test-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    // -------------------------------------------------------------------------
    // AC1: Launcher registration (POST /launchers/register)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Register_Launcher_Returns201AndAppearsInList()
    {
        var req = BuildRegistrationRequest("MACHINE-A", port: 7900);

        var resp = await _http.PostAsJsonAsync("launchers/register", req);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        Assert.NotNull(list);
        var entry = Assert.Single(list!, l => l.MachineName.Equals("MACHINE-A", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7900, entry.Port);
        Assert.Equal("1.0.0", entry.Version);
    }

    [Fact]
    public async Task Register_Launcher_IsIdempotent()
    {
        var req = BuildRegistrationRequest("MACHINE-B", port: 7901, version: "1.0.0");
        await _http.PostAsJsonAsync("launchers/register", req);

        var req2 = BuildRegistrationRequest("MACHINE-B", port: 7901, version: "1.0.1");
        var resp = await _http.PostAsJsonAsync("launchers/register", req2);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        var entries = list!.Where(l => l.MachineName.Equals("MACHINE-B", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(entries);
        Assert.Equal("1.0.1", entries[0].Version);
    }

    [Fact]
    public async Task Register_Launcher_RejectsMissingMachineName()
    {
        var req = new LauncherRegistrationRequest { MachineName = "", Port = 7900, Token = "tok" };
        var resp = await _http.PostAsJsonAsync("launchers/register", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Launcher_RejectsZeroPort()
    {
        var req = new LauncherRegistrationRequest { MachineName = "MACHINE-C", Port = 0, Token = "tok" };
        var resp = await _http.PostAsJsonAsync("launchers/register", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Launcher_RejectsMissingToken()
    {
        var req = new LauncherRegistrationRequest { MachineName = "MACHINE-D", Port = 7900, Token = "" };
        var resp = await _http.PostAsJsonAsync("launchers/register", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_KnownLauncher_Returns200()
    {
        var req = BuildRegistrationRequest("MACHINE-HB");
        await _http.PostAsJsonAsync("launchers/register", req);

        var resp = await _http.PostAsync("launchers/MACHINE-HB/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_UnknownLauncher_Returns410()
    {
        var resp = await _http.PostAsync("launchers/NONEXISTENT/heartbeat", null);
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Unregister
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unregister_RemovesLauncherFromList()
    {
        var req = BuildRegistrationRequest("MACHINE-DEL");
        await _http.PostAsJsonAsync("launchers/register", req);

        var beforeCount = (await _http.GetFromJsonAsync<List<LauncherDto>>("launchers"))!.Count;
        Assert.True(beforeCount >= 1);

        var delResp = await _http.DeleteAsync("launchers/MACHINE-DEL");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        Assert.DoesNotContain(list!, l => l.MachineName.Equals("MACHINE-DEL", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // AC3: Slot guard (relay refuses main + slots 1-4 without confirmProtected)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_SlotGuard_RefusesMainBuildWithoutConfirm()
    {
        var req = BuildRegistrationRequest("GUARD-MACHINE", port: 7999);
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\Program Files\cc-director\cc-director.exe" };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE/director/restart", body);

        // 403 = slot guard fired.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("slot_guard", json);
    }

    [Fact]
    public async Task Relay_SlotGuard_RefusesSlot1WithoutConfirm()
    {
        var req = BuildRegistrationRequest("GUARD-MACHINE2", port: 7998);
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\cc-director\local_builds\cc-director1.exe" };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE2/director/stop", body);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("slot_guard", json);
    }

    [Fact]
    public async Task Relay_SlotGuard_AllowsSlot5WithoutConfirm()
    {
        // Slot 5 (agent slot) is NOT protected. The launcher is not actually listening,
        // so we expect a 502 upstream-unreachable (not 403 from the guard).
        var req = BuildRegistrationRequest("GUARD-MACHINE3", port: 1); // dead port
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\cc-director\local_builds\cc-director5.exe" };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE3/director/restart", body);

        // 502 = guard passed, but launcher is not reachable on port 1.
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("slot_guard", json);
    }

    [Fact]
    public async Task Relay_SlotGuard_AllowsMainBuildWithConfirmProtected()
    {
        // With confirmProtected=true the guard is bypassed. Expect 502 (not 403).
        var req = BuildRegistrationRequest("GUARD-MACHINE4", port: 1); // dead port
        await _http.PostAsJsonAsync("launchers/register", req);

        var body = new { exePath = @"C:\Program Files\cc-director\cc-director.exe", confirmProtected = true };
        var resp = await _http.PostAsJsonAsync("machines/GUARD-MACHINE4/director/restart", body);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC5: 404 when launcher not registered
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_UnknownMachine_Returns404()
    {
        var resp = await _http.PostAsync("machines/UNKNOWN-MACHINE/director/restart", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("UNKNOWN-MACHINE", json);
    }

    [Fact]
    public async Task Relay_Launch_UnknownMachine_Returns404()
    {
        var resp = await _http.PostAsJsonAsync("machines/UNKNOWN-MACHINE/launch", new { path = "foo.exe" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC5: launcher unreachable -> 502 (not a hang)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Relay_LauncherUnreachable_Returns502()
    {
        // Register with a dead port (1 is never bound by the launcher in tests).
        var req = BuildRegistrationRequest("UNREACHABLE-MACHINE", port: 1);
        await _http.PostAsJsonAsync("launchers/register", req);

        var resp = await _http.PostAsync("machines/UNREACHABLE-MACHINE/director/restart", null);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unreachable", json);
    }

    // -------------------------------------------------------------------------
    // AC2: GET /launchers lists machine + port + last-seen
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetLaunchers_ReturnsAllRegistered()
    {
        await _http.PostAsJsonAsync("launchers/register", BuildRegistrationRequest("LIST-A", port: 7910));
        await _http.PostAsJsonAsync("launchers/register", BuildRegistrationRequest("LIST-B", port: 7911));

        var list = await _http.GetFromJsonAsync<List<LauncherDto>>("launchers");
        Assert.NotNull(list);
        Assert.Contains(list!, l => l.MachineName.Equals("LIST-A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list!, l => l.MachineName.Equals("LIST-B", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Auth (AC5: wrong token -> 401)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Register_Launcher_WrongToken_Returns401()
    {
        using var badHttp = new HttpClient { BaseAddress = _http.BaseAddress };
        badHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "WRONG");

        var resp = await badHttp.PostAsJsonAsync("launchers/register",
            BuildRegistrationRequest("TOKEN-TEST"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static LauncherRegistrationRequest BuildRegistrationRequest(
        string machine, int port = 7900, string version = "1.0.0") =>
        new()
        {
            MachineName = machine,
            Port = port,
            Token = "launcher-token-" + machine,
            Pid = 12345,
            Version = version,
            StartedAt = DateTime.UtcNow,
        };

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
