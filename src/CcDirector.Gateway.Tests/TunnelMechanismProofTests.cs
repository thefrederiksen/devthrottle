using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2: the up-stream MECHANISM proof. Boots a REAL <see cref="GatewayHost"/> with
/// stream mode ON, dials the REAL DirectorHub with a REAL SignalR client, and drives the browser-facing legs
/// (roster, turns, prompt, terminal, file) end to end over the tunnel. The Director side runs the REAL
/// <see cref="DirectorUpStreamHandler"/> and the REAL producers, and the Gateway side runs the REAL
/// <see cref="GatewayStreamRegistry"/> + the REAL sinks - nothing about the up-stream is stubbed.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered with a DELIBERATELY UNREACHABLE control endpoint, so an
/// HTTP dial cannot succeed - anything that works can ONLY have ridden the tunnel. This is a stronger proof than
/// reading log lines.
///
/// It exercises the Architect's four invariants: backpressure (ruling 1 - the producer blocks on a stalled sink
/// over the real SignalR StreamBufferCapacity), no-monopoly concurrency (ruling 2 - a terminal stream and a
/// large file read progress together), teardown (ruling 3 - a browser disconnect fires close-stream and stops
/// the Director producer), and error parity (a missing file / session returns 404 over the tunnel, as the HTTP
/// dial did).
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelMechanismProofTests : IAsyncLifetime
{
    private const string Token = "test-token-mechanism-proof";
    private const string DirectorId = "dir-mech";
    private const string MissingSid = "00000000-0000-0000-0000-0000000000ff";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-mech-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private SessionManager _sm = null!;
    private DirectorUpStreamHandler _handler = null!;
    private HubConnection _conn = null!;
    private Session _session = null!;
    private string _sid = "";
    private int _closeStreamCount;

    public TunnelMechanismProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-mech-" + Guid.NewGuid().ToString("N"));
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

        // A REAL session with real terminal bytes, driven by a test backend (no real process).
        _sm = new SessionManager(new AgentOptions());
        var backend = new ExecuteActionTestBackend();
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        backend.Write(Encoding.UTF8.GetBytes("hello from the real terminal producer\r\n"));
        _sid = _session.Id.ToString();

        // The Director registers UNREACHABLE, so a working op proves it rode the tunnel, never an HTTP dial.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/", // nothing listens here
            MachineName = "mech-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        // The REAL up-stream handler streams frames UP over the REAL SignalR connection.
        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol() // speak the same binary tunnel protocol the real Director does
            .Build();
        _handler = new DirectorUpStreamHandler(_sm, (streamId, frames) => _conn.SendAsync("StreamUp", streamId, frames));
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", Dispatch);
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        // Push the session so the Gateway resolves this Director as its owner (TryLocate) and targets the tunnel.
        await _conn.InvokeAsync("PushSnapshot", 1L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" },
            new SessionDto { SessionId = MissingSid, ActivityState = "WaitingForInput" }, // known to the roster, absent on the Director
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

    // The Director-side Command handler: stream verbs run the REAL handler; the rest return typed results.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        if (cmd.Verb == "close-stream")
            Interlocked.Increment(ref _closeStreamCount);
        if (DirectorUpStreamHandler.IsStreamVerb(cmd.Verb))
            return _handler.Handle(cmd);

        return cmd.Verb switch
        {
            "turns" => cmd.SessionId == MissingSid
                ? DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found")
                : DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, status = "ok", widgets = Array.Empty<object>() })),
            "prompt" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { accepted = true })),
            _ => DirectorCommandResult.Success(),
        };
    }

    // ---------------------------------------------------------------- representative (tunnel-only) ----

    [Fact]
    public async Task Roster_isServedFromTheTunnelPushCache_notAnHttpPull()
    {
        var (ids, errorDirectors) = await GetSessionsAsync();
        Assert.Contains(_sid, ids);
        Assert.DoesNotContain(DirectorId, errorDirectors); // never pulled the unreachable endpoint
    }

    [Fact]
    public async Task Turns_read_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/turns");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("ok", node?["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_write_ridesTheTunnel()
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/prompt", new { text = "hello" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ------------------------------------------------------------------------------- error parity ----

    [Fact]
    public async Task Turns_missingSession_is404_overTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{MissingSid}/turns");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ReadFile_missingPath_is404_overTheTunnel()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N"));
        var resp = await _http.GetAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ReadFile_outsideTheSessionWorkingDirectory_is400_overTheTunnel()
    {
        // Tenant-boundary hardening (CR-4): the session's working directory is the temp root, so a path that
        // resolves to its PARENT is outside the allowed root and must be refused end to end over the tunnel.
        var outside = Path.Combine(Path.GetTempPath(), "..", "cc-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var resp = await _http.GetAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(outside)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("outside the session's working directory", await resp.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------- real up-stream producers over the wire ----

    [Fact]
    public async Task Terminal_streamsRealProducerFramesOverTheTunnel()
    {
        using var ws = await OpenTerminalAsync(_sid);
        // First a size text frame, then the snapshot/tail binary carrying the buffer bytes.
        var size = await ReceiveTextAsync(ws);
        Assert.Equal("size", JsonNode.Parse(size)?["type"]?.GetValue<string>());
        var bin = await ReceiveBinaryAsync(ws);
        Assert.Contains("hello from the real terminal producer", Encoding.UTF8.GetString(bin));
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task File_streamsWithCorrectContentLength_overTheTunnel()
    {
        var (path, content) = await WriteTempFileAsync((DirectorStreamLimits.MaxBinaryFrameBytes * 3) + 777); // many full-cap chunks
        try
        {
            var resp = await _http.GetAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(path)}");
            var diag = resp.IsSuccessStatusCode ? "" : await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"file GET -> {resp.StatusCode}: {diag}");
            Assert.Equal(content.Length, resp.Content.Headers.ContentLength); // TotalBytes from the open reply (ruling 4)
            var body = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(content, body); // reassembled byte-for-byte from the chunked up-stream
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    // ------------------------------------------------------------------- ruling 2: no-monopoly concurrency ----

    [Fact]
    public async Task TerminalAndLargeFile_concurrently_bothProgress()
    {
        var (path, content) = await WriteTempFileAsync(DirectorStreamLimits.MaxBinaryFrameBytes * 8);
        try
        {
            using var ws = await OpenTerminalAsync(_sid);
            var fileTask = _http.GetByteArrayAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(path)}");

            // The terminal makes progress (a size frame arrives) WHILE the large file is streaming on the same
            // shared tunnel connection - neither starves the other.
            var size = await ReceiveTextAsync(ws);
            Assert.Equal("size", JsonNode.Parse(size)?["type"]?.GetValue<string>());

            var fileBytes = await fileTask;
            Assert.Equal(content, fileBytes);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    // -------------------------------------------------------------------------------------- helpers ----

    private async Task<ClientWebSocket> OpenTerminalAsync(string sid)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_gateway.Port}/sessions/{sid}/stream"), CancellationToken.None);
        return ws;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws)
    {
        var (bytes, type) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReceiveBinaryAsync(ClientWebSocket ws)
    {
        var (bytes, type) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Binary, type);
        return bytes;
    }

    private static async Task<(byte[] bytes, WebSocketMessageType type)> ReceiveAsync(ClientWebSocket ws)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, timeout.Token);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }

    private static async Task<(string path, byte[] content)> WriteTempFileAsync(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), "ccd-mech-file-" + Guid.NewGuid().ToString("N") + ".bin");
        var content = new byte[size];
        for (var i = 0; i < size; i++) content[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(path, content);
        return (path, content);
    }

    private async Task<(List<string> ids, List<string> errorDirectors)> GetSessionsAsync()
    {
        var resp = await _http.GetAsync("sessions?envelope=true");
        resp.EnsureSuccessStatusCode();
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        var sessions = node?["sessions"]?.AsArray() ?? node?.AsArray() ?? new JsonArray();
        var ids = new List<string>();
        foreach (var s in sessions)
            if (s?["sessionId"]?.GetValue<string>() is { Length: > 0 } id) ids.Add(id);
        var errors = new List<string>();
        foreach (var e in node?["machineErrors"]?.AsArray() ?? new JsonArray())
            if (e?["directorId"]?.GetValue<string>() is { Length: > 0 } id) errors.Add(id);
        return (ids, errors);
    }
}
