using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end tests for <c>POST /tools/run</c> (issue #328) against a real auth-enabled
/// ControlApiHost, covering the request-validation and security gates that are machine-independent:
/// the token gate (401), the bare-catalog-name gate (400 for any path-shaped name - never
/// executed), the catalog allowlist (404 for unknown tools), and request-shape errors (missing
/// name, null body, out-of-range timeout, nonexistent cwd). Successful execution / streaming /
/// timeout mechanics are covered by ToolRunnerTests in Core.Tests (cmd.exe-backed) plus the live
/// proof, because whether a given cc-* tool is BUILT on the test machine is environment state.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ToolRunEndpointTests : IAsyncLifetime
{
    private readonly string _instancesDir;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public ToolRunEndpointTests()
    {
        _instancesDir = Path.Combine(Path.GetTempPath(), "ccd-tools-run-test-" + Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: true, instancesDirectory: _instancesDir);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, recursive: true); } catch { /* best effort */ }
    }

    // ---------- The verb is token-gated like every other verb ----------

    [Fact]
    public async Task ToolsRun_WithoutBearerToken_Returns401()
    {
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        var resp = await anonymous.PostAsJsonAsync("tools/run", new ToolRunRequest { Name = "cc-vault" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---------- Catalog allowlist: only manifest tools, only bare names ----------

    [Fact]
    public async Task ToolsRun_UnknownToolName_Returns404()
    {
        var resp = await _client.PostAsJsonAsync("tools/run", new ToolRunRequest { Name = "cc-not-a-real-tool" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unknown tool", body);
    }

    [Theory]
    [InlineData("tools/../cc-vault")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("../../evil")]
    [InlineData(@"..\..\evil")]
    [InlineData("sub/dir/cc-vault")]
    public async Task ToolsRun_PathShapedName_Returns400NeverExecuted(string name)
    {
        var resp = await _client.PostAsJsonAsync("tools/run", new ToolRunRequest { Name = name });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("bare catalog tool name", body);
    }

    // ---------- Request-shape validation ----------

    [Fact]
    public async Task ToolsRun_MissingName_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("tools/run", new ToolRunRequest { Name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ToolsRun_NullBody_Returns400()
    {
        var content = new StringContent("null", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("tools/run", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ToolsRun_MalformedJson_Returns400()
    {
        var content = new StringContent("{not json", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("tools/run", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3601)]
    public async Task ToolsRun_TimeoutOutOfRange_Returns400(int timeoutS)
    {
        var resp = await _client.PostAsJsonAsync("tools/run",
            new ToolRunRequest { Name = "cc-vault", TimeoutS = timeoutS });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("timeoutS", body);
    }

    [Fact]
    public async Task ToolsRun_NonexistentCwd_Returns400()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "no-such-cwd-" + Guid.NewGuid().ToString("N"));
        var resp = await _client.PostAsJsonAsync("tools/run",
            new ToolRunRequest { Name = "cc-vault", Cwd = missingDir });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("cwd not found", body);
    }
}
