using System.Net.Sockets;
using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #506 PROOF: an end-to-end, live-socket demonstration that a single long-lived
/// <see cref="OpenAiKeyResolver"/> (a stand-in for a connected Director) picks up a Gateway-side
/// transcription-mode/URL change with NO restart and NO rebuild, and that the bring-your-own key
/// is never paired with the devthrottle.com URL. Boots the production
/// <see cref="TranscriptionRoutingEndpoint"/> on a real Kestrel port over the production
/// <see cref="KeyVault"/>, exactly as the Gateway hosts it. Output is captured for the proof report.
/// In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class Issue506RoutingProofTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _app = null!;
    private string _baseUrl = null!;
    private string _vaultPath = null!;

    public Issue506RoutingProofTests(ITestOutputHelper output)
    {
        _out = output;
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-506-proof-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-506-proof-" + Guid.NewGuid().ToString("N") + ".json");

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
    public async Task GatewayModeChange_IsPickedUpByConnectedDirector_NoRestart()
    {
        // The connected Director: ONE resolver instance, created once, never reconstructed. Its
        // gateway config points at our live Gateway. This is the "no Director rebuild or restart".
        var gateway = new GatewayConfig { Url = _baseUrl };
        var resolver = new OpenAiKeyResolver(new AgentOptions(), () => gateway);

        // Gateway-side state: seed both keys, start in BYO mode.
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.OpenAiKeyName, "sk-proof-byo");
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_proof");
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);

        var before = await resolver.ResolveEndpointAsync();
        Assert.NotNull(before);
        _out.WriteLine($"[1] Gateway mode=byo -> Director resolved baseUrl={before.BaseUrl}, model={before.Model}, mode={before.Mode}, keyPrefix={before.ApiKey[..3]}");
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, before.BaseUrl);
        Assert.Equal(TranscriptionMode.Byo, before.Mode);
        Assert.StartsWith("sk-", before.ApiKey);
        // BYO key is NEVER paired with the devthrottle URL.
        Assert.DoesNotContain("devthrottle.com", before.BaseUrl);

        // Flip the Gateway's mode. NOTHING about the Director is touched - same resolver instance.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        resolver.InvalidateCache();

        var after = await resolver.ResolveEndpointAsync();
        Assert.NotNull(after);
        _out.WriteLine($"[2] Gateway mode=devthrottle -> SAME Director resolved baseUrl={after.BaseUrl}, model={after.Model}, mode={after.Mode}, keyPrefix={after.ApiKey[..3]}");
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, after.BaseUrl);
        Assert.Equal(TranscriptionMode.DevThrottle, after.Mode);
        Assert.StartsWith("dt_", after.ApiKey);

        _out.WriteLine("[PROOF] One long-lived Director resolver followed the Gateway's mode/URL change with no restart and no rebuild.");
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
