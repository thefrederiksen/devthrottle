using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the Gateway-level wingman/brain tool + model choice (issue #393).
/// Boots a real GatewayHost in-process on an ephemeral port and drives the REST surface over
/// loopback, with CC_DIRECTOR_ROOT redirected to a temp dir so the brain config round-trips an
/// isolated config.json, never the user's real one. In the "DirectorRoot" collection so it never
/// runs alongside other root-touching tests.
///
/// Covers the acceptance criteria that are verifiable in code: the GET surfaces the tool + the
/// selectable tool list + the model; a PUT persists the choice gateway-side (durably on disk);
/// the default is claude + opus when unset; and an invalid tool is rejected (no-fallback).
/// </summary>
[Collection("DirectorRoot")]
public sealed class BrainConfigEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-braincfg-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public BrainConfigEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-braincfg-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token-12345", authEnabled: true,
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
    public async Task GatewaySettings_brain_block_carries_tool_tools_and_model_defaults()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        Assert.NotNull(obj);
        var brain = obj!["brain"] as JsonObject;
        Assert.NotNull(brain);

        // Default tool + model when nothing is configured (existing fleets unchanged).
        Assert.Equal("ClaudeCode", (string?)brain!["tool"]);
        Assert.Equal(BrainModelConfig.Default, (string?)brain["model"]);

        // The selectable tool list is present and offers the default.
        var tools = brain["tools"] as JsonArray;
        Assert.NotNull(tools);
        Assert.Contains(tools!, t => (string?)t == "ClaudeCode");
    }

    [Fact]
    public async Task Put_brain_config_persists_tool_and_model_gateway_side()
    {
        var resp = await _http.PutAsJsonAsync("gateway/brain/config",
            new { tool = "ClaudeCode", model = "sonnet" });
        resp.EnsureSuccessStatusCode();

        var echoed = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("ClaudeCode", (string?)echoed!["tool"]);
        Assert.Equal("sonnet", (string?)echoed["model"]);

        // Durable on disk (gateway-side config.json), not just in the response.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("ClaudeCode", (string?)onDisk["brain_tool"]);
        Assert.Equal("sonnet", (string?)onDisk["brain_model"]);

        // The GET's brain block reflects the RUNNING brain, whose model/tool were fixed at host
        // construction (read once at Gateway start - the documented "applies on next restart"
        // contract, same as brain_model has always behaved). So a live save does not change the
        // running brain's reported model; the durable on-disk value above is the proof of persistence.
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        var brain = obj!["brain"] as JsonObject;
        Assert.Equal(BrainModelConfig.Default, (string?)brain!["model"]);
    }

    [Fact]
    public async Task Put_brain_config_rejects_a_non_hostable_tool()
    {
        // Pi is a known AgentKind but cannot host a brain - the endpoint must reject it,
        // never silently accept (no-fallback).
        var resp = await _http.PutAsJsonAsync("gateway/brain/config",
            new { tool = "Pi", model = "opus" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Nothing was written.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Null(onDisk["brain_tool"]);
    }

    [Fact]
    public async Task Put_brain_config_rejects_a_blank_model()
    {
        var resp = await _http.PutAsJsonAsync("gateway/brain/config",
            new { tool = "ClaudeCode", model = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_brain_config_rejects_a_non_object_body()
    {
        var content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync("gateway/brain/config", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_brain_config_preserves_unrelated_config_sections()
    {
        // Seed an unrelated section, then a brain-config PUT must not drop it (MergePatch).
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://gw.example:7878" },
        });

        var resp = await _http.PutAsJsonAsync("gateway/brain/config",
            new { tool = "ClaudeCode", model = "opus" });
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://gw.example:7878", (string?)onDisk["gateway"]!["url"]);
        Assert.Equal("opus", (string?)onDisk["brain_model"]);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
