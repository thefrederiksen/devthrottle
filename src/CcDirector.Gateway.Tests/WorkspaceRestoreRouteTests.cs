using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The restore as a Director act (the Message Load mission, slice 6; owner decision 2, 17 September 2026) on a REAL
/// booted HOSTED Gateway, with a tunnel Director connected on its OWN workstation device key - the credential a
/// real Director holds - and minted session keys.
///
/// WHAT ONLY A BOOTED HOST CAN PROVE:
///
///  - THE DIRECTOR ARM NAMES THE OWNER. The real <see cref="DirectorRestore"/>, over the real
///    <see cref="GatewayClient"/>, restores a captured workspace through the real spawn route, and the create that
///    reaches the Director names each seat's real owner - the restarted Manager by its NEW id.
///  - THE PIN STILL HOLDS. The same create sent with a session key is refused before anything reaches the Director.
///  - THE ROUTE, THE GUARD AND THE STAMP. A session key reaches <c>POST /gateway/workspaces/{id}/restore</c>, and
///    the order sent down the tunnel names the calling session as the one who asked - never a name from the body.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
public sealed class WorkspaceRestoreRouteTests : IAsyncLifetime
{
    private const string SharedToken = "workspace-restore-route-token";
    private const string DirectorId = "director-restore-route";
    private const string Machine = "RESTORE-MACHINE";
    private const string WorkspaceId = "drain-restore-route";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _manager = Guid.NewGuid().ToString();
    private readonly string _worker = Guid.NewGuid().ToString();
    private readonly string _outsideOwned = Guid.NewGuid().ToString();
    private readonly string _ownerElsewhere = Guid.NewGuid().ToString();
    private readonly string _restoringSession = Guid.NewGuid().ToString();

    private readonly ConcurrentQueue<DirectorCommand> _commands = new();
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-workspace-restore-route-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _director = null!;
    private TenantId _tenant;
    private string _directorKey = "";
    private HttpClient _asDirector = null!;
    private HttpClient _asOwner = null!;
    private HttpClient _asRestoringSession = null!;
    private string? _priorHosted;
    private Func<DirectorCommand, DirectorCommandResult>? _restoreAnswer;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subject = $"sub-restore-{_runId}";
        var device = HostedTestEnrollment.Enroll(_gateway, subject, $"restore-{_runId}@example.com", $"dev-restore-{_runId}", Machine);
        _tenant = device.Tenant;
        _directorKey = device.DeviceKey;
        var owner = _gateway.Devices.RegisterForTenant(_tenant, subject, $"dev-restore-owner-{_runId}", "OWNER", deviceType: "browser");

        _asDirector = Client(_directorKey);
        _asOwner = Client(owner.DeviceKey);
        _asRestoringSession = Client(SessionKey(_restoringSession));

        _director = await FakeTunnelDirector.StartAsync(_gateway, _directorKey, DirectorId, Machine, cmd =>
        {
            _commands.Enqueue(cmd);
            if (cmd.Verb == "create")
            {
                var req = JsonSerializer.Deserialize<NewSessionRequest>(cmd.PayloadJson!, Web)!;
                return FakeTunnelDirector.Ok(new SessionDto { SessionId = Guid.NewGuid().ToString(), Name = req.Name, DirectorId = DirectorId });
            }
            if (cmd.Verb == WorkspaceRestoreVerbs.Restore && _restoreAnswer is not null)
                return _restoreAnswer(cmd);
            return FakeTunnelDirector.Ok(new { accepted = true });
        });

