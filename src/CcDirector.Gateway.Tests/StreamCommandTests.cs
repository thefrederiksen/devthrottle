using CcDirector.Core.Tenancy;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1177 (Phase 1, increment 1): end-to-end proof that a command flows DOWN the Director stream and
/// takes effect. A real Gateway (stream mode ON) drives a real Director-side <see cref="GatewayStreamClient"/>
/// whose down-channel dispatcher is the shared <see cref="SessionCommandExecutor"/> over a real
/// <see cref="SessionManager"/>. Covers: the direct down-channel (<c>SendCommandAsync</c>), the Gateway
/// prompt endpoint routing DOWN the stream (proven by a dispatcher spy the HTTP fallback never touches),
/// and the flag-off regression (stream mode off => the endpoint stays on HTTP).
/// </summary>
[Collection("DirectorRoot")]
public sealed class StreamCommandTests : IAsyncLifetime
{
    private const string Token = "test-token-streamcmd-1177";
    private const string DirectorId = "dir-A";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _gatewayInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _directorInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-dir-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;      // set in InitializeAsync
    private HttpClient _http = null!;          // set in InitializeAsync
    private SessionManager _directorSessions = null!;
    private ControlApiHost _directorHost = null!;

    public StreamCommandTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-streamcmd-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _gatewayInstances,
            workListsPath: Path.Combine(_gatewayInstances, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // A real Director-side Control API so the Gateway can LOCATE the session over HTTP (Phase 1 keeps
        // session location on HTTP; only command delivery moves to the stream). Auth off so the Gateway's
        // endpoint client can read GET /sessions/{sid} without a token in this harness.
        _directorSessions = new SessionManager(new AgentOptions());
        _directorHost = new ControlApiHost(_directorSessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: DirectorId, instancesDirectory: _directorInstances);
        await _directorHost.StartAsync();

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            // Remove-the-network-port: the Director serves no HTTP, so there is no endpoint to
            // register - everything rides the tunnel, exactly as the portless tests below prove.
            TailnetEndpoint = "",
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _directorHost.StopAsync();
        _directorSessions.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        TryDelete(_gatewayInstances);
        TryDelete(_directorInstances);
        TryDelete(_root);
    }

    [Fact]
    public async Task SendCommandAsync_ReturnsNull_WhenDirectorHasNoStream()
    {
        // No GatewayStreamClient started for this id => no active connection => null (HTTP fallback signal).
        var result = await _gateway.SendCommandAsync("dir-nobody",
            PromptCommand(Guid.NewGuid().ToString(), new PromptRequest { Text = "x" }));
        Assert.Null(result);
    }

    [Fact]
    public async Task StreamModeOff_PatchEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offp-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-offp", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offp");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offp",
                TailnetEndpoint = "http://127.0.0.1:59997/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PatchAsJsonAsync($"sessions/{Guid.NewGuid()}", new SessionUpdateRequest { Name = "x" });

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_WingmanGoalEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offg-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-offg", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offg");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offg",
                TailnetEndpoint = "http://127.0.0.1:59999/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/wingman/goal", new WingmanGoalRequest { Goal = "x" });

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_PortlessDirector_PerSessionPrompt_Returns404_LocationUnchanged()
    {
        // Flag-off regression: with stream mode OFF, pushedSessions is null, so LocateSessionAsync uses ONLY
        // the HTTP pull - which against an empty control endpoint finds nothing => 404, exactly as today. This
        // pins that the pushed-cache location is INERT when the flag is off (byte-identical behaviour).
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offloc-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-offloc", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offloc");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offloc",
                TailnetEndpoint = "", // portless: nothing to pull
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });

            // Nothing located, so nothing is sent. Since Voice Delivery phase 5 (contract section 8) that typed prompt is
            // held 202 waiting for its Director rather than answered 404.
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("waiting-for-director", body.GetProperty("directorState").GetString());
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path (ConPty
    // spawn) without depending on an installed coding-agent CLI.
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    [Fact]
    public async Task StreamModeOff_CreateEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offc-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-offc", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offc");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offc",
                TailnetEndpoint = "http://127.0.0.1:59998/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync("directors/dir-offc/sessions", new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "off-create",
            });

            // No stream + unreachable Director => the HTTP create fails => 502 (today's behaviour).
            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_KillEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offk-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-offk", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offk");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offk",
                TailnetEndpoint = "http://127.0.0.1:59996/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.DeleteAsync($"sessions/{Guid.NewGuid()}");

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_InterruptEndpoint_StaysOnHttp()
    {
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-offi-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-offi", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-offi");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-offi",
                TailnetEndpoint = "http://127.0.0.1:59995/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsync($"sessions/{Guid.NewGuid()}/interrupt", content: null);

            // No stream + unreachable Director => HTTP location finds nothing => 404 (today's behaviour).
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    [Fact]
    public async Task StreamModeOff_PromptEndpoint_StaysOnHttp()
    {
        // A second Gateway with stream mode OFF. Its Director is registered with an UNREACHABLE endpoint,
        // so a prompt can only be attempted over HTTP - which fails - proving no stream path is used and
        // behaviour matches today's HTTP-only Gateway.
        var offInstances = Path.Combine(Path.GetTempPath(), "cc-streamcmd-off-" + Guid.NewGuid().ToString("N"));
        var off = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "t-off", authEnabled: true,
            instancesDirectory: offInstances,
            workListsPath: Path.Combine(offInstances, "worklists", "worklists.json"),
            streamMode: false);
        await off.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{off.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t-off");
            off.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-off",
                TailnetEndpoint = "http://127.0.0.1:59994/", // unreachable on purpose
                MachineName = "test-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            var resp = await http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });

            // Unreachable Director + no stream => the HTTP location pull finds nothing, so the request is NOT served
            // (no stream shortcut). Since Voice Delivery phase 5 (contract section 8) a typed prompt whose session is
            // not located is held 202 waiting for its Director rather than answered 404 - still nothing was sent.
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("waiting-for-director", body.GetProperty("directorState").GetString());
        }
        finally
        {
            await off.StopAsync();
            TryDelete(offInstances);
        }
    }

    private static DirectorCommand PromptCommand(string sessionId, PromptRequest req) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        Verb = "prompt",
        SessionId = sessionId,
        PayloadJson = JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { /* best effort */ }
    }
}
