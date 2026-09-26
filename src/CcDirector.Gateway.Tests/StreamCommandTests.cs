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
    public async Task SendCommandAsync_Prompt_RoundTripsAndExecutesOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "over-the-stream", AppendEnter = false });
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Contains("over-the-stream", BufferText(session));
    }

    [Fact]
    public async Task AutoDismiss_DoneVerdict_ClosesSessionOverTheStream_EndToEnd()
    {
        // Issue #1200 end-to-end: a real auto-dismiss session that declared "done" is closed by the Gateway's
        // auto-dismiss sweep sending the "kill" verb DOWN the real SignalR stream. This proves the WHOLE loop
        // over the live stream: the verdict rides UP on the pushed session DTO, the sweep addresses the owning
        // Director from the pushed cache, the kill flows DOWN, and the Director actually kills+removes the
        // session (no phantom - RemoveSession runs). No mocks of the channel; the same GatewayHost wiring.
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        session.AutoDismiss = true;
        session.SetDismissVerdict("done");   // what the Director's verdict watcher stamps from the CC-DISMISS block

        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        // The verdict actually reached the Gateway UP the stream, on the pushed DTO.
        var pushed = _gateway.PushedSessions.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30));
        var mine = pushed.Single(t => t.Session.SessionId == session.Id.ToString());
        Assert.True(mine.Session.AutoDismiss);
        Assert.Equal("done", mine.Session.DismissVerdict);

        // The real Gateway sweep, wired exactly as GatewayHost wires it (pushed snapshot + SendCommandAsync).
        var sweeper = new AutoDismissSweeper(
            () => _gateway.PushedSessions.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30)),
            _gateway.SendCommandAsync);

        var closed = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(1, closed);
        // The kill executed on the Director over the stream: the session is gone from the roster (killed+removed).
        Assert.Null(_directorSessions.GetSession(session.Id));
    }

    [Fact]
    public async Task AutoDismiss_NeedsHumanVerdict_LeavesSessionOpen_EndToEnd()
    {
        // The mirror case: a "needs-human" verdict must NOT close the session, even over a live stream.
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        session.AutoDismiss = true;
        session.SetDismissVerdict("needs-human");

        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        var sweeper = new AutoDismissSweeper(
            () => _gateway.PushedSessions.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30)),
            _gateway.SendCommandAsync);

        var closed = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(0, closed);
        Assert.NotNull(_directorSessions.GetSession(session.Id)); // still open
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
    public async Task GatewayPromptEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/prompt", new PromptRequest { Text = "endpoint-stream", AppendEnter = false });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.NotNull(body);
        Assert.True(body.Accepted);
        Assert.Contains("endpoint-stream", BufferText(session));
        // The spy sits ONLY on the stream down-channel. A count of 1 proves the endpoint delivered the
        // prompt over the stream; an HTTP fallback would have gone straight to the Director's own endpoint
        // and never touched this dispatcher.
        Assert.Equal(1, spy.CountOf("prompt"));
    }

    [Fact]
    public async Task SendCommandAsync_Interrupt_RoundTripsAndExecutesOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand { CommandId = Guid.NewGuid().ToString("N"), Verb = "interrupt", SessionId = session.Id.ToString() };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        // Claude's driver writes Ctrl+C (0x03) to the backend: proof the interrupt executed on the Director.
        Assert.Contains((byte)0x03, BufferRaw(session));
    }

    [Fact]
    public async Task SendCommandAsync_Escape_RoundTripsAndExecutesOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand { CommandId = Guid.NewGuid().ToString("N"), Verb = "escape", SessionId = session.Id.ToString() };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        // Claude's driver writes Esc (0x1B) to the backend: proof the escape executed on the Director.
        Assert.Contains((byte)0x1B, BufferRaw(session));
    }

    [Fact]
    public async Task GatewayInterruptEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsync($"sessions/{session.Id}/interrupt", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains((byte)0x03, BufferRaw(session));
        Assert.Equal(1, spy.CountOf("interrupt")); // delivered over the stream, not the Director's HTTP endpoint
    }

    [Fact]
    public async Task GatewayEscapeEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsync($"sessions/{session.Id}/escape", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains((byte)0x1B, BufferRaw(session));
        Assert.Equal(1, spy.CountOf("escape"));
    }

    [Fact]
    public async Task SendCommandAsync_Hold_RoundTripsAndSetsOnHold()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "hold",
            SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new HoldRequest { OnHold = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.True(session.OnHold);
        var body = JsonSerializer.Deserialize<HoldResponse>(result.BodyJson ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.True(body.OnHold);
    }

    [Fact]
    public async Task SendCommandAsync_Kill_RoundTripsAndRemovesTheSession()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var id = session.Id;
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand { CommandId = Guid.NewGuid().ToString("N"), Verb = "kill", SessionId = id.ToString() };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Null(_directorSessions.GetSession(id)); // killed + removed on the Director
    }

    [Fact]
    public async Task GatewayHoldEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/hold", new HoldRequest { OnHold = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.NotNull(body);
        Assert.True(body.OnHold);
        // The hold reached the Director DOWN THE STREAM (not over HTTP), so its raw hold is set. Round 4
        // finding 1: it now rides the single reliable display-state channel (set-display-state), not a second
        // direct hold command - so NO hold verb is sent, and the raw hold is applied from the folded value.
        Assert.True(session.OnHold);
        Assert.Equal(0, spy.CountOf("hold"));
        Assert.True(spy.CountOf("set-display-state") >= 1);
    }

    [Fact]
    public async Task GatewayKillEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var id = session.Id;
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.DeleteAsync($"sessions/{id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(_directorSessions.GetSession(id)); // removed over the stream
        Assert.Equal(1, spy.CountOf("kill"));
    }

    [Fact]
    public async Task SendCommandAsync_Patch_RoundTripsAndRenamesTheSession()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "patch",
            SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "Stream-Rename" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal("Stream-Rename", session.CustomName);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dto);
        Assert.Equal("Stream-Rename", dto.Name);
    }

    [Fact]
    public async Task GatewayPatchEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PatchAsJsonAsync($"sessions/{session.Id}", new SessionUpdateRequest { Name = "Endpoint-Rename" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(body);
        Assert.Equal("Endpoint-Rename", body.Name);       // the returned DTO carries the new name
        Assert.Equal(DirectorId, body.DirectorId);         // the Gateway stamped the DirectorId
        Assert.Equal("Endpoint-Rename", session.CustomName); // and it actually took effect on the Director
        Assert.Equal(1, spy.CountOf("patch"));               // delivered over the stream, not HTTP
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
    public async Task SendCommandAsync_WingmanGoal_RoundTripsAndSetsGoal()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions));
        client.Start();
        await WaitForStream();

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "wingman-goal",
            SessionId = session.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = "stream-goal" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        var result = await _gateway.SendCommandAsync(DirectorId, command);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.Equal("stream-goal", session.WingmanGoal); // set on the Director over the stream
        Assert.Contains("stream-goal", result.BodyJson ?? "");
    }

    [Fact]
    public async Task GatewayWingmanGoalEndpoint_RoutesDownTheStream_NotHttp()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/wingman/goal", new WingmanGoalRequest { Goal = "endpoint-goal" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("endpoint-goal", text);
        Assert.Equal("endpoint-goal", session.WingmanGoal); // took effect on the Director
        Assert.Equal(1, spy.CountOf("wingman-goal"));        // delivered over the stream, not HTTP
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
    public async Task PortlessDirector_PerSessionPrompt_LocatesViaPushedCache_AndRoutesDownTheStream()
    {
        // Phase 4a: a remotely-unreachable (portless) Director. Its control endpoint is empty, so the
        // HTTP-pull location path CANNOT find its sessions (mirrors tailscale-off, controlEndpoint=""). The
        // session is created BEFORE the stream connects so the initial snapshot carries it into the Gateway's
        // pushed cache - the ONLY way to locate it. The per-session prompt must resolve the owner from that
        // pushed cache and route DOWN the stream, with zero HTTP reach into the Director.
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());

        // Re-register dir-A with NO reachable endpoint (portless: nothing to pull).
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "",
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        // MTR-01: the HTTP-register path (Upsert) is Local-keyed; read it back through the tenant-scoped overload.
        Assert.Equal("", _gateway.Registry.Get(CcDirector.Core.Tenancy.TenantId.Local, DirectorId)?.ControlEndpoint);

        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/prompt",
            new PromptRequest { Text = "portless-stream", AppendEnter = false });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PromptResponse>();
        Assert.NotNull(body);
        Assert.True(body.Accepted);
        Assert.Contains("portless-stream", BufferText(session)); // took effect on the Director
        Assert.Equal(1, spy.CountOf("prompt")); // located from the pushed cache + delivered over the stream, zero HTTP
    }

    [Fact]
    public async Task PortlessDirector_PerSessionHold_LocatesViaPushedCache_AndRoutesDownTheStream()
    {
        // A second per-session verb over the portless path, proving location (not just prompt) is fixed.
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "",
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/hold", new HoldRequest { OnHold = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.NotNull(body);
        Assert.True(body.OnHold);
        // Located via the pushed cache and routed DOWN THE STREAM - the raw hold is set. Round 4 finding 1:
        // it rides the single reliable display-state channel (set-display-state), not a second hold command.
        Assert.True(session.OnHold);
        Assert.Equal(0, spy.CountOf("hold"));
        Assert.True(spy.CountOf("set-display-state") >= 1);
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

    [Fact]
    public async Task GatewaySetRoleEndpoint_RoutesDownTheStream_SetsExplicitRoleOnTheDirector()
    {
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var resp = await _http.PostAsJsonAsync($"sessions/{session.Id}/role", new SetRoleRequest { Role = "architect" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(dto);
        Assert.Equal("Architect", dto.ExplicitRole);      // the returned DTO carries the normalized role
        Assert.Equal("Architect", session.ExplicitRole);  // and it took effect on the Director
        Assert.Equal(1, spy.CountOf("set-role"));          // delivered over the stream, not the HTTP endpoint
    }

    [Fact]
    public async Task PeriodicRePush_KeepsPushedCacheFresh_ForAQuietSession()
    {
        // Phase 4a Fix 2: a quiet session (no deltas after the initial snapshot). With a SHORT re-push
        // interval, the Director keeps re-sending its full snapshot, so the Gateway's pushed cache stays
        // fresh against a stale window SHORTER than the total elapsed time - which only the periodic re-push
        // can achieve (portless has no HTTP pull floor).
        var session = _directorSessions.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        await using var client = NewClient(new CountingDispatcher(_directorSessions), rePushInterval: TimeSpan.FromMilliseconds(100));
        client.Start();
        await WaitForStream();
        await WaitForPushedSession(session.Id.ToString());

        // Elapsed ~1.2s at a 500ms stale window: without re-push the cache would go stale after ~500ms and
        // this loop would fail; the 100ms re-push keeps ReceivedAtUtc well inside the window throughout.
        var stale = TimeSpan.FromMilliseconds(500);
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(100);
            Assert.NotNull(_gateway.PushedSessions.TryGetFresh(TenantId.Local, DirectorId, stale));
        }
    }

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path (ConPty
    // spawn) without depending on an installed coding-agent CLI.
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    [Fact]
    public async Task GatewayCreateEndpoint_RoutesDownTheStream_CreatesRealSessionOnTheDirector()
    {
        var spy = new CountingDispatcher(_directorSessions);
        await using var client = NewClient(spy);
        client.Start();
        await WaitForStream();

        var before = _directorSessions.ListSessions().Count;
        var resp = await _http.PostAsJsonAsync($"directors/{DirectorId}/sessions", new NewSessionRequest
        {
            RepoPath = Path.GetTempPath(),
            Agent = "RawCli",
            Command = TestShellPath,
            Name = "stream-create-test",
            WingmanEnabled = true,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>();
        Assert.NotNull(body);
        Assert.Equal("RawCli", body.Agent);
        Assert.Equal("stream-create-test", body.Name);
        Assert.Equal(DirectorId, body.DirectorId);
        Assert.Equal(1, spy.CountOf("create")); // created over the stream, not the Director's HTTP endpoint

        // A real session now exists on the Director.
        Assert.Equal(before + 1, _directorSessions.ListSessions().Count);
        Assert.True(Guid.TryParse(body.SessionId, out var sid));
        Assert.NotNull(_directorSessions.GetSession(sid));
    }

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

    // A dispatcher that counts how many commands the stream delivered, then runs the real shared executor.
    /// <summary>
    /// Records every command the Director received off the stream, BY VERB.
    ///
    /// It used to expose only a total <c>Count</c>, and each test asserted <c>Assert.Equal(1, spy.Count)</c>
    /// to mean "this went down the STREAM, not the Director's HTTP endpoint" (their comments say exactly
    /// that). The contract was right; the total was a weak proxy for it that happened to equal 1 only
    /// because the protocol had exactly one command in flight at a time. Defect 5's FleetRoleObserver sends a
    /// legitimate <c>set-resolved-role</c> down the same stream, so the proxy broke - the tests were
    /// detecting their own shortcut expiring, not a defect.
    ///
    /// Counting totals was also a RACE, before defect 5 and worse after: the role stamp is fire-and-forget,
    /// so a total is 1 or 2 depending on timing. <see cref="CountOf"/> removes the race AND states the real
    /// claim - "the PROMPT went down the stream", not "some command did", which is strictly stronger and is
    /// what the comments always said. This repo already carries one undiagnosed flaky test; it is not
    /// getting a second.
    /// </summary>
    private sealed class CountingDispatcher
    {
        private readonly SessionManager _sessions;
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _verbs = new();
        public CountingDispatcher(SessionManager sessions) => _sessions = sessions;

        /// <summary>How many commands with this exact verb the Director received off the stream.</summary>
        public int CountOf(string verb) => _verbs.Count(v => string.Equals(v, verb, StringComparison.Ordinal));

        public Task<DirectorCommandResult> DispatchAsync(DirectorCommand command)
        {
            _verbs.Add(command.Verb);
            return SessionCommandExecutor.DispatchAsync(_sessions, DirectorId, command);
        }
    }

    private GatewayStreamClient NewClient(CountingDispatcher dispatcher, TimeSpan? rePushInterval = null)
    {
        var config = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = Token, StreamMode = true };
        return new GatewayStreamClient(config, DirectorId, "test", SnapshotDirectorSessions, dispatcher.DispatchAsync, rePushInterval);
    }

    // Map with the SAME production mapper the Director uses to push its roster UP the stream
    // (ControlApiHost.SnapshotFullSessions), so pushed DTOs carry every field a real Director sends -
    // including Status and the issue #1200 AutoDismiss / DismissVerdict fields the auto-dismiss sweep reads.
    private List<SessionDto> SnapshotDirectorSessions() =>
        _directorSessions.ListSessions().Select(s => ControlEndpoints.Map(s, DirectorId)).ToList();

    private static DirectorCommand PromptCommand(string sessionId, PromptRequest req) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        Verb = "prompt",
        SessionId = sessionId,
        PayloadJson = JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private static string BufferText(Session session) => Encoding.UTF8.GetString(BufferRaw(session));

    private static byte[] BufferRaw(Session session)
    {
        if (session.Buffer is null) throw new InvalidOperationException("session has no buffer");
        return session.Buffer.DumpAll();
    }

    // Poll the proven Ping down-channel until the stream is connected and bound (Hello sent).
    private async Task WaitForStream()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await _gateway.PingDirectorAsync(DirectorId, "up") == "pong:up") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for the Director stream to connect");
    }

    // Poll the Gateway's pushed cache until the Director's snapshot carrying this session has been applied.
    private async Task WaitForPushedSession(string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.PushedSessions.TryLocate(TenantId.Local, sessionId, TimeSpan.FromSeconds(30)) is not null) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for the session to appear in the pushed cache");
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { /* best effort */ }
    }

}
