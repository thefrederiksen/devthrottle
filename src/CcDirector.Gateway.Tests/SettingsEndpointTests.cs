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
/// End-to-end smoke tests for GET/PUT /settings on the Director Control API. Runs against a
/// real ControlApiHost on an ephemeral port. CC_DIRECTOR_ROOT is redirected to a temp dir so
/// the tests read/write an isolated config.json, never the user's real one. In the
/// "DirectorRoot" collection so it never runs alongside other root-touching tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SettingsEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public SettingsEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-settings-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Seed a config with two sections so we can prove a targeted PUT preserves the other.
        var path = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "gateway": { "url": "http://gw.example:7878" },
          "screenshots": { "source_directory": "/old/path" }
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
    public async Task Get_settings_returns_full_config_object()
    {
        var obj = await _client.GetFromJsonAsync<JsonObject>("settings");
        Assert.NotNull(obj);
        Assert.Equal("http://gw.example:7878", (string?)obj!["gateway"]!["url"]);
        Assert.Equal("/old/path", (string?)obj["screenshots"]!["source_directory"]);
    }

    [Fact]
    public async Task Put_settings_merges_and_preserves_siblings()
    {
        var patch = new JsonObject
        {
            ["screenshots"] = new JsonObject { ["source_directory"] = "/Users/soren/Desktop" },
        };
        var resp = await _client.PutAsJsonAsync("settings", patch);
        resp.EnsureSuccessStatusCode();

        var merged = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("/Users/soren/Desktop", (string?)merged!["screenshots"]!["source_directory"]);
        // The gateway block the PUT didn't mention must still be there.
        Assert.Equal("http://gw.example:7878", (string?)merged["gateway"]!["url"]);

        // And it must be durable on disk, not just in the response.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("/Users/soren/Desktop", (string?)onDisk["screenshots"]!["source_directory"]);
        Assert.Equal("http://gw.example:7878", (string?)onDisk["gateway"]!["url"]);
    }

    [Fact]
    public async Task Put_settings_with_gateway_change_succeeds_and_persists()
    {
        // A gateway change triggers a live re-apply (ReapplyGatewayAsync) - it must not throw.
        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["url"] = "http://new-gw.example:7878",
                ["tailnetEndpoint"] = "http://mac.example:7879",
            },
        };
        var resp = await _client.PutAsJsonAsync("settings", patch);
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://new-gw.example:7878", (string?)onDisk["gateway"]!["url"]);
        Assert.Equal("http://mac.example:7879", (string?)onDisk["gateway"]!["tailnetEndpoint"]);
    }

    [Fact]
    public async Task Put_settings_rejects_non_object_body()
    {
        var content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json");
        var resp = await _client.PutAsync("settings", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
