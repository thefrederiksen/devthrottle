using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #506: HTTP wire test for the Gateway's <c>GET /transcription/routing</c> endpoint. Boots
/// only <see cref="TranscriptionRoutingEndpoint"/> on an ephemeral port with a temp-file vault and
/// a temp CC_DIRECTOR_ROOT (so the test owns the transcription_mode config). Proves the Gateway
/// composes the correct (mode, baseUrl, model, key) pair per mode server-side, and - the
/// security-critical invariant - NEVER returns the bring-your-own OpenAI key with the devthrottle.com
/// URL (or vice versa). In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TranscriptionRoutingEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _vaultPath = null!;

    public TranscriptionRoutingEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-routing-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-routing-" + Guid.NewGuid().ToString("N") + ".json");

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        TranscriptionRoutingEndpoint.Map(_app, new KeyVault(_vaultPath));
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void SeedVault(string name, string value) => new KeyVault(_vaultPath).Set(name, value);

    [Fact]
    public async Task Routing_ByoMode_ComposesOpenAiPair()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        SeedVault(TranscriptionEndpointResolver.OpenAiKeyName, "sk-byo-123");

        var resp = await _http.GetAsync("/transcription/routing");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("byo", root.GetProperty("mode").GetString());
        // Issue #513: BYO carries the realtime transport + the OpenAI model.
        Assert.Equal("realtime", root.GetProperty("transport").GetString());
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, root.GetProperty("baseUrl").GetString());
        Assert.Equal(TranscriptionEndpointResolver.OpenAiModel, root.GetProperty("model").GetString());
        Assert.Equal("gpt-4o-transcribe", root.GetProperty("model").GetString());
        Assert.Equal("sk-byo-123", root.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Routing_DevThrottleMode_ComposesDevThrottlePair()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        SeedVault(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_xyz");

        var resp = await _http.GetAsync("/transcription/routing");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("devthrottle", root.GetProperty("mode").GetString());
        // Issue #513: DevThrottle carries the batch transport + the provider-correct Groq model.
        Assert.Equal("batch", root.GetProperty("transport").GetString());
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, root.GetProperty("baseUrl").GetString());
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleModel, root.GetProperty("model").GetString());
        Assert.Equal("whisper-large-v3", root.GetProperty("model").GetString());
        Assert.Equal("dt_live_xyz", root.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Routing_ByoMode_NeverPairsByoKeyWithDevThrottleUrl()
    {
        // The product's hard rule, now enforced server-side: the user's own OpenAI key is only ever
        // paired with the OpenAI URL. Seed BOTH keys so a wrong cross-pairing would be observable.
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        SeedVault(TranscriptionEndpointResolver.OpenAiKeyName, "sk-secret-byo");
        SeedVault(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_other");

        var resp = await _http.GetAsync("/transcription/routing");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.DoesNotContain("devthrottle.com", root.GetProperty("baseUrl").GetString());
        Assert.Equal("sk-secret-byo", root.GetProperty("key").GetString());
        // The DevThrottle key must not leak into a BYO response at all.
        Assert.DoesNotContain("dt_live_other", body);
    }

    [Fact]
    public async Task Routing_KeyNotSet_Returns404_WithMarkerHeader()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        // No key seeded.
        var resp = await _http.GetAsync("/transcription/routing");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        // The marker header is present so a Director can tell this from an older Gateway's framework 404.
        Assert.True(resp.Headers.Contains("X-Transcription-Routing"));
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
