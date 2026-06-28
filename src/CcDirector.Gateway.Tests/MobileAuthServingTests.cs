using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #806 (mobile foundation), AC5: the mobile app renders with global Gateway auth either
/// on or off. With auth ON, the app shell at /m loads WITHOUT a credential (it carries the
/// injected token, not a secret), while the data endpoint /sessions stays Bearer-gated - so the
/// injected token is exactly what makes the roster load. This boots a Gateway with auth ON and
/// proves both halves.
/// </summary>
public sealed class MobileAuthServingTests : IAsyncLifetime
{
    private const string Token = "test-token-806";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task Sessions_requires_bearer_when_auth_is_on()
    {
        using var res = await _http.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Sessions_returns_200_with_the_injected_bearer()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Mobile_shell_is_public_and_not_login_gated_when_auth_is_on()
    {
        // The /m shell is exempt from the global gate, so it reaches the mobile handler instead of
        // being 302-redirected to /login. In a Debug test build the app is not staged into
        // wwwroot/m, so the handler answers 404 - which still proves it was NOT auth-gated.
        using var res = await _http.GetAsync("/m");
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
