using System.Collections.Concurrent;
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
/// Issue #1765 - the fleet-wide voice switch. POST /sessions/voice-mode/all walks the whole roster and, for
/// each session, sends the owning Director the same "voice-mode" verb the per-session path sends, then marks
/// (or unmarks) the session on the Gateway. This boots a REAL streamMode <see cref="GatewayHost"/>, dials the
/// REAL DirectorHub with a REAL MessagePack client, PUSHES the sessions so their owner resolves with zero
/// remote reach, and drives the batch endpoint.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered UNREACHABLE and the sessions arrive ONLY via
/// PushSnapshot, so a session that reports "on" can only have ridden the tunnel - an HTTP dial to the
/// unreachable control endpoint would have failed and the owner would never have resolved. The two tests
/// prove the happy path (every session switched, one voice-mode command per session carrying the enabled
/// flag) and the skip path (a session whose Director rejects the write is skipped and reported, never
/// failing the batch).
/// </summary>
[Collection("DirectorRoot")]
public sealed class VoiceModeAllEndpointProofTests : IAsyncLifetime
{
    private const string Token = "test-token-voice-mode-all-proof";
    private const string DirectorId = "dir-voice-mode-all";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-vmall-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    // Every command the fake Director received, captured concurrently (the fan-out sends them in parallel).
    private readonly ConcurrentBag<DirectorCommand> _commands = new();
    // Session ids the fake Director should REJECT the voice-mode write for (to prove the skip path).
    private readonly HashSet<string> _rejectSids = new(StringComparer.Ordinal);

    public VoiceModeAllEndpointProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-vmall-" + Guid.NewGuid().ToString("N"));
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
            TailnetEndpoint = "http://127.0.0.1:59923/", // nothing listens here
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

