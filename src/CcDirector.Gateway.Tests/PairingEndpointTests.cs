using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Network;
using CcDirector.Gateway;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Integration tests for the phone-pairing endpoints (issue #385): GET /pair/qr.png and
/// GET /pair/payload on a real in-process GatewayHost. Auth and the no-fallback contract are
/// asserted deterministically; the success path is asserted CONDITIONALLY on whether this host
/// has a Tailscale front door (CI / no-Tailscale machines exercise the 503 no-fallback path,
/// dev machines with Tailscale exercise the 200 image/png path), so the suite is green either way.
/// The QR's pixel-level decode is proven live in the issue's HTML report.
/// </summary>
public sealed class PairingEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-12345";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-pair-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task QrPng_without_token_returns_401()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anon.GetAsync("pair/qr.png");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Payload_without_token_returns_401()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anon.GetAsync("pair/payload");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task QrPng_returns_png_or_clear_503_naming_the_front_door()
    {
        var frontDoor = TailscaleIdentity.TryGetFrontDoorBaseUrl();
        var resp = await _http.GetAsync("pair/qr.png");

        if (frontDoor is null)
        {
            // No-fallback rule (criterion 3): a missing front door is a clear error, NOT a QR.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(body);
            Assert.Contains("front-door", (string?)body!["error"], StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 0);
            // PNG magic number - proves it really is a PNG.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        }
    }

    [Fact]
    public async Task Payload_returns_the_builder_contract_or_clear_503()
    {
        var frontDoor = TailscaleIdentity.TryGetFrontDoorBaseUrl();
        var resp = await _http.GetAsync("pair/payload");

        if (frontDoor is null)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body);
        Assert.Equal(frontDoor, (string?)body!["url"]);
        Assert.Equal(Token, (string?)body["token"]);
        // The payload field is exactly what the pure builder produces for these two inputs.
        Assert.Equal(PairingPayload.Build(frontDoor, Token), (string?)body["payload"]);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