        await _director.PushSnapshotAsync(
            Row(_manager, "Restore - Manager", controller: null, role: "Manager"),
            Row(_worker, "Restore - Worker", controller: _manager, role: "Worker"),
            Row(_outsideOwned, "Owned from elsewhere", controller: _ownerElsewhere, role: "Worker"),
            Row(_restoringSession, "Restore - driver", controller: null, role: "Standalone"));
        await CaptureAndDecideAsync();
    }

    /// <summary>What the drain's closes leave on the roster: the driver, and the owner that lives outside this
    /// workspace. The drained seats are gone (inspection 7, ruling 2: only a drained seat comes back).</summary>
    private Task DrainClosedTheSeatsAsync()
        => _director.PushSnapshotAsync(
            Row(_restoringSession, "Restore - driver", controller: null, role: "Standalone"),
            Row(_ownerElsewhere, "Owner elsewhere", controller: null, role: "Manager"));

    private GatewayClient DirectorClient(string directorId = DirectorId)
        => new(new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = _directorKey }, directorId, "test");

    /// <summary>Ask for the restore through the route, as a session does, with the fake Director answering
    /// "taken" - which is what grants this Director the workspace's restore lease.</summary>
    private async Task LeaseThroughTheRouteAsync(HttpClient who, string directorId = DirectorId)
    {
        _restoreAnswer = Taken;
        var r = await AskRestore(who, $"{{\"directorId\":\"{directorId}\"}}");
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        foreach (var c in new[] { _asDirector, _asOwner, _asRestoringSession }) c.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private static SessionDto Row(string sid, string name, string? controller, string role) => new()
    {
        SessionId = sid,
        DirectorId = DirectorId,
        Name = name,
        MachineName = Machine,
        RepoPath = "/repos/devthrottle",
        Agent = "ClaudeCode",
        SessionRole = role,
        ActivityState = "WaitingForInput",
        ControllerSessionId = controller,
        LastActivityAt = DateTime.UtcNow,
    };

    private string SessionKey(string sid)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenant, DirectorId, sid, GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    /// <summary>What a drain leaves: the capture, then every seat but the driver drained and decided "restore".</summary>
    private async Task CaptureAndDecideAsync()
    {
        var captured = await _asDirector.PostAsJsonAsync("gateway/workspaces",
            new WorkspaceCaptureRequest { Id = WorkspaceId, Name = "Restore route", DirectorId = DirectorId, Reason = "test" });
        Assert.Equal(HttpStatusCode.Created, captured.StatusCode);
        var doc = (await captured.Content.ReadFromJsonAsync<WorkspaceDocument>(Web))!;

        var owed = new List<string>();
        foreach (var seat in doc.Seats)
        {
            var restore = seat.SessionId != _restoringSession;
            seat.DrainState = "drained";
            seat.HandoverPath = $"/handovers/{seat.SessionId}.md";
            seat.ClosedAtUtc = DateTime.UtcNow;
            seat.Restore = new WorkspaceSeatRestore
            {
                Decision = restore ? WorkspaceRestoreDecisions.Restore : WorkspaceRestoreDecisions.Close,
                Why = "test",
                Command = restore ? DrainRestoreCommand.Build(WorkspaceId, seat.SessionId!) : null,
            };
            if (restore) owed.Add(seat.SessionId!);
        }
        // Seniors first, as the drain writes it - the Director does not rely on it, the validation wants it listed.
        doc.RestoreAfterRestart = owed;
        var put = await _asDirector.PutAsJsonAsync($"gateway/workspaces/{WorkspaceId}", doc);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private NewSessionRequest[] CreatesSent() => _commands
        .Where(c => c.Verb == "create")
        .Select(c => JsonSerializer.Deserialize<NewSessionRequest>(c.PayloadJson!, Web)!)
        .ToArray();

    private async Task<WorkspaceDocument> StoredAsync()
        => (await _asOwner.GetFromJsonAsync<WorkspaceDocument>($"gateway/workspaces/{WorkspaceId}", Web))!;

    // =========================================================================================
    // The Director arm names the owner
    // =========================================================================================

    [Fact]
    public async Task A_Director_restoring_controlled_seats_starts_each_under_its_real_owner()
    {
        await DrainClosedTheSeatsAsync();
        await LeaseThroughTheRouteAsync(_asRestoringSession);
        using var client = DirectorClient();
        var restore = new DirectorRestore(new GatewayClientRestoreGateway(client), DirectorId);

        var result = await restore.RunAsync(new WorkspaceRestoreOrder { WorkspaceId = WorkspaceId, RequestedBySessionId = _restoringSession });

        Assert.All(result.Seats, s => Assert.Null(s.Failure));
        var creates = CreatesSent();
        Assert.Equal(3, creates.Length);

        // The Manager came back first and is the user's; the Worker is owned by the Manager's NEW id - the
        // placeholder, resolved - and the seat owned from another Director keeps that owner.
        var stored = (await StoredAsync()).Seats.ToDictionary(s => s.SessionId!);
        var newManager = stored[_manager].RestoredSessionId;
        Assert.False(string.IsNullOrWhiteSpace(newManager));
        Assert.NotEqual(_manager, newManager);

        Assert.Equal("Restore - Manager", creates[0].Name);
        Assert.Null(creates[0].ControllerSessionId);
        Assert.Equal(newManager, creates.Single(c => c.Name == "Restore - Worker").ControllerSessionId);
        Assert.Equal(_ownerElsewhere, creates.Single(c => c.Name == "Owned from elsewhere").ControllerSessionId);

        // Who asked is recorded as the parent, never as the owner.
        Assert.All(creates, c => Assert.Equal(_restoringSession, c.ParentSessionId));
        Assert.All(creates, c => Assert.Equal("agent", c.Origin));

        Assert.False(string.IsNullOrWhiteSpace(stored[_worker].RestoredSessionId));
        Assert.False(string.IsNullOrWhiteSpace(stored[_outsideOwned].RestoredSessionId));
        Assert.Null(stored[_restoringSession].RestoredSessionId);

        // Every create carried its seat's token, and the run gave the lease back.
        Assert.All(creates, c => Assert.False(string.IsNullOrWhiteSpace(c.RestoreClaim?.Token)));
        Assert.Null((await StoredAsync()).RestoreLease);
    }

    // =========================================================================================
    // Inspection 7, ruling 1: what a restore did is written only by the Director holding the lease
    // =========================================================================================

    [Fact]
    public async Task A_session_key_cannot_name_the_owner_of_a_restored_worker_by_writing_the_bosses_restored_id()
    {
        // The inspector's sequence on a real host: an unrelated session in the account PUTs the Manager's
        // restoredSessionId to a live session X, sets the Worker's decision, and asks for the Worker alone.
        var unrelated = Client(SessionKey(Guid.NewGuid().ToString()));
        var x = _restoringSession;
        await DrainClosedTheSeatsAsync();

        var doc = (await unrelated.GetFromJsonAsync<WorkspaceDocument>($"gateway/workspaces/{WorkspaceId}", Web))!;
        doc.Seats.Single(s => s.SessionId == _manager).RestoredSessionId = x;
        var put = await unrelated.PutAsJsonAsync($"gateway/workspaces/{WorkspaceId}", doc);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await StoredAsync()).Seats.Single(s => s.SessionId == _manager).RestoredSessionId);

        await LeaseThroughTheRouteAsync(unrelated);
        using var client = DirectorClient();
        var workerOnly = await new DirectorRestore(new GatewayClientRestoreGateway(client), DirectorId)
            .RunAsync(new WorkspaceRestoreOrder { WorkspaceId = WorkspaceId, Seats = new() { _worker } });
        Assert.Contains("has not been brought back yet", Assert.Single(workerOnly.Seats).Failure);
        Assert.Empty(CreatesSent());

        // The owner really comes back, and only then the Worker - under the Manager's restored id, never X.
        await LeaseThroughTheRouteAsync(unrelated);
        await new DirectorRestore(new GatewayClientRestoreGateway(client), DirectorId)
            .RunAsync(new WorkspaceRestoreOrder { WorkspaceId = WorkspaceId, Seats = new() { _manager, _worker } });
        var newManager = (await StoredAsync()).Seats.Single(s => s.SessionId == _manager).RestoredSessionId;
        Assert.NotEqual(x, newManager);
        Assert.Equal(newManager, CreatesSent().Single(c => c.Name == "Restore - Worker").ControllerSessionId);
        unrelated.Dispose();
    }

    [Fact]
    public async Task Restore_marks_are_refused_to_a_session_key_to_the_owners_browser_and_to_a_Director_without_the_lease()
    {
        var mark = new WorkspaceRestoreMark
        {
            DirectorId = DirectorId, Kind = WorkspaceRestoreMarkKinds.Restored, SeatSessionId = _manager, RestoredSessionId = _restoringSession,
        };

        var asSession = await _asRestoringSession.PostAsJsonAsync($"gateway/workspaces/{WorkspaceId}/restore/marks", mark);
        var asOwner = await _asOwner.PostAsJsonAsync($"gateway/workspaces/{WorkspaceId}/restore/marks", mark);
        var asDirectorWithoutLease = await _asDirector.PostAsJsonAsync($"gateway/workspaces/{WorkspaceId}/restore/marks", mark);

        Assert.Equal(HttpStatusCode.Forbidden, asSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, asDirectorWithoutLease.StatusCode);
        Assert.Contains("restore lease", await asDirectorWithoutLease.Content.ReadAsStringAsync());
        Assert.Null((await StoredAsync()).Seats.Single(s => s.SessionId == _manager).RestoredSessionId);
    }

    // =========================================================================================
    // Inspection 7, ruling 2: only a drained seat comes back
    // =========================================================================================

    [Fact]
    public async Task A_captured_seat_still_running_is_refused_and_starts_once_it_has_closed()
    {
        // Captured while running, decided "restore", and asked for - with the seat still on the roster.
        await LeaseThroughTheRouteAsync(_asRestoringSession);
        using var client = DirectorClient();
        var order = new WorkspaceRestoreOrder { WorkspaceId = WorkspaceId, Seats = new() { _manager } };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DirectorRestore(new GatewayClientRestoreGateway(client), DirectorId).PrepareAsync(order));
        Assert.Contains("still running", refused.Message);
        var ran = await new DirectorRestore(new GatewayClientRestoreGateway(client), DirectorId).RunAsync(order);
        Assert.Contains("still running", Assert.Single(ran.Seats).Failure);
        Assert.Empty(CreatesSent());

        await DrainClosedTheSeatsAsync();
        await LeaseThroughTheRouteAsync(_asRestoringSession);
        var started = await new DirectorRestore(new GatewayClientRestoreGateway(client), DirectorId).RunAsync(order);
        Assert.Null(Assert.Single(started.Seats).Failure);
        Assert.Equal("Restore - Manager", Assert.Single(CreatesSent()).Name);
    }

    // =========================================================================================
    // Inspection 7, ruling 3: the spawn door records a restore's create by its token
    // =========================================================================================

    [Fact]
    public async Task The_spawn_door_records_the_created_session_on_the_seat_whose_token_it_carries()
    {
        await DrainClosedTheSeatsAsync();
        await LeaseThroughTheRouteAsync(_asRestoringSession);
        using var client = DirectorClient();
        await client.RecordRestoreMarkAsync(WorkspaceId, new WorkspaceRestoreMark
        {
            DirectorId = DirectorId, Kind = WorkspaceRestoreMarkKinds.Started, SeatSessionId = _manager, Token = "tok-1",
        });

        // The Director never writes the result - as if its answer was lost - and the Gateway has recorded it anyway.
        var created = await client.SpawnOnThisDirectorAsync(new NewSessionRequest
        {
            RepoPath = "/repos/devthrottle", Agent = "ClaudeCode", Name = "Restore - Manager",
            RestoreClaim = new WorkspaceRestoreClaim { WorkspaceId = WorkspaceId, SeatSessionId = _manager, Token = "tok-1" },
        });

        Assert.Equal(created.SessionId, (await StoredAsync()).Seats.Single(s => s.SessionId == _manager).RestoredSessionId);
    }

    [Fact]
    public async Task A_restore_claim_from_a_session_key_or_a_person_is_refused_and_nothing_reaches_the_Director()
    {
        var claim = new { workspaceId = WorkspaceId, seatSessionId = _manager, token = "tok-1" };

        var asSession = await _asRestoringSession.PostAsJsonAsync($"directors/{DirectorId}/sessions", new
        {
            repoPath = "/repos/devthrottle", agent = "ClaudeCode", name = "x", controllerSessionId = "none", restoreClaim = claim,
        });
        var asOwner = await _asOwner.PostAsJsonAsync($"directors/{DirectorId}/sessions", new
        {
            repoPath = "/repos/devthrottle", agent = "ClaudeCode", name = "x", restoreClaim = claim,
        });

        Assert.Equal(HttpStatusCode.Forbidden, asSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
        Assert.Empty(CreatesSent());
    }

    // =========================================================================================
    // Inspection 7, ruling 4: one Director restores a workspace at a time
    // =========================================================================================

    [Fact]
    public async Task Two_Directors_on_the_captured_machine_get_one_lease()
    {
        const string second = "director-restore-second";
        var toSecond = new ConcurrentQueue<DirectorCommand>();
        await using var other = await FakeTunnelDirector.StartAsync(_gateway, _directorKey, second, Machine,
            cmd => { toSecond.Enqueue(cmd); return Taken(cmd); });

        await LeaseThroughTheRouteAsync(_asRestoringSession);
        var r = await AskRestore(_asOwner, $"{{\"directorId\":\"{second}\"}}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains(DirectorId, await r.Content.ReadAsStringAsync());
        Assert.DoesNotContain(toSecond, c => c.Verb == WorkspaceRestoreVerbs.Restore);
        Assert.Equal(DirectorId, (await StoredAsync()).RestoreLease!.DirectorId);
    }

    [Fact]
    public async Task A_restore_the_Director_refuses_gives_back_the_lease_it_was_granted()
    {
        _restoreAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "no seat left to bring back");

        var r = await AskRestore(_asRestoringSession, $"{{\"directorId\":\"{DirectorId}\"}}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Null((await StoredAsync()).RestoreLease);
    }

    [Fact]
    public async Task The_same_create_from_a_session_key_is_refused_and_nothing_reaches_the_Director()
    {
        // Exactly the spawn the Director makes for the Worker, naming the Manager as owner - sent with the
        // restoring session's own key. The pin (a session key may name only itself or the user) still holds.
        var r = await _asRestoringSession.PostAsJsonAsync($"directors/{DirectorId}/sessions", new
        {
            repoPath = "/repos/devthrottle",
            agent = "ClaudeCode",
            name = "Restore - Worker",
            role = "Worker",
            controllerSessionId = _manager,
            prePrompt = "Read /handovers/x.md",
        });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains(_manager, await r.Content.ReadAsStringAsync());
        Assert.Empty(CreatesSent());
    }

    // =========================================================================================
    // The route: who asked is stamped, the Director's answer is relayed
    // =========================================================================================

    private Task<HttpResponseMessage> AskRestore(HttpClient who, string json)
        => who.PostAsync($"gateway/workspaces/{WorkspaceId}/restore", new StringContent(json, Encoding.UTF8, "application/json"));

    private DirectorCommandResult Taken(DirectorCommand cmd)
    {
        var order = JsonSerializer.Deserialize<WorkspaceRestoreOrder>(cmd.PayloadJson!, Web)!;
        return FakeTunnelDirector.Ok(new WorkspaceRestoreAccepted
        {
            Taken = true, WorkspaceId = order.WorkspaceId, DirectorId = DirectorId, Seats = order.Seats ?? new List<string>(),
        });
    }

    [Fact]
    public async Task A_session_key_asks_for_a_restore_and_the_order_names_that_session_whatever_the_body_says()
    {
        _restoreAnswer = Taken;

        // The body tries to name somebody else as the one asking. There is no such field; it is ignored.
        var r = await AskRestore(_asRestoringSession,
            $"{{\"directorId\":\"{DirectorId}\",\"seats\":[\"{_worker}\"],\"seeds\":{{\"{_worker}\":\"/index/SEED.md\"}},\"requestedBySessionId\":\"{_manager}\"}}");

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var cmd = Assert.Single(_commands, c => c.Verb == WorkspaceRestoreVerbs.Restore);
        var order = JsonSerializer.Deserialize<WorkspaceRestoreOrder>(cmd.PayloadJson!, Web)!;
        Assert.Equal(WorkspaceId, order.WorkspaceId);
        Assert.Equal(_restoringSession, order.RequestedBySessionId);
        Assert.Equal(new[] { _worker }, order.Seats);
        Assert.Equal("/index/SEED.md", order.Seeds![_worker]);

        var accepted = (await r.Content.ReadFromJsonAsync<WorkspaceRestoreAccepted>(Web))!;
        Assert.True(accepted.Taken);
        Assert.Equal(new[] { _worker }, accepted.Seats);
    }

    [Fact]
    public async Task The_owner_asks_for_a_restore_and_the_order_names_nobody()
    {
        _restoreAnswer = Taken;

        var r = await AskRestore(_asOwner, $"{{\"directorId\":\"{DirectorId}\"}}");

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var order = JsonSerializer.Deserialize<WorkspaceRestoreOrder>(
            Assert.Single(_commands, c => c.Verb == WorkspaceRestoreVerbs.Restore).PayloadJson!, Web)!;
        Assert.Null(order.RequestedBySessionId);
        Assert.Null(order.Seats);
    }

    [Fact]
    public async Task A_restore_the_Director_refuses_is_answered_with_its_reason()
    {
        _restoreAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "workspace 'x' has no seat left to bring back");

        var r = await AskRestore(_asRestoringSession, $"{{\"directorId\":\"{DirectorId}\"}}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("no seat left to bring back", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_restore_onto_a_Director_on_another_machine_is_refused_before_anything_is_sent()
    {
        const string otherDirector = "director-restore-elsewhere";
        await using var elsewhere = await FakeTunnelDirector.StartAsync(_gateway, _directorKey, otherDirector, "OTHER-MACHINE",
            cmd => { _commands.Enqueue(cmd); return FakeTunnelDirector.Ok(new { }); });

        var r = await AskRestore(_asRestoringSession, $"{{\"directorId\":\"{otherDirector}\"}}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains(Machine, await r.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_commands, c => c.Verb == WorkspaceRestoreVerbs.Restore);
    }

    [Fact]
    public async Task A_restore_naming_no_Director_is_a_bad_request()
    {
        var r = await AskRestore(_asRestoringSession, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain(_commands, c => c.Verb == WorkspaceRestoreVerbs.Restore);
    }
}
