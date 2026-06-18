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
/// End-to-end proof for the Gateway-level wingman agent + model choice (issues #393, #510).
/// Boots a real GatewayHost in-process on an ephemeral port and drives the REST surface over
/// loopback, with CC_DIRECTOR_ROOT redirected to a temp dir so the brain config round-trips an
/// isolated config.json, never the user's real one. In the "DirectorRoot" collection so it never
/// runs alongside other root-touching tests.
///
/// Covers the acceptance criteria that are verifiable in code: the GET surfaces the configured
/// tool + the machine's registered agents + the model; a PUT by agent id persists the choice
/// gateway-side (durably on disk) and the GET then reflects the saved agent id (the page-reload
/// round-trip); the default is claude + opus when unset; and an unknown agent id is rejected
/// (no-fallback).
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
    public async Task GatewaySettings_brain_block_carries_tool_agents_and_model_defaults()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        Assert.NotNull(obj);
        var brain = obj!["brain"] as JsonObject;
        Assert.NotNull(brain);

        // Default tool + model when nothing is configured (existing fleets unchanged).
        Assert.Equal("ClaudeCode", (string?)brain!["tool"]);
        Assert.Equal(BrainModelConfig.Default, (string?)brain["model"]);

        // The machine's registered agents are present (issue #510) and include the Claude Code
        // entry the legacy seed always creates. The picker lists these, not a Claude-only list.
        var agents = brain["agents"] as JsonArray;
        Assert.NotNull(agents);
        Assert.Contains(agents!, a => (string?)(a as JsonObject)?["type"] == "ClaudeCode");
    }

    [Fact]
    public async Task Put_brain_config_by_agent_id_persists_and_round_trips()
    {
        // Pick a real registered agent from the machine list the GET surfaces.
        var snapshot = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        var agents = (snapshot!["brain"] as JsonObject)!["agents"] as JsonArray;
        Assert.NotNull(agents);
        var first = agents![0] as JsonObject;
        Assert.NotNull(first);
        var agentId = (string?)first!["id"];
        var agentType = (string?)first["type"];
        Assert.False(string.IsNullOrWhiteSpace(agentId));

        var resp = await _http.PutAsJsonAsync("gateway/brain/config",
            new { agentId, model = "sonnet" });
        resp.EnsureSuccessStatusCode();

        var echoed = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(agentId, (string?)echoed!["agentId"]);
        Assert.Equal(agentType, (string?)echoed["tool"]);
        Assert.Equal("sonnet", (string?)echoed["model"]);

        // Durable on disk (gateway-side config.json): agent id, its kind, and the model.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal(agentId, (string?)onDisk["brain_agent_id"]);
        Assert.Equal(agentType, (string?)onDisk["brain_tool"]);
        Assert.Equal("sonnet", (string?)onDisk["brain_model"]);

        // The GET reflects the saved agent id so the Cockpit picker pre-selects it after a page
        // reload (the issue #510 acceptance criterion). The running brain's model stays the value
        // fixed at host construction (the documented "applies on next restart" contract).
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        var brain = obj!["brain"] as JsonObject;
        Assert.Equal(agentId, (string?)brain!["agentId"]);
        Assert.Equal(BrainModelConfig.Default, (string?)brain["model"]);
    }

    [Fact]
    public async Task Put_brain_config_rejects_an_unknown_agent_id()
    {
        // An agent id that is not a registered, enabled agent on this machine must be rejected,
        // never silently accepted (no-fallback).
        var resp = await _http.PutAsJsonAsync("gateway/brain/config",
            new { agentId = "not-a-real-agent-id", model = "opus" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Nothing was written.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Null(onDisk["brain_agent_id"]);
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
