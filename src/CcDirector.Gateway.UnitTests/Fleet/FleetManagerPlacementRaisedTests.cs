using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// RAISED FOLLOWS THE FLEET MANAGER MARK, through the real <see cref="FleetManagerPlacementService"/> - the caller of
/// <see cref="RaisedSessionStore.FollowMark"/> and <see cref="RaisedSessionStore.CarryToSuccessor"/> - over a real
/// database (the Fleet Manager Improvement mission, phase 1). The store's own rules are pinned in
/// <c>RaisedSessionStoreTests</c>; what is proven here is that the service CALLS them, with the right caller, at the
/// right moments, and writes the record. The mark the store reads is the very setting the service writes.
/// </summary>
public sealed class FleetManagerPlacementRaisedTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-fm-raised");
    private static readonly DateTime Now = new(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
    private const string OldId = "41000000-0000-4000-8000-000000000001";
    private const string NewId = "41000000-0000-4000-8000-000000000002";
    private const string OtherId = "41000000-0000-4000-8000-000000000003";

    private static readonly FleetManagerCaller OwnersBrowser = new("device browser dev-owner", IsOwnerDevice: true);
    private static readonly FleetManagerCaller ASessionKey = new("session " + OtherId, IsOwnerDevice: false);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantSettingsResolver _settings;
    private readonly FakePlacementWorld _world = new(Now, NewId);
    private readonly RaisedSessionStore _raised;
    private readonly GovernanceAuditLog _audit;
    private readonly FleetManagerPlacementService _service;

    public FleetManagerPlacementRaisedTests()
    {
        _settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        _world.Promotions = new FleetManagerPromotionStore(_harness.Open());
        _raised = new RaisedSessionStore(_harness.Open(), tenant => _settings.FleetManagerSessionId(tenant));
        _audit = new GovernanceAuditLog(_harness.Open(new FixedTenantContext(Tenant)));
        var record = new RaisedSessionRecord(_audit, _ => new NoScope());
        _service = new FleetManagerPlacementService(_settings, _world, new FleetManagerDeliveryGate(),
            retirePoll: TimeSpan.Zero, raised: _raised, raisedRecord: record);
    }

    public void Dispose()
    {
        _service.Dispose();
        _harness.Dispose();
    }

    private sealed class NoScope : IDisposable { public void Dispose() { } }

    private static void NoStamp(NewSessionRequest _) { }

    private static FleetManagerMachineFacts Running(string machine) => new(
        machine, new LauncherDto { MachineName = machine, LastSeenAt = Now }, LauncherReach.Connected, true,
        new[] { new DirectorDto { DirectorId = "dir-a", MachineName = machine, LastSeen = Now } }, Now.AddDays(-2), "ClaudeCode");

    private static SessionDto Live(string id, string state) => new()
    {
        SessionId = id, ActivityState = state, MachineName = "WORKSTATION-A", Agent = "ClaudeCode", CreatedAt = Now.AddHours(-1),
    };

    private IReadOnlyList<GovernanceAuditEventDto> Records(string eventType, string sessionId)
        => _audit.List(sessionId, null, GovernanceAuditCategory.Permission, eventType, null, null, 100);

    [Fact]
    public async Task SetMarkByOwnerAsync_FromTheOwnersDevice_RaisesTheMarkedSession_AndRecordsIt()
    {
        _world.Roster.Add(("dir-a", Live(OldId, "Idle")));

        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, OwnersBrowser);

        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
        Assert.True(_raised.IsRaised(Tenant, OldId));
        var record = Assert.Single(Records(GovernanceAuditEventType.SessionRaised, OldId));
        Assert.Equal(OwnersBrowser.Actor, record.Actor);
    }

    [Fact]
    public async Task SetMarkByOwnerAsync_FromASessionKey_MarksTheSession_AndRaisesNobody()
    {
        _world.Roster.Add(("dir-a", Live(OtherId, "Idle")));

        await _service.SetMarkByOwnerAsync(Tenant, OtherId, default, ASessionKey);

        // POSITIVE CONTROL: the mark was set, so the call did its work.
        Assert.Equal(OtherId, _settings.FleetManagerSessionId(Tenant));
        Assert.False(_raised.IsRaised(Tenant, OtherId));
        Assert.Empty(_raised.List(Tenant));
        Assert.Empty(Records(GovernanceAuditEventType.SessionRaised, OtherId));
    }

    [Fact]
    public async Task SetMarkByOwnerAsync_ASessionKeyMarksTheAlreadyMarkedFleetManagerAgain_ItStaysRaised()
    {
        _world.Roster.Add(("dir-a", Live(OldId, "Idle")));
        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, OwnersBrowser);

        // The Fleet Manager running `fleet-manager set` on itself changes nothing, so it takes nothing away.
        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, new FleetManagerCaller("session " + OldId, false));

        Assert.True(_raised.IsRaised(Tenant, OldId));
        Assert.Empty(Records(GovernanceAuditEventType.SessionLowered, OldId));
    }

    [Fact]
    public async Task SetMarkByOwnerAsync_ASessionKeyMovesTheMark_TheOldOneIsLowered_AndRecorded()
    {
        _world.Roster.Add(("dir-a", Live(OldId, "Idle")));
        _world.Roster.Add(("dir-a", Live(OtherId, "Idle")));
        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, OwnersBrowser);

        await _service.SetMarkByOwnerAsync(Tenant, OtherId, default, ASessionKey);

        Assert.False(_raised.IsRaised(Tenant, OldId));
        Assert.False(_raised.IsRaised(Tenant, OtherId));
        var lowered = Assert.Single(Records(GovernanceAuditEventType.SessionLowered, OldId));
        Assert.Equal(ASessionKey.Actor, lowered.Actor);
    }

    [Fact]
    public async Task SetMarkByOwnerAsync_Cleared_LowersTheFleetManager()
    {
        _world.Roster.Add(("dir-a", Live(OldId, "Idle")));
        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, OwnersBrowser);

        await _service.SetMarkByOwnerAsync(Tenant, null, default, OwnersBrowser);

        Assert.Null(_settings.FleetManagerSessionId(Tenant));
        Assert.False(_raised.IsRaised(Tenant, OldId));
        Assert.Empty(_raised.List(Tenant));
        Assert.Single(Records(GovernanceAuditEventType.SessionLowered, OldId));
    }

    [Fact]
    public async Task StartAsync_FromTheOwnersDevice_TheNewFleetManagerIsRaised()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _world.NextSession = Live(NewId, "Starting");

        var result = await _service.StartAsync(Tenant, NoStamp, default, OwnersBrowser);

        Assert.Equal(200, result.Status);
        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.True(_raised.IsRaised(Tenant, NewId));
        Assert.Single(Records(GovernanceAuditEventType.SessionRaised, NewId));
    }

    /// <summary>
    /// A RESTART CARRIES RAISED, AND NEVER MULTIPLIES IT. While the old Fleet Manager finishes its turn it is still the
    /// marked one and still the raised one, and the new one - already started, already holding a key - is NOT. The
    /// moment the mark moves, they swap.
    /// </summary>
    [Fact]
    public async Task RestartAsync_OfARaisedFleetManager_TheNewOneIsRaisedOnlyOnceTheMarkMoves_AndTheOldOneIsNotAfterwards()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        _world.Roster.Add(("dir-a", Live(OldId, "Working")));
        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, OwnersBrowser);
        _world.Roster.Add(("dir-a", Live(NewId, "WaitingForInput")));

        var looks = 0;
        _world.OnDelay = () =>
        {
            looks++;
            Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
            Assert.True(_raised.IsRaised(Tenant, OldId));
            Assert.False(_raised.IsRaised(Tenant, NewId));
            if (looks == 2) _world.SetState(OldId, "Idle");
        };

        var result = await _service.RestartAsync(Tenant, NoStamp, default, OwnersBrowser);
        Assert.Equal(200, result.Status);
        await _service.WhenIdleAsync();

        Assert.True(looks >= 2, "the loop must have looked while the old one still worked, or the asserts inside it never ran");
        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.True(_raised.IsRaised(Tenant, NewId));
        Assert.False(_raised.IsRaised(Tenant, OldId));
        Assert.Single(Records(GovernanceAuditEventType.SessionRaised, NewId));
    }

    [Fact]
    public async Task RestartAsync_OfAFleetManagerThatIsNotRaised_TheNewOneIsNotRaisedEither()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        _world.Roster.Add(("dir-a", Live(OldId, "Idle")));
        await _service.SetMarkByOwnerAsync(Tenant, OldId, default, ASessionKey);
        _world.Roster.Add(("dir-a", Live(NewId, "WaitingForInput")));

        var result = await _service.RestartAsync(Tenant, NoStamp, default, OwnersBrowser);
        Assert.Equal(200, result.Status);
        await _service.WhenIdleAsync();

        // POSITIVE CONTROL: the restart ran to its end - the mark moved.
        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.False(_raised.IsRaised(Tenant, NewId));
        Assert.Empty(Records(GovernanceAuditEventType.SessionRaised, NewId));
    }
}
