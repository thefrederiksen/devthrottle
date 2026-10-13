using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (final pre-cut re-point): POST /directors/{id}/sessions/github - the
/// "create a session from a GitHub repo" endpoint - dialed the target Director over HTTP with NO tunnel branch
/// (a stray the re-run definitive grep surfaced). It now rides the existing director-level "create-from-github"
/// verb, tunnel-first with the byte-identical HTTP dial as the fallback.
///
/// TUNNEL-BY-CONSTRUCTION: the Director registers UNREACHABLE, so a 201 with the created session can only have
/// ridden the tunnel; the test asserts the exact verb + payload the Gateway sent down.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelGitHubSessionProofTests : IAsyncLifetime
{
    private const string Token = "test-token-github-session";
    private const string DirectorId = "dir-github";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-github-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;
    // Every command the Gateway sent over the tunnel. A test asks for the command of ITS OWN verb, never for "the last
    // command": the Gateway also sends commands of its own accord (a pushed waiting session has its screen read on a
    // background task), so which command arrived last depends on how busy the machine is.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _commands = new();

    private DirectorCommand Sent(string verb) => Assert.Single(_commands, c => c.Verb == verb);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public TunnelGitHubSessionProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-github-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59921/", // nothing listens here
            MachineName = "github-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", cmd =>
        {
            _commands.Enqueue(cmd);
            return cmd.Verb == "create-from-github"
                ? DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { SessionId = "gh-sess", ActivityState = "Working" }, WebJson))
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateGitHubSession_ridesTheTunnel_asCreateFromGithub()
    {
        var resp = await _http.PostAsJsonAsync($"directors/{DirectorId}/sessions/github", new GitHubSessionRequest
        {
            Owner = "thefrederiksen",
            Repo = "devthrottle",
            InitialPrompt = "fix the flaky test",
            TriggerMode = "NewIssue",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("gh-sess", node?["sessionId"]?.GetValue<string>());

        var sent = Sent("create-from-github");
        Assert.Equal("", sent.SessionId); // director-level
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal("thefrederiksen", (string?)payload["owner"]); // the request rode the payload
        Assert.Equal("devthrottle", (string?)payload["repo"]);
    }

}
