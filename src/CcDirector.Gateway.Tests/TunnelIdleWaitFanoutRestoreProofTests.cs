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
/// Gateway Cleanup mission, Phase 2 (final pre-cut re-point): the THREE stray caller clusters the definitive
/// DirectorEndpointClient grep surfaced - each dialed the owning Director over HTTP with NO tunnel branch - now
/// ride the tunnel under stream mode. The re-points reuse EXISTING registered verbs
/// (snapshot / buffer / prompt / create / patch / interrupted-remove):
///   1. POST /sessions/{sid}/prompt WaitForIdle poll (was GetSession + GetBuffer HTTP)
///   2. POST /fanout fleet broadcast delivery + poll (was PostPrompt + GetSession + GetBuffer HTTP)
///   3. POST /interrupted/{dir}/{pid}/restore create + rename + journal cleanup (was raw spawn + PatchSession
///      + RemoveInterruptedSession HTTP)
///
/// TUNNEL-BY-CONSTRUCTION: the Director registers with a DELIBERATELY UNREACHABLE control endpoint, so an HTTP
/// dial cannot succeed - a 200/201 with the expected body can ONLY have ridden the tunnel. Each test asserts the
/// exact verbs the Gateway sent DOWN the tunnel, so a leg that silently stayed on HTTP fails loudly.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelIdleWaitFanoutRestoreProofTests : IAsyncLifetime
{
    private const string Token = "test-token-idlewait-fanout-restore";
    private const string DirectorId = "dir-idlewait";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-idlewait-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;
    private SessionManager _sm = null!;
    private Session _session = null!;
    private string _sid = "";

    // Every command the Director saw over the tunnel, in order, so a test can assert the exact verb sequence.
    // Written by the tunnel's callback thread while a test reads it, so it must be safe to read while written.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _commands = new();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public TunnelIdleWaitFanoutRestoreProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-idlewait-" + Guid.NewGuid().ToString("N"));
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

        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        _sid = _session.Id.ToString();

        // UNREACHABLE endpoint: a working route can only have ridden the tunnel.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/", // nothing listens here
            MachineName = "idlewait-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", Dispatch);
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        await _conn.InvokeAsync("PushSnapshot", 1L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" },
        });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _sm.Dispose();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // Canned per-verb responses so each flow completes; every command is recorded for verb-sequence assertions.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        _commands.Enqueue(cmd);
        return cmd.Verb switch
        {
            // prompt / fanout delivery: accepted, still Working so the WaitForIdle poll actually runs.
            "prompt" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new PromptResponse { Accepted = true, ActivityState = "Working", BufferCursor = 3 }, WebJson)),
            // the idle poll: report Idle on the first read so the loop exits promptly.
            "snapshot" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new SessionDto { SessionId = cmd.SessionId, ActivityState = "Idle" }, WebJson)),
            // the output diff.
            "buffer" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new BufferResponse { SessionId = cmd.SessionId, Text = "new output", NewCursor = 9 }, WebJson)),
            // restore: journal re-read, continuation create, rename, journal cleanup.
            "interrupted-list" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new List<CrashJournalDto>
                {
                    new()
                    {
                        DirectorId = "dead-dir",
                        Pid = 999,
                        Sessions = new List<CrashJournalSessionDto>
                        {
                            new() { SessionId = "dead-sess", Name = "Dead One", RepoPath = Path.GetTempPath(), Agent = "ClaudeCode" },
                        },
                    },
                }, WebJson)),
            "create" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new SessionDto { SessionId = "new-sess", ActivityState = "Working" }, WebJson)),
            "patch" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new SessionDto { SessionId = "new-sess", Name = "Dead One" }, WebJson)),
            "interrupted-remove" => DirectorCommandResult.Success(),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };
    }

    [Fact]
    public async Task PromptWaitForIdle_pollAndBufferRideTheTunnel()
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/prompt",
            new PromptRequest { Text = "hi", WaitForIdle = true, TimeoutMs = 5000 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var body = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.Equal("idle", body?.WaitStatus);
        Assert.Equal("new output", body?.Output);

        // Delivery, the idle poll, and the output diff all rode the tunnel.
        var verbs = _commands.Select(c => c.Verb).ToList();
        Assert.Contains("prompt", verbs);
        Assert.Contains("snapshot", verbs);   // the poll (was client.GetSessionAsync)
        Assert.Contains("buffer", verbs);     // the diff (was client.GetBufferAsync)
    }

    [Fact]
    public async Task Fanout_deliveryPollAndBufferRideTheTunnel()
    {
        // Sender == target => the broadcast stays in the sender's own team (in-scope, free) so the governor allows it.
        var resp = await _http.PostAsJsonAsync("fanout", new FanoutRequest
        {
            SessionIds = new List<string> { _sid },
            Text = "team heads-up",
            FromSessionId = _sid,
            WaitForIdle = true,
            TimeoutMs = 5000,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotEqual(true, node?["denied"]?.GetValue<bool>());

        var verbs = _commands.Select(c => c.Verb).ToList();
        Assert.Contains("prompt", verbs);     // delivery (was client.PostPromptAsync)
        Assert.Contains("snapshot", verbs);   // the poll (was client.GetSessionAsync)
        Assert.Contains("buffer", verbs);     // the diff (was client.GetBufferAsync)
    }

    [Fact]
    public async Task RestoreInterrupted_createRenameAndCleanupRideTheTunnel()
    {
        var resp = await _http.PostAsJsonAsync("interrupted/dead-dir/999/restore",
            new RestoreInterruptedRequest { SessionId = "dead-sess", Via = DirectorId });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode); // 201; an HTTP dial to the unreachable Director would have failed
        var body = await resp.Content.ReadFromJsonAsync<RestoreInterruptedResponse>();
        Assert.True(body?.Restored);
        Assert.Equal("new-sess", body?.TargetSession?.SessionId);

        var verbs = _commands.Select(c => c.Verb).ToList();
        Assert.Contains("interrupted-list", verbs);   // journal re-read (already tunnel-first)
        Assert.Contains("create", verbs);             // continuation (was raw spawnHttp.PostAsJsonAsync)
        Assert.Contains("patch", verbs);              // rename (was client.PatchSessionAsync)
        Assert.Contains("interrupted-remove", verbs); // journal cleanup (was client.RemoveInterruptedSessionAsync)
    }

}
