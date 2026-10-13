using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E-B): the wingman voice surface used to HTTP-dial the owning Director for
/// the session verbs (turns / buffer / prompt). It now resolves the owner from the push store and reaches it
/// through the tunnel. This boots a REAL streamMode <see cref="GatewayHost"/>, dials the REAL DirectorHub with a
/// REAL MessagePack client, PUSHES the session so the owner resolves with zero remote reach, and drives the
/// wingman menu endpoint - which reads the session terminal (the "buffer" verb) with no wingman-brain call when
/// the terminal is not a menu.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered UNREACHABLE and the session arrives ONLY via PushSnapshot,
/// so a 200 that reflects the Director's buffer answer can ONLY have ridden the tunnel - an HTTP dial to the
/// unreachable control endpoint would have failed and the owner would never have resolved from an HTTP pull.
/// This proves the GatewayWingmanVoiceEndpoint.Map wiring threads sendCommand + the push store correctly.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelWingmanVoiceProofTests : IAsyncLifetime
{
    private const string Token = "test-token-wingman-voice-proof";
    private const string DirectorId = "dir-wingman-voice";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-wmvproof-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;
    // Every command the Gateway sent over the tunnel. A test asks for the command of ITS OWN verb, never for "the last
    // command": the Gateway also sends commands of its own accord (a pushed waiting session has its screen read on a
    // background task), so which command arrived last depends on how busy the machine is.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _commands = new();

    // The most recent, not the only one: the verb this file asserts is "screen-grid", which the Gateway also sends of
    // its own accord for the session the test pushed, so two of them is an ordinary run.
    private DirectorCommand Sent(string verb)
    {
        var sent = _commands.LastOrDefault(c => c.Verb == verb);
        Assert.NotNull(sent);
        return sent!;
    }

    public TunnelWingmanVoiceProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-wmvproof-" + Guid.NewGuid().ToString("N"));
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
            TailnetEndpoint = "http://127.0.0.1:59922/", // nothing listens here
            MachineName = Environment.MachineName,
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

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Answer the session "screen-grid" verb with a plain (non-menu) live grid, so the wingman menu endpoint
    // returns {isMenu:false} WITHOUT calling the wingman brain - the tunnel read is the whole point here.
    // Since #1777 the LIVE screen grid is the authoritative read (the scrollback "buffer" verb is only read to
    // supplement extraction once the live screen already looks like a menu), so a plain grid short-circuits
    // before any buffer read.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        _commands.Enqueue(cmd);
        return cmd.Verb switch
        {
            "screen-grid" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                new ScreenGridResponse
                {
                    Rows = new List<string> { "just some plain terminal output, no menu here" },
                    HasGrid = true,
                    CursorRow = 0,
                    CursorCol = 0,
                }, Web)),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    [Fact]
    public async Task WingmanMenu_readsTheSessionBuffer_overTheTunnel()
    {
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, new SessionDto
        {
            SessionId = sid,
            Name = "a voice session",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });

        var resp = await _http.GetAsync($"sessions/{sid}/wingman/menu");
        // An HTTP dial to the unreachable Director would have failed to resolve the owner and read the screen.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(node?["isMenu"]?.GetValue<bool>());

        // The owner resolved from the push store and the LIVE screen was read via the tunnel "screen-grid" verb.
        var sent = Sent("screen-grid");
        Assert.Equal(sid, sent.SessionId);
    }

}
