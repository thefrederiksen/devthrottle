using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Live end-to-end proof for issue #839: boots a REAL <see cref="GatewayHost"/> (the same host the
/// Gateway tray runs) on an ephemeral loopback port with isolated dirs and an isolated key-vault
/// file, then drives the single speech-to-text endpoint <c>POST /transcription</c> over real HTTP
/// through the whole host pipeline (routing + the single <c>GatewayTranscriptionService</c>).
///
/// This proves the consolidation on a running instance, not just in a unit harness:
///   * with no key for the current remote mode, the endpoint answers 409 with the mode - the exact
///     "no key set" condition that made a recorded note fail even when a key was present; and
///   * the routing endpoint a connected Director consumes resolves through the SAME single owner.
/// Tailscale is disabled via TestEnvironment so it never touches a running Gateway.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayTranscriptionLiveProofTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly string? _prevRoot;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-839-live-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-839-live-inst-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyVaultPath =
        Path.Combine(Path.GetTempPath(), "cc-839-live-vault-" + Guid.NewGuid().ToString("N") + ".json");

    public GatewayTranscriptionLiveProofTests()
    {
        // Isolate the transcription_mode config (TranscriptionModeConfig reads CcStorage, which honors
        // CC_DIRECTOR_ROOT) so this test owns the mode and never reads the real machine config.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: "", authEnabled: false,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1, keyVaultPath: _keyVaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_keyVaultPath)) File.Delete(_keyVaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, recursive: true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task PostTranscription_RemoteModeNoKey_Returns409_ThroughRealHost()
    {
        // DevThrottle mode: its key (DEVTHROTTLE_API_KEY) is never seeded from the environment (only
        // OPENAI_API_KEY is), so the no-key 409 gate is deterministic regardless of the host machine's
        // OPENAI_API_KEY. This is the exact "no key set for the current transcription mode" condition
        // the single endpoint now answers consistently for every batch caller.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);

        using var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
        var resp = await _http.PostAsync("transcription", content);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("devthrottle", doc.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RoutingEndpoint_ResolvesThroughSameOwner_ReturnsKeyOnceSet()
    {
        // The single owner reads the key from the vault: set it via the vault HTTP surface, then the
        // routing endpoint (what a connected Director consumes) composes URL + key server-side.
        await _http.PutAsJsonAsync("vault/keys/OPENAI_API_KEY", new { value = "sk-live-proof" });
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);

        var resp = await _http.GetAsync("transcription/routing");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("byo", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("https://api.openai.com/v1", doc.RootElement.GetProperty("baseUrl").GetString());
        Assert.Equal("sk-live-proof", doc.RootElement.GetProperty("key").GetString());
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
