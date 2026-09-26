using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Fleet Manager's events (the Fleet Manager mission, step 4) on a REAL booted hosted Gateway. What ONLY a booted
/// host proves is THE WIRING: that the host's turn-end fan-out, its exit and removal callbacks, the Wingman seat's
/// reading-completed notice and the reconcile each reach the events service. Every test drives the host's own
/// turn-end watcher - the object the Director stream feeds - so turning off one of those hooks in GatewayHost turns a
/// test here red. The events are read back from the host's own store; the acknowledge authority is called through
/// the real middleware with real credentials.
///
/// The Wingman's judge switch is left at its default (off), so a reading ends at once with that reason and no model
/// is asked.
///
/// NOT PROVEN HERE: the Director stream hub's own call into the watcher (a one-line forward in DirectorHub), and the
/// Director typing the prompt (<c>FleetManagerEventServiceTests</c> drives the Director's real prompt core).
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetManagerEventsHostTests : IAsyncLifetime
{
    private const string SharedToken = "fleet-manager-events-host-token";
    private const string DirectorId = "director-fm-events";

    private readonly ITestOutputHelper _out;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-fm-events-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-fm-events-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private string? _priorRoot;
    private bool _priorSweep;

    private GatewayHost _gateway = null!;
    private TenantId _tenant;
    private string _subject = "";
    private HttpClient _owner = null!;
    private long _pushSequence;
    private int _boots;

    private readonly string _fleetManagerId = Guid.NewGuid().ToString();
    private readonly string _otherSessionId = Guid.NewGuid().ToString();
    private readonly string _workerId = Guid.NewGuid().ToString();
    private readonly List<SessionDto> _fleet = new();

    public FleetManagerEventsHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // No reconcile timer: a test decides when the reconcile runs, so no timer can stand in for a hook.
        _priorSweep = FleetManagerEventSweep.Enabled;
        FleetManagerEventSweep.Enabled = false;

        await BootAsync();

        var now = DateTime.UtcNow;
        _fleet.Add(new SessionDto { SessionId = _fleetManagerId, Name = "Fleet Manager", ActivityState = "Working",
                                    CreatedAt = now.AddHours(-1), LastActivityAt = now });
        _fleet.Add(new SessionDto { SessionId = _otherSessionId, Name = "The owner's own session", ActivityState = "WaitingForInput",
                                    CreatedAt = now.AddHours(-1), LastActivityAt = now });
        _fleet.Add(new SessionDto { SessionId = _workerId, Name = "Docs - one change", ActivityState = "Working",
                                    IsControlled = true, ControllerSessionId = _fleetManagerId,
                                    CreatedAt = now.AddMinutes(-5), LastActivityAt = now });
        PushAll();

        var (status, _) = await Send(_owner, "PUT", "gateway/fleet-manager", new { sessionId = _fleetManagerId });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private async Task BootAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        // A restarted Gateway finds the same account in its database; each boot enrolls fresh device ids.
        var boot = ++_boots;
        _subject = $"sub-fm-events-{_runId}";
        var director = HostedTestEnrollment.Enroll(_gateway, _subject, $"fm-events-{_runId}@example.com",
            $"dev-fm-events-dir-{_runId}-{boot}", "MFME");
        _tenant = director.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        var owner = _gateway.Devices.RegisterForTenant(_tenant, _subject, $"dev-fm-events-owner-{_runId}-{boot}", "OWNER",
            deviceType: "browser");
        _owner?.Dispose();
        _owner = Client(owner.DeviceKey);
    }

    public async Task DisposeAsync()
    {
        _owner.Dispose();
        await _gateway.StopAsync();
        FleetManagerEventSweep.Enabled = _priorSweep;
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenant, DirectorId, sessionId,
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private void PushAll(string connection = "conn-1")
    {
        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-" + DirectorId, "someone", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: _tenant);
        _gateway.PushedSessions.RegisterConnection(_tenant, DirectorId, connection);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(_tenant, DirectorId, connection, ++_pushSequence, _fleet.ToList()));
    }

    private void SetState(string sid, string state, bool crashed = false)
    {
        var row = _fleet.Single(s => s.SessionId == sid);
        row.ActivityState = state;
        row.Crashed = crashed;
        row.LastActivityAt = DateTime.UtcNow;
        Assert.True(_gateway.PushedSessions.ApplyDelta(_tenant, DirectorId, "conn-1", ++_pushSequence, row));
    }

    /// <summary>What the Director stream does on an accepted delta: the host's own watcher observes the state.</summary>
    private void Observe(string sid, string state)
        => _gateway.TurnEndWatcherForTest!.Observe(_tenant, sid, state, DirectorId);

    private IReadOnlyList<FleetManagerEventDto> Events() => _gateway.FleetManagerEventStoreForTest!.Unacknowledged(_tenant);

    private async Task<IReadOnlyList<FleetManagerEventDto>> EventsWhenAsync(Func<IReadOnlyList<FleetManagerEventDto>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var events = Events();
            if (done(events)) return events;
            await Task.Delay(50);
        }
        return Events();
    }

    private async Task<(HttpStatusCode Status, string Body)> Send(HttpClient http, string verb, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{verb} {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + text);
        return (resp.StatusCode, text);
    }

    // ---- the hooks ---------------------------------------------------------------------------------------

    /// <summary>THE TURN-END HOOK AND THE READING-COMPLETED HOOK. The worker's turn end is stored by the host's turn-end
    /// fan-out COMPLETE: an owned session is never read (owner ruling, 2026-09-25), so the stop is not left waiting and
    /// says why it has no reading. The reading-completed hook now matters for the Fleet Manager's OWN turn only - its
    /// reading says whether it may be typed into - so it is proven there: the Fleet Manager's reading (the judge
    /// switch is off) reaches the service through the seat's notice, and the Gateway's note names that reason. Without
    /// the notice the note would still say the Wingman is reading.</summary>
    [Fact]
    public async Task TurnEnd_OfAnOwnedSession_IsStoredWithNoReading_AndTheFleetManagersReadingReachesTheService()
    {
        Observe(_workerId, "Working");
        SetState(_workerId, "WaitingForInput");
        Observe(_workerId, "WaitingForInput");

        var events = await EventsWhenAsync(e => e.Count == 1);

        var stop = Assert.Single(events);
        Assert.Equal(("stop", _workerId, _fleetManagerId), (stop.Kind, stop.SessionId, stop.AddressedTo));
        Assert.False(stop.ReadingPending);
        Assert.Null(stop.Verdict);
        Assert.Equal("the Wingman does not read a session another session owns, so this stop has no reading", stop.NoVerdictReason);

        Observe(_fleetManagerId, "Working");
        SetState(_fleetManagerId, "WaitingForInput");
        Observe(_fleetManagerId, "WaitingForInput");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? note = null;
        while (DateTime.UtcNow < deadline)
        {
            await _gateway.FleetManagerEventsForTest!.WhenIdleAsync();
            note = _gateway.FleetManagerEventsForTest.DeliveryNote(_tenant);
            if (note?.Contains("judge switch is off", StringComparison.Ordinal) == true) break;
            await Task.Delay(50);
        }
        Assert.True(note?.Contains("judge switch is off", StringComparison.Ordinal) == true,
            $"the Fleet Manager's own reading never reached the service - the seat's notice is not wired. Note: {note ?? "(none)"}");
    }

    [Fact]
    public async Task TurnEnd_OfASessionNobodyOwns_StoresNothing()
    {
        Observe(_otherSessionId, "Working");
        Observe(_otherSessionId, "WaitingForInput");
        await _gateway.FleetManagerEventsForTest!.WhenIdleAsync();
        await Task.Delay(200);

        Assert.Empty(Events());
    }

    /// <summary>THE MARK ROUTE GOES THROUGH THE PLACEMENT SERVICE (steps 5 and 6 fixes, round 3): marking a session that
    /// was started to take over tells it once, however often it is marked, and the owner's clear records nothing a
    /// replacement could read as the Gateway's own removal.</summary>
    [Fact]
    public async Task MarkRoute_AWaitingSuccessor_IsToldOnce_AndTheOwnersClearIsNotTheGateways()
    {
        var waiting = Guid.NewGuid().ToString();
        _gateway.TenantSettingsResolver.SetFleetManagerSuccessor(_tenant, waiting, _fleetManagerId, DateTime.UtcNow);

        for (var i = 0; i < 2; i++)
        {
            var (status, _) = await Send(_owner, "PUT", "gateway/fleet-manager", new { sessionId = waiting });
            Assert.Equal(HttpStatusCode.OK, status);
        }

        var marked = Assert.Single(Events(), e => e.Kind == FleetManagerEventStore.KindMarked);
        Assert.Equal((waiting, waiting), (marked.SessionId, marked.AddressedTo));
        Assert.Null(_gateway.TenantSettingsResolver.FleetManagerSuccessorSessionId(_tenant));

        _gateway.TenantSettingsResolver.ClearFleetManagerMarkByGateway(_tenant, waiting,
            Settings.TenantSettingsResolver.MarkClearedExited, DateTime.UtcNow);
        var (cleared, body) = await Send(_owner, "PUT", "gateway/fleet-manager", new { sessionId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cleared);
        Assert.Contains("null", body);
        Assert.Null(_gateway.TenantSettingsResolver.FleetManagerSessionId(_tenant));
        Assert.Null(_gateway.TenantSettingsResolver.FleetManagerMarkClearedByGateway(_tenant));
    }

    /// <summary>THE EXIT HOOK: the worker seen working, then exited, is a death.</summary>
    [Fact]
    public async Task Exit_OfAnOwnedSession_IsStoredAsADeath()
    {
        Observe(_workerId, "Working");
        SetState(_workerId, "Exited", crashed: true);
        Observe(_workerId, "Exited");

        var events = await EventsWhenAsync(e => e.Count == 1);

        var died = Assert.Single(events);
        Assert.Equal(("died", _workerId, _fleetManagerId, (bool?)true), (died.Kind, died.SessionId, died.AddressedTo, died.Crashed));
        Assert.Equal("it crashed", died.Detail);
    }

    /// <summary>THE REMOVAL HOOK: the worker removed from its Director's list without an exit is a death.</summary>
    [Fact]
    public async Task Removal_OfAnOwnedSession_IsStoredAsADeath()
    {
        Observe(_workerId, "Working");
        Assert.True(_gateway.PushedSessions.ApplyRemove(_tenant, DirectorId, "conn-1", ++_pushSequence, _workerId));
        _gateway.TurnEndWatcherForTest!.ObserveRemoval(_tenant, _workerId, DirectorId);

        var events = await EventsWhenAsync(e => e.Count == 1);

        var died = Assert.Single(events);
        Assert.Equal(("died", _workerId, "Docs - one change"), (died.Kind, died.SessionId, died.SessionName));
        Assert.Contains("removed it from its session list", died.Detail);
    }

    /// <summary>
    /// THE RECONCILE ACROSS A RESTART: the worker is seen working by one Gateway, which stops. A second Gateway on the
    /// same database has seen nothing - and when the Director reports again without the worker, the reconcile raises
    /// its death.
    /// </summary>
    [Fact]
    public async Task Restart_AnOwnedSessionGoneWhileTheGatewayWasDown_IsStoredAsADeath()
    {
        Observe(_workerId, "Working");
        await _gateway.StopAsync();

        await BootAsync();
        _fleet.RemoveAll(s => s.SessionId == _workerId);
        PushAll("conn-after-restart");
        Assert.Empty(Events());

        await _gateway.ReconcileFleetManagerEventsForTestAsync();

        var died = Assert.Single(await EventsWhenAsync(e => e.Count == 1));
        Assert.Equal(("died", _workerId, _fleetManagerId), (died.Kind, died.SessionId, died.AddressedTo));
        Assert.Contains("no longer in its Director's session list", died.Detail);
    }

    /// <summary>
    /// ABSENCE IS NOT DEATH, THROUGH THE HOST (inspection round 2, finding 2). The worker is seen working; the Gateway
    /// restarts, and its Director - known to the new Gateway but never reconnecting - reports nothing. The reconcile
    /// records no death, run as often as it likes: the service has no timeout after which silence becomes death (the
    /// unit tests advance its clock by days). When the Director says goodbye through the registry the host reads, the
    /// death is recorded.
    /// </summary>
    [Fact]
    public async Task Restart_WithTheDirectorStillDisconnected_NothingDies_UntilTheDirectorSaysGoodbye()
    {
        Observe(_workerId, "Working");
        await _gateway.StopAsync();

        await BootAsync();
        // Known to this Gateway, and not connected: no stream, nothing pushed.
        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-" + DirectorId, "someone", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: _tenant);
        for (var i = 0; i < 3; i++)
            await _gateway.ReconcileFleetManagerEventsForTestAsync();
        Assert.Empty(Events());

        Assert.True(_gateway.Registry.MarkStopped(_tenant, DirectorId));
        await _gateway.ReconcileFleetManagerEventsForTestAsync();

        var died = Assert.Single(await EventsWhenAsync(e => e.Count == 1));
        Assert.Equal(("died", _workerId), (died.Kind, died.SessionId));
        Assert.Contains("its Director shut down", died.Detail);
    }

    // ---- who may acknowledge -----------------------------------------------------------------------------

    /// <summary>Through the real middleware: the guard lets every session key reach the route, and the route lets
    /// only the marked Fleet Manager acknowledge - another session of the account and the owner's own device are
    /// refused with a reason, and nothing is closed.</summary>
    [Fact]
    public async Task Acknowledge_OnlyTheMarkedFleetManager_AndAllClosesOnlyWhatItWasSent()
    {
        Observe(_workerId, "Working");
        SetState(_workerId, "Exited");
        Observe(_workerId, "Exited");
        var died = Assert.Single(await EventsWhenAsync(e => e.Count == 1));

        using var other = Client(SessionKey(_otherSessionId));
        using var fleetManager = Client(SessionKey(_fleetManagerId));

        foreach (var (who, reason) in new[] { (other, "is not it"), (_owner, "the owner does not acknowledge events") })
        {
            var (status, body) = await Send(who, "POST", "gateway/fleet-manager/events/ack", new { ids = new[] { died.Id } });
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Contains("not_fleet_manager", body);
            Assert.Contains(reason, body);
        }
        Assert.Single(Events());

        // Acknowledging all closes nothing it was not sent.
        var (allStatus, allBody) = await Send(fleetManager, "POST", "gateway/fleet-manager/events/ack", new { all = true });
        Assert.Equal(HttpStatusCode.OK, allStatus);
        Assert.Contains("\"acknowledged\":0", allBody);
        Assert.Single(Events());

        var (byId, _) = await Send(fleetManager, "POST", "gateway/fleet-manager/events/ack", new { ids = new[] { died.Id } });
        Assert.Equal(HttpStatusCode.OK, byId);
        Assert.Empty(Events());
    }
}
