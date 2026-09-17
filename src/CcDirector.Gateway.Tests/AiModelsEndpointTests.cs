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
/// End-to-end proof for the AI model catalog + test surface (the Settings AI tab's model/voice pickers).
/// Boots a real GatewayHost on an ephemeral port with an isolated CC_DIRECTOR_ROOT. The list + test-chat
/// routes need a live provider, so here we prove: the model setters persist and round-trip through the
/// /gateway/ai-provider snapshot, and the list route reports "not signed in" (503) with no key - never a
/// crash or a silent empty list.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class AiModelsEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-aimodels-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public AiModelsEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-aimodels-test-" + Guid.NewGuid().ToString("N"));
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
    public async Task Put_wingman_model_persists_and_snapshot_reflects_it()
    {
        // The FAST id on the THINKING role: an included id (the setter honors nothing else since issue
        // #1360) that differs from the role's default, so the re-read proves a write, not a no-op.
        var resp = await _http.PutAsJsonAsync("gateway/ai/wingman-model", new { model = "devthrottle/wingman-fast" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("devthrottle/wingman-fast", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["model"]);

        // Issue #2017: the model now persists to the per-tenant store, not config.json. Read it back directly
        // from the resolver for the self-host local tenant (an independent store re-read), then confirm the
        // snapshot reflects it too.
        Assert.Equal("devthrottle/wingman-fast", _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, TranscriptionModeConfig.Get(), WingmanModelRole.Thinking).Value);
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("devthrottle/wingman-fast", (string?)snap!["wingmanModel"]);
    }

    [Fact]
    public async Task Put_wingman_fast_model_persists_and_snapshot_reflects_it()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai/wingman-fast-model", new { model = "devthrottle/wingman" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("devthrottle/wingman", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["model"]);

        Assert.Equal("devthrottle/wingman", _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, TranscriptionModeConfig.Get(), WingmanModelRole.Fast).Value);
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("devthrottle/wingman", (string?)snap!["wingmanFastModel"]);
    }

    [Theory]
    [InlineData("gateway/ai/wingman-model")]
    [InlineData("gateway/ai/wingman-fast-model")]
    public async Task Put_model_setters_refuse_catalog_ids(string route)
    {
        // Included AI revert-proof (issue #1360): the wingman is an internal included
        // feature, and a catalog id would bill credits - the setter must refuse it loudly, never
        // store it. Put the old accept-anything setter back and this goes red.
        var resp = await _http.PutAsJsonAsync(route, new { model = "kimi-k2" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // And nothing was stored: the resolver still answers the included defaults.
        var mode = TranscriptionModeConfig.Get();
        Assert.Equal("devthrottle/wingman", _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Thinking).Value);
        Assert.Equal("devthrottle/wingman-fast", _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Fast).Value);
    }

    [Fact]
    public async Task Put_tts_model_persists_and_snapshot_reflects_it()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai/tts-model", new { model = "kokoro" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("kokoro", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["model"]);

        Assert.Equal("kokoro", _gateway.TenantSettingsResolver.TtsModel(TenantId.Local, TranscriptionModeConfig.Get()));
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("kokoro", (string?)snap!["ttsModel"]);
    }

    [Fact]
    public async Task Snapshot_defaults_wingman_and_tts_model()
    {
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("devthrottle/wingman", (string?)snap!["wingmanModel"]);   // included default when unset
        Assert.Equal("devthrottle/wingman-fast", (string?)snap["wingmanFastModel"]);
        Assert.Equal("hexgrad/Kokoro-82M", (string?)snap["ttsModel"]);
    }

    [Fact]
    public async Task Get_chat_models_serves_only_the_included_wingman_ids_and_never_the_catalog()
    {
        // Included AI revert-proof (issue #1360, design C3): the chat kind feeds the wingman pickers,
        // which must never offer catalog models. NO provider key is stored in this rig, so this list
        // arriving at all also proves no upstream catalog call is involved - put the old
        // relay-the-catalog branch back and this answers 503 instead.
        var resp = await _http.GetAsync("gateway/ai/models?kind=chat");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>();
        var ids = (body!["models"] as JsonArray)!.Select(m => (string?)m!["id"]).ToList();
        Assert.Equal(new[] { "devthrottle/wingman", "devthrottle/wingman-fast" }, ids);
    }

    [Fact]
    public async Task Get_speech_models_without_key_reports_not_signed_in()
    {
        // The SPEECH kind still reads the live catalog: no DevThrottle key stored -> the catalog cannot
        // be fetched; a clear 503, never a crash/empty.
        var resp = await _http.GetAsync("gateway/ai/models?kind=speech");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Post_test_chat_refuses_catalog_ids_before_touching_the_credential()
    {
        // Included AI revert-proof (issue #1360, inspection round): test-chat sends the requested
        // model with the deployment credential, so it must refuse a non-included id exactly as the
        // model setters do. The refusal comes BEFORE the credential is resolved: in this key-less rig
        // a catalog id answers 400, while an included id gets past the guard to the key check and
        // answers 503 (not signed in). Put the old accept-any-nonblank-model round trip back and the
        // catalog id answers 503 too - red.
        var catalog = await _http.PostAsJsonAsync("gateway/ai/test-chat", new { model = "kimi-k2" });
        Assert.Equal(HttpStatusCode.BadRequest, catalog.StatusCode);

        var included = await _http.PostAsJsonAsync("gateway/ai/test-chat", new { model = "devthrottle/wingman" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, included.StatusCode);
    }

    [Fact]
    public async Task Put_wingman_model_rejects_blank_and_non_object()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PutAsJsonAsync("gateway/ai/wingman-model", new { model = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PutAsJsonAsync("gateway/ai/wingman-fast-model", new { model = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PutAsync("gateway/ai/tts-model", new StringContent("[1,2]", Encoding.UTF8, "application/json"))).StatusCode);
    }

}
