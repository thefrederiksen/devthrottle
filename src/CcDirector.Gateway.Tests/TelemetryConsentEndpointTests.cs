using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the Gateway's fleet-wide telemetry-consent endpoints (issue #649),
/// <c>GET/PUT /gateway/telemetry-consent</c>. Boots only <see cref="TelemetryConsentEndpoint"/> on an
/// ephemeral port with a temp CC_DIRECTOR_ROOT so each test owns an isolated config.json. Proves the
/// authoritative consent defaults ON, persists OFF on the Gateway, is read back after a fresh process
/// (re-reading config.json from disk, the across-restart guarantee), and rejects a non-object body.
/// In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TelemetryConsentEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public TelemetryConsentEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-consent-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        TelemetryConsentEndpoint.Map(_app);
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Get_consent_defaults_on_when_never_set()
    {
        // No config.json key written -> the authoritative default is ON.
        var dto = await _http.GetFromJsonAsync<JsonObject>("gateway/telemetry-consent");
        Assert.NotNull(dto);
        Assert.True((bool)dto!["enabled"]!);
    }

    [Fact]
    public async Task Put_consent_off_persists_and_get_reflects_it()
    {
        var resp = await _http.PutAsJsonAsync("gateway/telemetry-consent", new { enabled = false });
        resp.EnsureSuccessStatusCode();
        var echoed = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False((bool)echoed!["enabled"]!);

        // Persisted on the Gateway (config.json), not just in the response.
        Assert.False(TelemetryConsentConfig.Get());

        // And a GET reflects the persisted OFF value.
        var dto = await _http.GetFromJsonAsync<JsonObject>("gateway/telemetry-consent");
        Assert.False((bool)dto!["enabled"]!);
    }

    [Fact]
    public async Task Put_consent_off_survives_a_fresh_read_from_disk()
    {
        // Turn it off via the endpoint.
        var resp = await _http.PutAsJsonAsync("gateway/telemetry-consent", new { enabled = false });
        resp.EnsureSuccessStatusCode();

        // The across-restart guarantee: config.json on disk carries the value, so any fresh process
        // (a restarted Gateway) that re-reads config.json sees OFF. We prove it by reading the raw file.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.False((bool)onDisk[TelemetryConsentConfig.Key]!);

        // Turning it back on persists the new value the same way.
        var back = await _http.PutAsJsonAsync("gateway/telemetry-consent", new { enabled = true });
        back.EnsureSuccessStatusCode();
        Assert.True(TelemetryConsentConfig.Get());
    }

    [Fact]
    public async Task Put_consent_rejects_non_object_body()
    {
        var content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync("gateway/telemetry-consent", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
