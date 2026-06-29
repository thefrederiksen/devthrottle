using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #513 PROOF: a live-socket demonstration that the Gateway's production
/// <c>GET /transcription/routing</c> serves the TRANSPORT and the PROVIDER-CORRECT MODEL per mode,
/// and that a connected Director's resolver (<see cref="OpenAiKeyResolver"/>) consumes exactly that
/// tuple:
///   - DevThrottle -> (transport=batch, model=whisper-large-v3, baseUrl=devthrottle.com)
///   - BYO         -> (transport=realtime, model=gpt-4o-transcribe, baseUrl=api.openai.com)
/// Boots the production <see cref="TranscriptionRoutingEndpoint"/> on a real Kestrel port over the
/// production <see cref="KeyVault"/>, exactly as the Gateway hosts it. Output is captured for the
/// proof report. In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class Issue513TransportRoutingProofTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _app = null!;
    private string _baseUrl = null!;
    private string _vaultPath = null!;

    public Issue513TransportRoutingProofTests(ITestOutputHelper output)
    {
        _out = output;
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-513-proof-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-513-proof-" + Guid.NewGuid().ToString("N") + ".json");

        var port = AllocateFreePort();
        _baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(_baseUrl);
        TranscriptionRoutingEndpoint.Map(_app, new KeyVault(_vaultPath));
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Routing_ServesTransportAndProviderCorrectModel_PerMode()
    {
        // Seed both keys so a wrong cross-pairing would be observable.
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.OpenAiKeyName, "sk-proof-byo");
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_proof");

        var gateway = new GatewayConfig { Url = _baseUrl };
        var resolver = new OpenAiKeyResolver(() => gateway);

        // DevThrottle mode: batch transport + whisper-large-v3 + devthrottle.com.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        var dt = await resolver.ResolveEndpointAsync();
        Assert.NotNull(dt);
        _out.WriteLine($"[1] mode=devthrottle -> Director resolved transport={dt.Transport.ToConfigString()}, "
                       + $"model={dt.Model}, baseUrl={dt.BaseUrl}, keyPrefix={dt.ApiKey[..3]}");
        Assert.Equal(TranscriptionTransport.Batch, dt.Transport);
        Assert.Equal("whisper-large-v3", dt.Model);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, dt.BaseUrl);
        Assert.StartsWith("dt_", dt.ApiKey);

        // BYO mode: realtime transport + gpt-4o-transcribe + api.openai.com. Same resolver instance.
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        resolver.InvalidateCache();
        var byo = await resolver.ResolveEndpointAsync();
        Assert.NotNull(byo);
        _out.WriteLine($"[2] mode=byo -> SAME Director resolved transport={byo.Transport.ToConfigString()}, "
                       + $"model={byo.Model}, baseUrl={byo.BaseUrl}, keyPrefix={byo.ApiKey[..3]}");
        Assert.Equal(TranscriptionTransport.Realtime, byo.Transport);
        Assert.Equal("gpt-4o-transcribe", byo.Model);
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, byo.BaseUrl);
        Assert.StartsWith("sk-", byo.ApiKey);

        // The never-cross invariant holds: DevThrottle never gets the sk- key or the OpenAI URL.
        Assert.DoesNotContain("api.openai.com", dt.BaseUrl);
        Assert.DoesNotContain("devthrottle.com", byo.BaseUrl);

        _out.WriteLine("[PROOF] Gateway serves transport + provider-correct model per mode; "
                       + "the Director honors batch/whisper-large-v3 for DevThrottle and "
                       + "realtime/gpt-4o-transcribe for BYO, with no key/URL cross-pairing.");
    }

    [Fact]
    public async Task Routing_RawJson_IncludesTransportField()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_raw");

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        var resp = await http.GetAsync("/transcription/routing");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"[RAW] GET /transcription/routing -> {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("batch", root.GetProperty("transport").GetString());
        Assert.Equal("whisper-large-v3", root.GetProperty("model").GetString());
        Assert.Equal("devthrottle", root.GetProperty("mode").GetString());
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
