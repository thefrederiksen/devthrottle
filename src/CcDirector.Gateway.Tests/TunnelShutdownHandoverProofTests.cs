using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (final pre-cut re-point): the two Gateway paths that dialed a Director over
/// HTTP with NO tunnel path now ride the tunnel via the two NEW verbs this PR adds:
///   - DELETE /directors/{id}  -> the "shutdown" director-level verb (POST /shutdown stays on the loopback floor
///     for the local launcher; this is the Gateway-initiated REMOTE stop).
///   - POST /handover          -> same-Director via the existing "handover-generate" verb; cross-Director reads
///     the source context via the new "handover-context" read verb, then creates the target via "create".
///
/// TUNNEL-BY-CONSTRUCTION: both Directors register with DELIBERATELY UNREACHABLE control endpoints, so a success
/// can only have ridden the tunnel; each test asserts the exact verb(s) the Gateway sent down.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelShutdownHandoverProofTests : IAsyncLifetime
{
    private const string Token = "test-token-shutdown-handover";
    private const string SourceDir = "dir-source";
    private const string TargetDir = "dir-target";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-shutho-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _sourceConn = null!;
    private HubConnection _targetConn = null!;
    private SessionManager _sm = null!;
    private Session _session = null!;
    private string _sid = "";

    // Written by the tunnel's callback thread while a test reads them, and the Gateway sends commands of its own
    // accord (a pushed waiting session has its screen read on a background task), so a plain list could be changed
    // under a test that was reading it. These collections may be read while they are written.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _sourceCommands = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _targetCommands = new();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public TunnelShutdownHandoverProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-shutho-" + Guid.NewGuid().ToString("N"));
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

        foreach (var id in new[] { SourceDir, TargetDir })
            _gateway.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = id,
                TailnetEndpoint = "http://127.0.0.1:59920/", // nothing listens here
                MachineName = "shutho-machine",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

        _sourceConn = await ConnectAsync(SourceDir, _sourceCommands);
        _targetConn = await ConnectAsync(TargetDir, _targetCommands);

        // The source session is pushed so the Gateway resolves SourceDir as its owner (TryLocate) via the tunnel.
        await _sourceConn.InvokeAsync("PushSnapshot", 1L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" },
        });
    }

    private async Task<HubConnection> ConnectAsync(string directorId, System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> sink)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        conn.On<DirectorCommand, DirectorCommandResult>("Command", cmd => Dispatch(directorId, cmd, sink));
        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = directorId, Version = "test" });
        return conn;
    }

    private static DirectorCommandResult Dispatch(string directorId, DirectorCommand cmd, System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> sink)
    {
        sink.Enqueue(cmd);
        return cmd.Verb switch
        {
            "shutdown" => DirectorCommandResult.Success(),
            "handover-generate" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new HandoverResponse { Accepted = true, TargetSession = new SessionDto { SessionId = "ho-target", ActivityState = "Working" } }, WebJson)),
            "handover-context" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new HandoverContextResponse { Text = "the source handover context" }, WebJson)),
            "create" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new SessionDto { SessionId = "xdir-target", ActivityState = "Working" }, WebJson)),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };
    }

    public async Task DisposeAsync()
    {
        try { await _sourceConn.DisposeAsync(); } catch { /* best effort */ }
        try { await _targetConn.DisposeAsync(); } catch { /* best effort */ }
        _sm.Dispose();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task DeleteDirector_ridesTheTunnel_asTheShutdownVerb()
    {
        // TargetDir has no pushed sessions, so the live-session gate is skipped and the remote stop proceeds.
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{TargetDir}")
        {
            Content = JsonContent.Create(new { reason = "proof: remote stop over the tunnel" }),
        };
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed

        Assert.Contains(_targetCommands, c => c.Verb == "shutdown");
        Assert.All(_targetCommands.Where(c => c.Verb == "shutdown"), c => Assert.Equal("", c.SessionId)); // director-level
    }

    [Fact]
    public async Task Handover_sameDirector_ridesTheTunnel_asHandoverGenerate()
    {
        var resp = await _http.PostAsJsonAsync("handover", new HandoverRequest
        {
            FromSessionId = _sid,
            ToRepoPath = Path.GetTempPath(), // same-Director (no toDirectorId) => the handover-generate proxy
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HandoverResponse>();
        Assert.True(body?.Accepted);
        Assert.Equal("ho-target", body?.TargetSession?.SessionId);
        Assert.Equal(SourceDir, body?.TargetSession?.DirectorId); // the Gateway stamps the source director id

        Assert.Contains(_sourceCommands, c => c.Verb == "handover-generate");
    }

    [Fact]
    public async Task Handover_crossDirector_ridesTheTunnel_asHandoverContextThenCreate()
    {
        var resp = await _http.PostAsJsonAsync("handover", new HandoverRequest
        {
            FromSessionId = _sid,
            ToDirectorId = TargetDir,          // cross-Director
            ToRepoPath = Path.GetTempPath(),
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HandoverResponse>();
        Assert.True(body?.Accepted);
        Assert.Equal("xdir-target", body?.TargetSession?.SessionId);
        Assert.Equal(TargetDir, body?.TargetSession?.DirectorId);
        Assert.Equal("the source handover context", body?.ContextSent);

        Assert.Contains(_sourceCommands, c => c.Verb == "handover-context"); // read from the source over the tunnel
        Assert.Contains(_targetCommands, c => c.Verb == "create");           // spawn on the target over the tunnel
    }

}
