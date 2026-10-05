using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>The mark, its history, the replacement's removal and the one event move in one transaction.</summary>
public sealed class FleetManagerPromotionStoreTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-fm-promote");
    private static readonly DateTime Now = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
    private const string OldId = "60000000-0000-4000-8000-000000000001";
    private const string NewId = "60000000-0000-4000-8000-000000000002";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantSettingsResolver _settings;
    private readonly FleetManagerPromotionStore _store;
    private readonly FleetManagerEventStore _events;
    private readonly FleetManagerMarkHistory _marks;

    public FleetManagerPromotionStoreTests()
    {
        _settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        _store = new FleetManagerPromotionStore(_harness.Open());
        _events = new FleetManagerEventStore(_harness.Open());
        _marks = new FleetManagerMarkHistory(_harness.Open());
        _settings.SetFleetManagerSessionId(Tenant, OldId, Now);
        _settings.SetFleetManagerSuccessor(Tenant, NewId, OldId, Now);
        _settings.ClearFleetManagerMarkByGateway(Tenant, OldId, TenantSettingsResolver.MarkClearedClosed, Now);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Promote_WritesTheMarkTheHistoryAndOneEvent_AndForgetsTheReplacement()
    {
        Assert.True(_store.Promote(Tenant, NewId.ToUpperInvariant(), Now));

        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorReplaces(Tenant));
        Assert.Null(_settings.FleetManagerMarkClearedByGateway(Tenant));
        Assert.Empty(_settings.FleetManagerWaitingSuccessors(Tenant));
        Assert.Equal(NewId, Assert.Single(_marks.List(Tenant)).SessionId);
        var e = Assert.Single(_events.Unacknowledged(Tenant));
        Assert.Equal((FleetManagerEventStore.KindMarked, NewId, NewId), (e.Kind, e.SessionId, e.AddressedTo));
    }

    [Fact]
    public void Promote_Twice_StoresOneEvent()
    {
        Assert.True(_store.Promote(Tenant, NewId, Now));
        Assert.False(_store.Promote(Tenant, NewId, Now.AddMinutes(1)));

        Assert.Single(_events.Unacknowledged(Tenant));
    }

    [Fact]
    public void Promote_EventWriteFailsAfterTheMarkIsSaved_WritesNothingAtAll()
    {
        _store.BeforeEventSaveForTest = () => throw new IOException("the event write failed");

        Assert.Throws<IOException>(() => _store.Promote(Tenant, NewId, Now));

        Assert.Null(_settings.FleetManagerSessionId(Tenant));
        Assert.Equal(NewId, _settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Equal(OldId, _settings.FleetManagerSuccessorReplaces(Tenant));
        Assert.Equal((OldId, TenantSettingsResolver.MarkClearedClosed), _settings.FleetManagerMarkClearedByGateway(Tenant));
        Assert.Equal(new[] { NewId }, _settings.FleetManagerWaitingSuccessors(Tenant));
        Assert.Empty(_marks.List(Tenant));
        Assert.Empty(_events.Unacknowledged(Tenant));
    }

    [Fact]
    public void SetFleetManagerSuccessor_StoresBothIdsTogether_AndClearForgetsTheReplacementButNotTheWaitingSession()
    {
        _settings.SetFleetManagerSuccessor(Tenant, NewId.ToUpperInvariant(), OldId.ToUpperInvariant(), Now);
        Assert.Equal((NewId, OldId), (_settings.FleetManagerSuccessorSessionId(Tenant), _settings.FleetManagerSuccessorReplaces(Tenant)));
        Assert.Null(_settings.FleetManagerMarkClearedByGateway(Tenant));

        _settings.SetFleetManagerSessionId(Tenant, OldId, Now);
        _settings.ClearFleetManagerSuccessor(Tenant, Now);
        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorReplaces(Tenant));
        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
        // Forgetting the replacement does not tell the waiting session anything: it stays remembered as waiting.
        Assert.Equal(new[] { NewId }, _settings.FleetManagerWaitingSuccessors(Tenant));
    }

    [Fact]
    public void SetFleetManagerSuccessor_NotASessionId_IsRefusedAndNothingIsStored()
    {
        _settings.ClearFleetManagerSuccessor(Tenant, Now);

        Assert.Throws<ArgumentException>(() => _settings.SetFleetManagerSuccessor(Tenant, NewId, "not-an-id", Now));

        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
    }

    // ---- round 3: the Gateway's own removal of the mark, and the owner's mark ------------------------------

    [Fact]
    public void GatewayRemoval_IsRecordedWithTheMark_AndAnyOwnerChangeForgetsIt()
    {
        Assert.Equal((OldId, TenantSettingsResolver.MarkClearedClosed), _settings.FleetManagerMarkClearedByGateway(Tenant));

        Assert.False(_settings.ClearFleetManagerSessionId(Tenant, Now));
        Assert.Null(_settings.FleetManagerMarkClearedByGateway(Tenant));

        _settings.ClearFleetManagerMarkByGateway(Tenant, OldId, TenantSettingsResolver.MarkClearedExited, Now);
        _settings.SetFleetManagerSessionId(Tenant, OldId, Now);
        Assert.Null(_settings.FleetManagerMarkClearedByGateway(Tenant));
    }

    [Fact]
    public void ClearFleetManagerMarkByGateway_UnknownReason_IsRefusedAndNothingIsWritten()
    {
        _settings.SetFleetManagerSessionId(Tenant, OldId, Now);

        Assert.Throws<ArgumentException>(() => _settings.ClearFleetManagerMarkByGateway(Tenant, OldId, "owner", Now));

        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
    }

    [Fact]
    public void MarkByOwner_TheWaitingSuccessor_IsToldOnce_AndTheReplacementIsForgotten()
    {
        Assert.True(_store.MarkByOwner(Tenant, NewId, Now));
        Assert.False(_store.MarkByOwner(Tenant, NewId, Now.AddMinutes(1)));
        Assert.False(_store.Promote(Tenant, NewId, Now.AddMinutes(2)));

        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Null(_settings.FleetManagerMarkClearedByGateway(Tenant));
        Assert.Empty(_settings.FleetManagerWaitingSuccessors(Tenant));
        Assert.Equal(NewId, Assert.Single(_marks.List(Tenant)).SessionId);
        Assert.Equal(NewId, Assert.Single(_events.Unacknowledged(Tenant)).SessionId);
    }

    [Fact]
    public void MarkByOwner_ASessionThatNeverWaited_IsMarkedAndToldOnce_AndTheReplacementStaysRecorded()
    {
        const string Other = "60000000-0000-4000-8000-000000000003";

        // Issue #3559: a session the owner marks by hand is told too, because that event carries the lessons.
        Assert.True(_store.MarkByOwner(Tenant, Other.ToUpperInvariant(), Now));
        Assert.False(_store.MarkByOwner(Tenant, Other, Now.AddMinutes(1)));

        Assert.Equal(Other, _settings.FleetManagerSessionId(Tenant));
        Assert.Equal(NewId, _settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Null(_settings.FleetManagerMarkClearedByGateway(Tenant));
        Assert.Equal(new[] { NewId }, _settings.FleetManagerWaitingSuccessors(Tenant));
        Assert.Equal(Other, Assert.Single(_marks.List(Tenant)).SessionId);
        var told = Assert.Single(_events.Unacknowledged(Tenant));
        Assert.Equal(FleetManagerEventStore.KindMarked, told.Kind);
        Assert.Equal(Other, told.SessionId);
        Assert.Equal(Other, told.AddressedTo);
    }

    [Fact]
    public void MarkByOwner_ASessionMarkedAgainAfterItAcknowledged_IsToldAgain_AndTheMarkedOneIsToldNothing()
    {
        // Review of part 1, finding 3: the event carries the lessons kept since, so a session marked again later must be
        // told again. Marking the session already marked moves nothing and tells it nothing.
        const string A = "60000000-0000-4000-8000-00000000000a";
        const string B = "60000000-0000-4000-8000-00000000000b";

        Assert.True(_store.MarkByOwner(Tenant, A, Now));
        Assert.False(_store.MarkByOwner(Tenant, A, Now.AddMinutes(1)));
        var told = Assert.Single(_events.Unacknowledged(Tenant));
        _events.Acknowledge(Tenant, new[] { Guid.Parse(told.Id) }, all: false, A, Now.AddMinutes(2));
        Assert.True(_store.MarkByOwner(Tenant, B, Now.AddMinutes(3)));
        Assert.True(_store.MarkByOwner(Tenant, A, Now.AddMinutes(4)));

        var open = _events.Unacknowledged(Tenant).Where(e => e.Kind == FleetManagerEventStore.KindMarked).ToList();
        Assert.Equal(new[] { B, A }, open.Select(e => e.SessionId).ToArray());
    }

    [Fact]
    public void WaitingSuccessors_AreBounded_ToTheMostRecent()
    {
        var ids = Enumerable.Range(1, FleetManagerPlacementService.MaxWaitingSuccessors + 5)
            .Select(i => $"70000000-0000-4000-8000-{i:D12}").ToList();

        _settings.AddFleetManagerWaitingSuccessors(Tenant, ids, Now);

        Assert.Equal(new[] { NewId }.Concat(ids).TakeLast(FleetManagerPlacementService.MaxWaitingSuccessors),
            _settings.FleetManagerWaitingSuccessors(Tenant));
    }
}
