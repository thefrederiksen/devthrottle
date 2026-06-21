using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end tests for the /settings/agents REST surface on the Director Control API (issue #584).
/// Runs against a real ControlApiHost on an ephemeral port. CC_DIRECTOR_ROOT is redirected to a temp
/// dir so the tests read/write an isolated config.json, never the user's real one. In the
/// "DirectorRoot" collection so it never runs alongside other root-touching tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class AgentsEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!; // assigned in InitializeAsync (xUnit lifecycle)
    private SessionManager _sm = null!;   // assigned in InitializeAsync (xUnit lifecycle)
    private HttpClient _client = null!;   // assigned in InitializeAsync (xUnit lifecycle)

    public AgentsEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-agents-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Seed a config with an explicit two-entry agent library plus an unrelated section, so we
        // can prove targeted writes preserve siblings and the unrelated section.
        var path = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "gateway": { "url": "http://gw.example:7878" },
          "agent": {
            "entries": [
              {
                "id": "id-claude",
                "display_name": "Claude Code",
                "type": "ClaudeCode",
                "enabled": true,
                "executable_path": "claude",
                "preset_id": "Standard",
                "default_model": "sonnet",
                "args_override": "",
                "launch_mode": "Guided"
              },
              {
                "id": "id-codex",
                "display_name": "Codex",
                "type": "Codex",
                "enabled": false,
                "executable_path": "codex",
                "preset_id": "Standard",
                "default_model": "",
                "args_override": "",
                "launch_mode": "Guided"
              }
            ]
          }
        }
        """);

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task List_returns_every_entry_in_file_order()
    {
        var obj = await _client.GetFromJsonAsync<JsonObject>("settings/agents");
        Assert.NotNull(obj);
        var agents = obj!["agents"]!.AsArray();
        Assert.Equal(2, agents.Count);
        Assert.Equal("id-claude", (string?)agents[0]!["id"]);
        Assert.Equal("id-codex", (string?)agents[1]!["id"]);
    }

    [Fact]
    public async Task Get_by_id_returns_all_values()
    {
        var obj = await _client.GetFromJsonAsync<JsonObject>("settings/agents/id-claude");
        Assert.NotNull(obj);
        Assert.Equal("Claude Code", (string?)obj!["displayName"]);
        Assert.Equal("ClaudeCode", (string?)obj["type"]);
        Assert.Equal("Standard", (string?)obj["presetId"]);
        Assert.Equal("sonnet", (string?)obj["defaultModel"]);
        Assert.Equal("Guided", (string?)obj["launchMode"]);
    }

    [Fact]
    public async Task Get_by_unknown_id_returns_404()
    {
        var resp = await _client.GetAsync("settings/agents/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Add_appends_entry_and_returns_it_with_id()
    {
        var body = new JsonObject { ["type"] = "Gemini", ["displayName"] = "My Gemini" };
        var resp = await _client.PostAsJsonAsync("settings/agents", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(created);
        var newId = (string?)created!["id"];
        Assert.False(string.IsNullOrWhiteSpace(newId));
        Assert.Equal("Gemini", (string?)created["type"]);
        Assert.Equal("My Gemini", (string?)created["displayName"]);

        // Present in the file and in a follow-up list.
        var onDisk = CcDirectorConfigService.ReadRaw();
        var entries = onDisk["agent"]!["entries"]!.AsArray();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => (string?)e!["id"] == newId);

        var list = await _client.GetFromJsonAsync<JsonObject>("settings/agents");
        Assert.Equal(3, list!["agents"]!.AsArray().Count);
    }

    [Fact]
    public async Task Patch_updates_single_value_and_preserves_other_entries()
    {
        var before = CcDirectorConfigService.ReadRaw();
        var codexBefore = before["agent"]!["entries"]!.AsArray()[1]!.ToJsonString();

        var patch = new JsonObject { ["argsOverride"] = "--my-flag" };
        var resp = await _client.PatchAsync("settings/agents/id-claude",
            new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();

        var updated = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("--my-flag", (string?)updated!["argsOverride"]);

        // Durable in the file under the right entry, and the OTHER entry is byte-identical.
        var after = CcDirectorConfigService.ReadRaw();
        var afterEntries = after["agent"]!["entries"]!.AsArray();
        Assert.Equal("--my-flag", (string?)afterEntries[0]!["args_override"]);
        Assert.Equal(codexBefore, afterEntries[1]!.ToJsonString());
        // The unrelated gateway section is untouched.
        Assert.Equal("http://gw.example:7878", (string?)after["gateway"]!["url"]);
    }

    [Fact]
    public async Task Patch_each_editable_field_round_trips()
    {
        async Task AssertField(string jsonField, JsonNode value, string fileKey, string expected)
        {
            var patch = new JsonObject { [jsonField] = value };
            var resp = await _client.PatchAsync("settings/agents/id-claude",
                new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();

            var get = await _client.GetFromJsonAsync<JsonObject>("settings/agents/id-claude");
            Assert.Equal(expected, get![jsonField]!.ToString());

            var onDisk = CcDirectorConfigService.ReadRaw();
            Assert.Equal(expected, onDisk["agent"]!["entries"]!.AsArray()[0]![fileKey]!.ToString());
        }

        await AssertField("displayName", "Renamed", "display_name", "Renamed");
        await AssertField("type", "Codex", "type", "Codex");
        await AssertField("enabled", false, "enabled", "false");
        await AssertField("executablePath", "C:/tools/claude.cmd", "executable_path", "C:/tools/claude.cmd");
        await AssertField("presetId", "Automatic (skip permissions)", "preset_id", "Automatic (skip permissions)");
        await AssertField("defaultModel", "opus", "default_model", "opus");
        await AssertField("argsOverride", "--x", "args_override", "--x");
        await AssertField("launchMode", "Custom", "launch_mode", "Custom");
    }

    [Fact]
    public async Task Delete_removes_entry_and_it_is_gone_from_list_and_file()
    {
        var resp = await _client.DeleteAsync("settings/agents/id-codex");
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        var entries = onDisk["agent"]!["entries"]!.AsArray();
        Assert.Single(entries);
        Assert.DoesNotContain(entries, e => (string?)e!["id"] == "id-codex");

        var list = await _client.GetFromJsonAsync<JsonObject>("settings/agents");
        Assert.DoesNotContain(list!["agents"]!.AsArray(), e => (string?)e!["id"] == "id-codex");
    }

    [Fact]
    public async Task Delete_unknown_id_returns_404_and_leaves_file_unchanged()
    {
        var before = CcDirectorConfigService.ReadRaw().ToJsonString();
        var resp = await _client.DeleteAsync("settings/agents/nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(before, CcDirectorConfigService.ReadRaw().ToJsonString());
    }

    [Fact]
    public async Task Reorder_sets_file_order_to_requested_order()
    {
        var body = new JsonObject { ["ids"] = new JsonArray("id-codex", "id-claude") };
        var resp = await _client.PostAsJsonAsync("settings/agents/reorder", body);
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        var entries = onDisk["agent"]!["entries"]!.AsArray();
        Assert.Equal("id-codex", (string?)entries[0]!["id"]);
        Assert.Equal("id-claude", (string?)entries[1]!["id"]);
    }

    [Fact]
    public async Task Reorder_with_bad_ids_is_rejected_and_file_unchanged()
    {
        var before = CcDirectorConfigService.ReadRaw().ToJsonString();
        var body = new JsonObject { ["ids"] = new JsonArray("id-claude") }; // missing id-codex
        var resp = await _client.PostAsJsonAsync("settings/agents/reorder", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, CcDirectorConfigService.ReadRaw().ToJsonString());
    }

    [Fact]
    public async Task Enabled_toggle_sets_the_flag()
    {
        var body = new JsonObject { ["enabled"] = true };
        var resp = await _client.PostAsJsonAsync("settings/agents/id-codex/enabled", body);
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.True((bool)onDisk["agent"]!["entries"]!.AsArray()[1]!["enabled"]!);
    }

    [Fact]
    public async Task Detect_custom_type_says_nothing_to_detect()
    {
        var body = new JsonObject { ["type"] = "RawCli" };
        var resp = await _client.PostAsJsonAsync("settings/agents/detect", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False((bool)result!["detectable"]!);
        Assert.False((bool)result["found"]!);
    }

    [Fact]
    public async Task Detect_builtin_type_returns_a_found_result_shape()
    {
        var body = new JsonObject { ["type"] = "ClaudeCode" };
        var resp = await _client.PostAsJsonAsync("settings/agents/detect", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True((bool)result!["detectable"]!);
        // found may be true or false depending on the test host; the shape is what we assert.
        Assert.True(result.ContainsKey("found"));
        Assert.True(result.ContainsKey("source"));
        Assert.True(result.ContainsKey("message"));
    }

    [Fact]
    public async Task QuickCheck_persists_validation_status_in_config()
    {
        var body = new JsonObject { ["type"] = "ClaudeCode", ["path"] = "definitely-not-a-real-tool.exe" };
        var resp = await _client.PostAsJsonAsync("settings/agents/quick-check", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False((bool)result!["ok"]!); // a bogus path cannot pass

        // The validation status is recorded the same way the Agents tab records it.
        var onDisk = CcDirectorConfigService.ReadRaw();
        var status = onDisk["agent_status"]!["claude"]!.AsObject();
        Assert.False((bool)status["ok"]!);
        Assert.True(status.ContainsKey("tested_at_utc"));
    }

    [Fact]
    public async Task CommandLine_matches_the_resolver_for_a_guided_claude_config()
    {
        var body = new JsonObject
        {
            ["type"] = "ClaudeCode",
            ["executablePath"] = "claude",
            ["presetId"] = "Automatic (skip permissions)",
            ["defaultModel"] = "opus",
            ["launchMode"] = "Guided",
        };
        var resp = await _client.PostAsJsonAsync("settings/agents/command-line", body);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonObject>();

        // Same resolution the Agents tab preview uses (AgentToolConfig.ResolveEffectiveCommandLineArguments).
        var expected = new AgentToolConfig
        {
            Tool = CcDirector.Core.Agents.AgentKind.ClaudeCode,
            PresetName = "Automatic (skip permissions)",
            DefaultModel = "opus",
            LaunchMode = LaunchMode.Guided,
        }.ResolveEffectiveCommandLineArguments();
        Assert.Equal(expected, (string?)result!["arguments"]);
        Assert.Equal($"claude {expected}", (string?)result["commandLine"]);
    }

    [Fact]
    public async Task Catalog_returns_types_presets_and_models()
    {
        var obj = await _client.GetFromJsonAsync<JsonObject>("settings/agents/catalog");
        Assert.NotNull(obj);
        var types = obj!["types"]!.AsArray();
        var claude = types.First(t => (string?)t!["type"] == "ClaudeCode")!.AsObject();
        Assert.True((bool)claude["supportsModelSelection"]!);
        Assert.Equal("--model", (string?)claude["modelFlag"]);
        Assert.NotEmpty(claude["presets"]!.AsArray());
        Assert.NotEmpty(claude["models"]!.AsArray());

        // A non-model tool reports no model selection and an empty model list.
        var pi = types.First(t => (string?)t!["type"] == "Pi")!.AsObject();
        Assert.False((bool)pi["supportsModelSelection"]!);
        Assert.Empty(pi["models"]!.AsArray());
    }

    [Fact]
    public async Task Invalid_type_is_rejected_with_400_and_file_unchanged()
    {
        var before = CcDirectorConfigService.ReadRaw().ToJsonString();
        var body = new JsonObject { ["type"] = "NotARealAgent" };
        var resp = await _client.PostAsJsonAsync("settings/agents", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, CcDirectorConfigService.ReadRaw().ToJsonString());
    }

    [Fact]
    public async Task Invalid_launch_mode_on_patch_is_rejected_with_400_and_file_unchanged()
    {
        var before = CcDirectorConfigService.ReadRaw().ToJsonString();
        var patch = new JsonObject { ["launchMode"] = "Sideways" };
        var resp = await _client.PatchAsync("settings/agents/id-claude",
            new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, CcDirectorConfigService.ReadRaw().ToJsonString());
    }
}
