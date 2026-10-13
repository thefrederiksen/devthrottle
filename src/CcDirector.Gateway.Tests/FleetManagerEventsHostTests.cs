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
/// The Fleet Manager's events (the Fleet Manager mission, step 4) on a REAL booted hosted Gateway: the routes,
/// called through the real middleware with real credentials, with the events read back from the host's own store.
/// The tests that drove the host's turn-end watcher and waited for its fan-out were removed with the rest of the
/// tests that wait on real time, so the wiring of those hooks is not proven here any more.
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
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
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

        // Issue #3559: the owner's mark now always stores a `marked` event, so the session marked by hand is given the
        // lessons. That is the mark this setup makes, not what any test here is about - prove it is there, then close
        // it, so every test starts from no events.
        var marked = Assert.Single(Events());
        Assert.Equal((FleetManagerEventStore.KindMarked, _fleetManagerId), (marked.Kind, marked.AddressedTo));
        var closed = _gateway.FleetManagerEventStoreForTest!.Acknowledge(_tenant, new[] { Guid.Parse(marked.Id) }, all: false,
            deliveredTo: null, DateTime.UtcNow);
        Assert.Equal(FleetManagerEventAckStatus.Applied, closed.Status);
        Assert.Empty(Events());
    }

    private async Task BootAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
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

    private void PushAll(string connection = "conn-1")
    {
        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-" + DirectorId, "someone", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: _tenant);
        _gateway.PushedSessions.RegisterConnection(_tenant, DirectorId, connection);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(_tenant, DirectorId, connection, ++_pushSequence, _fleet.ToList()));
    }

    private IReadOnlyList<FleetManagerEventDto> Events() => _gateway.FleetManagerEventStoreForTest!.Unacknowledged(_tenant);

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

    // ---- the mark route --------------------------------------------------------------------------------

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
}
