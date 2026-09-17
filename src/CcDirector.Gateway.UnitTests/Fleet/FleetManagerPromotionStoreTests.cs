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
        _settings.SetFleetManagerSuccessorClosedOld(Tenant, OldId, Now);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Promote_WritesTheMarkTheHistoryAndOneEvent_AndForgetsTheReplacement()
    {
        Assert.True(_store.Promote(Tenant, NewId.ToUpperInvariant(), Now));

        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorReplaces(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorClosedOld(Tenant));
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

        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
        Assert.Equal(NewId, _settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Equal(OldId, _settings.FleetManagerSuccessorReplaces(Tenant));
        Assert.Equal(OldId, _settings.FleetManagerSuccessorClosedOld(Tenant));
        Assert.Empty(_marks.List(Tenant));
        Assert.Empty(_events.Unacknowledged(Tenant));
    }

    [Fact]
    public void SetFleetManagerSuccessor_StoresBothIdsTogether_AndClearForgetsAllThree()
    {
        _settings.SetFleetManagerSuccessor(Tenant, NewId.ToUpperInvariant(), OldId.ToUpperInvariant(), Now);
        Assert.Equal((NewId, OldId, (string?)null),
            (_settings.FleetManagerSuccessorSessionId(Tenant), _settings.FleetManagerSuccessorReplaces(Tenant),
             _settings.FleetManagerSuccessorClosedOld(Tenant)));

        _settings.ClearFleetManagerSuccessor(Tenant, Now);
        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
        Assert.Null(_settings.FleetManagerSuccessorReplaces(Tenant));
        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
    }

    [Fact]
    public void SetFleetManagerSuccessor_NotASessionId_IsRefusedAndNothingIsStored()
    {
        _settings.ClearFleetManagerSuccessor(Tenant, Now);

        Assert.Throws<ArgumentException>(() => _settings.SetFleetManagerSuccessor(Tenant, NewId, "not-an-id", Now));

        Assert.Null(_settings.FleetManagerSuccessorSessionId(Tenant));
    }
}
