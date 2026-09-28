using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the consolidated DevThrottle AI-provider surface and the TTS-voice endpoint.
/// Boots a real GatewayHost on an
/// ephemeral port with CC_DIRECTOR_ROOT redirected to a temp dir, so writes round-trip an isolated
/// config.json rather than the user's real one.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class AiProviderEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-aiprov-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public AiProviderEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-aiprov-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Get_ai_provider_defaults_to_devthrottle_with_included_wingman_ids()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.NotNull(obj);
        Assert.Equal("devthrottle", (string?)obj!["provider"]);
        Assert.Equal("devthrottle/wingman", (string?)obj["wingmanModel"]);
        Assert.Equal("devthrottle/wingman-fast", (string?)obj["wingmanFastModel"]);
        Assert.Equal("whisper-large-v3", (string?)obj["transcriptionModel"]);
        Assert.Equal("af_bella", (string?)obj["ttsVoice"]);   // DevThrottle (Kokoro) default voice
        var voices = obj["voices"] as JsonArray;
        Assert.NotNull(voices);
        Assert.Contains(voices!, v => (string?)v == "nova");   // fallback set when catalog is unavailable
    }

    [Fact]
    public async Task Put_ai_provider_openai_is_rejected()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "openai" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(CcDirectorConfigService.ReadRaw()["transcription_mode"]);
    }

    [Fact]
    public async Task Put_ai_provider_devthrottle_restores_hosted_defaults()
    {
        var mode = TranscriptionModeConfig.Get();
        // Seed distinctive per-tenant overrides the reset must move away from (issue #2017: the AI tab writes
        // per tenant, not to config.json).
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantId.Local, WingmanModelRole.Thinking, "sentinel-think", DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantId.Local, WingmanModelRole.Fast, "sentinel-fast", DateTime.UtcNow);

        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "devthrottle" });
        resp.EnsureSuccessStatusCode();

        // transcription_mode is a single-valued global provider fact, still written to config.json.
        Assert.Equal("devthrottle", (string?)CcDirectorConfigService.ReadRaw()["transcription_mode"]);
        // The reset CLEARS this tenant's model overrides, so the resolver falls back to the operator hosted
        // defaults - the sentinels are gone.
        Assert.Equal(WingmanModelConfig.Resolve(mode, WingmanModelRole.Thinking),
            _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Thinking));
        Assert.Equal(WingmanModelConfig.Resolve(mode, WingmanModelRole.Fast),
            _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Fast));
        Assert.NotEqual("sentinel-think", _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Thinking).Value);
    }

    [Fact]
    public async Task Put_ai_provider_rejects_unknown_provider()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "anthropic" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // Nothing written.
        Assert.Null(CcDirectorConfigService.ReadRaw()["transcription_mode"]);
    }

    [Fact]
    public async Task Put_ai_provider_preserves_unrelated_config_sections()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://gw.example:7878" },
        });

        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "devthrottle" });
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://gw.example:7878", (string?)onDisk["gateway"]!["url"]);
        Assert.Equal("devthrottle", (string?)onDisk["transcription_mode"]);
    }

    [Fact]
    public async Task Get_tts_voice_defaults_to_provider_default_with_fallback_set()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/tts-voice");
        Assert.Equal("af_bella", (string?)obj!["voice"]);   // DevThrottle (Kokoro) default
        var voices = obj["voices"] as JsonArray;
        Assert.NotNull(voices);
        Assert.Contains(voices!, v => (string?)v == "shimmer");   // fallback set
    }

    [Fact]
    public async Task Put_tts_voice_persists_and_round_trips()
    {
        var resp = await _http.PutAsJsonAsync("gateway/tts-voice", new { voice = "onyx" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("onyx", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["voice"]);

        // Issue #2017: the voice persists to the per-tenant store (this Gateway's local tenant), not the
        // process-global config.json, so the write is read back through the resolver.
        Assert.Equal("onyx", _gateway.TenantSettingsResolver.TtsVoice(TenantId.Local, TranscriptionModeConfig.Get()));
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/tts-voice");
        Assert.Equal("onyx", (string?)obj!["voice"]);
    }

    [Fact]
    public async Task Put_tts_voice_accepts_any_voice_id()
    {
        // Voices are dynamic, so any non-empty id is accepted - there is no fixed allow-list.
        var resp = await _http.PutAsJsonAsync("gateway/tts-voice", new { voice = "af_bella" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("af_bella", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["voice"]);
        // Per-tenant store (issue #2017), read back through the resolver for the local tenant.
        Assert.Equal("af_bella", _gateway.TenantSettingsResolver.TtsVoice(TenantId.Local, TranscriptionModeConfig.Get()));
    }

    [Fact]
    public async Task Put_tts_voice_rejects_empty()
    {
        var resp = await _http.PutAsJsonAsync("gateway/tts-voice", new { voice = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_tts_voice_rejects_non_object_body()
    {
        var content = new StringContent("\"nova\"", Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync("gateway/tts-voice", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

}