    // The fake Director accepts the voice-mode write (Success) for every session except those in _rejectSids,
    // which it fails - proving a rejected session is reported skipped without failing the whole batch.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        _commands.Add(cmd);
        if (cmd.Verb != "voice-mode")
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        if (_rejectSids.Contains(cmd.SessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");
        return DirectorCommandResult.Success("{}");
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    private static SessionDto Session(string sid, string name) => new()
    {
        SessionId = sid,
        Name = name,
        Status = "WaitingForInput",
        ActivityState = "WaitingForInput",
        RepoPath = @"D:\repo",
    };

    [Fact]
    public async Task VoiceModeAll_enable_switchesEverySession_overTheTunnel()
    {
        var sid1 = Guid.NewGuid().ToString();
        var sid2 = Guid.NewGuid().ToString();
        await PushAsync(1L, Session(sid1, "one"), Session(sid2, "two"));

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["enabled"]?.GetValue<bool>());
        Assert.Equal(2, node?["total"]?.GetValue<int>());
        Assert.Equal(2, node?["changed"]?.GetValue<int>());
        Assert.Equal(0, node?["skipped"]?.GetValue<int>());

        // Exactly one voice-mode write per session, each carrying enabled=true, and they rode the tunnel
        // (an HTTP dial to the unreachable Director could never have resolved the owner).
        var voiceModeCmds = _commands.Where(c => c.Verb == "voice-mode").ToList();
        Assert.Equal(2, voiceModeCmds.Count);
        Assert.Contains(voiceModeCmds, c => c.SessionId == sid1);
        Assert.Contains(voiceModeCmds, c => c.SessionId == sid2);
        foreach (var c in voiceModeCmds)
            Assert.True(JsonNode.Parse(c.PayloadJson)?["enabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task VoiceModeAll_enable_leavesASessionAnotherLiveSessionOwnsAlone()
    {
        // Owner, 2026-09-27: "voice mode should only be the ones that are owned by the user". The button used to
        // switch every row on, workers included, and report them as changed. A worker under a LIVE manager is read
        // by that manager, so it gets no voice-mode write at all, and is not counted.
        var manager = Guid.NewGuid().ToString();
        var worker = Guid.NewGuid().ToString();
        var workerRow = Session(worker, "worker");
        workerRow.IsControlled = true;
        workerRow.ControllerSessionId = manager;
        var managerRow = Session(manager, "manager");
        managerRow.Status = "Working";
        managerRow.ActivityState = "Working";
        await PushAsync(1L, managerRow, workerRow);

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(1, node?["total"]?.GetValue<int>());
        Assert.Equal(1, node?["changed"]?.GetValue<int>());
        var voiceModeCmds = _commands.Where(c => c.Verb == "voice-mode").ToList();
        Assert.Single(voiceModeCmds);
        Assert.Equal(manager, voiceModeCmds[0].SessionId);
    }

    [Fact]
    public async Task VoiceModeAll_disable_stillReachesEverySession()
    {
        // Off is always safe and must clear every mark, including one a worker picked up before this rule.
        var manager = Guid.NewGuid().ToString();
        var worker = Guid.NewGuid().ToString();
        var workerRow = Session(worker, "worker");
        workerRow.IsControlled = true;
        workerRow.ControllerSessionId = manager;
        var managerRow = Session(manager, "manager");
        managerRow.Status = "Working";
        managerRow.ActivityState = "Working";
        await PushAsync(1L, managerRow, workerRow);

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(2, node?["total"]?.GetValue<int>());
        var sids = _commands.Where(c => c.Verb == "voice-mode").Select(c => c.SessionId).ToHashSet();
        Assert.Contains(manager, sids);
        Assert.Contains(worker, sids);
    }

    [Fact]
    public async Task VoiceModeAll_skipsASessionWhoseDirectorRejects_withoutFailingTheBatch()
    {
        var ok = Guid.NewGuid().ToString();
        var rejected = Guid.NewGuid().ToString();
        _rejectSids.Add(rejected);
        await PushAsync(1L, Session(ok, "keeps"), Session(rejected, "rejects"));

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(2, node?["total"]?.GetValue<int>());
        Assert.Equal(1, node?["changed"]?.GetValue<int>());
        Assert.Equal(1, node?["skipped"]?.GetValue<int>());

        // The per-session report names the rejected one as not-ok with a reason, and the other as ok.
        var sessions = node?["sessions"]?.AsArray() ?? new JsonArray();
        var okRow = sessions.FirstOrDefault(s => s?["sessionId"]?.GetValue<string>() == ok);
        var rejectedRow = sessions.FirstOrDefault(s => s?["sessionId"]?.GetValue<string>() == rejected);
        Assert.True(okRow?["ok"]?.GetValue<bool>());
        Assert.False(rejectedRow?["ok"]?.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(rejectedRow?["reason"]?.GetValue<string>()));
    }

    // ---- voice mode as a STANDING SWITCH, not a one-time fan-out (owner, 2026-07-24) --------------------

    [Fact]
    public async Task VoiceModeAll_persistsTheIntent_soAClientCanReadWhichStateItIsIn()
    {
        // Before anyone asks for it, voice mode is off. This is the question clients used to answer for
        // themselves by scanning the roster for any marked session - a DIFFERENT question, true the moment
        // one session was on and still true while nine others were off.
        var before = await _http.GetFromJsonAsync<JsonNode>("sessions/voice-mode/all");
        Assert.False(before?["enabled"]?.GetValue<bool>());

        await PushAsync(1L, Session(Guid.NewGuid().ToString(), "one"));
        (await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true })).EnsureSuccessStatusCode();

        var after = await _http.GetFromJsonAsync<JsonNode>("sessions/voice-mode/all");
        Assert.True(after?["enabled"]?.GetValue<bool>());

        (await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = false })).EnsureSuccessStatusCode();
        var off = await _http.GetFromJsonAsync<JsonNode>("sessions/voice-mode/all");
        Assert.False(off?["enabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task VoiceModeAll_recordsTheIntentEvenWhenTheFanOutCouldNotReachASession()
    {
        // The one session there is cannot be written. If the intent were recorded only on a fully successful
        // fan-out, "my fleet is on voice" would silently downgrade to "the reachable part of it is" - and the
        // sweep would never come back for the session that was skipped.
        var unreachable = Guid.NewGuid().ToString();
        _rejectSids.Add(unreachable);
        await PushAsync(1L, Session(unreachable, "offline computer"));

        var resp = await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true });
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(0, node?["changed"]?.GetValue<int>());
        Assert.Equal(1, node?["skipped"]?.GetValue<int>());

        var state = await _http.GetFromJsonAsync<JsonNode>("sessions/voice-mode/all");
        Assert.True(state?["enabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Sweep_switchesOnASessionThatAPPEAREDAFTERTheSwitchWasThrown()
    {
        // THE BUG THIS EXISTS TO KILL. The fan-out reaches the sessions alive at that instant. A session
        // created a minute later was never told, so it quietly never joined the voice queue - which looked
        // like the queue losing sessions rather than what it was.
        var existing = Guid.NewGuid().ToString();
        await PushAsync(1L, Session(existing, "was here first"));
        (await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true })).EnsureSuccessStatusCode();
        Assert.Single(_commands, c => c.Verb == "voice-mode");

        // A NEW session joins the roster. Nobody has told it anything.
        var born = Guid.NewGuid().ToString();
        await PushAsync(2L, Session(existing, "was here first"), Session(born, "born later"));

        await _gateway.SweepVoiceModeAllAsync();

        // The sweep sent the new one the voice-mode verb, and did NOT re-send it to the session that was
        // already on - a steady fleet must produce no traffic.
        var cmds = _commands.Where(c => c.Verb == "voice-mode").ToList();
        Assert.Equal(2, cmds.Count);
        Assert.Contains(cmds, c => c.SessionId == born);
        Assert.True(JsonNode.Parse(cmds.Single(c => c.SessionId == born).PayloadJson)?["enabled"]?.GetValue<bool>());

        // And it is now genuinely a voice session on the Gateway, not merely commanded.
        Assert.True(_gateway.VoiceService?.IsVoiceSession(Core.Tenancy.TenantId.Local, born));
    }

    [Fact]
    public async Task Sweep_doesNothingAtAllWhenVoiceModeWasNeverTurnedOn()
    {
        // The other direction of the guard: the sweep must never put a fleet on voice that did not ask for
        // it. Without this, merely running the Gateway would start spending narration on every session.
        await PushAsync(1L, Session(Guid.NewGuid().ToString(), "left alone"));

        await _gateway.SweepVoiceModeAllAsync();

        Assert.DoesNotContain(_commands, c => c.Verb == "voice-mode");
    }

    [Fact]
    public async Task Sweep_doesNotSwitchSessionsBackOnAfterVoiceModeIsTurnedOff()
    {
        // Turning voice mode off has to STAY off. A sweep that kept reading a stale intent would switch the
        // fleet straight back on, which is the "I turned it off and it kept coming back" failure in its
        // purest form.
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, Session(sid, "one"));
        (await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = true })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("sessions/voice-mode/all", new { enabled = false })).EnsureSuccessStatusCode();
        var beforeSweep = _commands.Count(c => c.Verb == "voice-mode");

        await _gateway.SweepVoiceModeAllAsync();

        Assert.Equal(beforeSweep, _commands.Count(c => c.Verb == "voice-mode"));
        Assert.False(_gateway.VoiceService?.IsVoiceSession(Core.Tenancy.TenantId.Local, sid));
    }

}
