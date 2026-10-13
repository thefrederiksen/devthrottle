using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Settings;

/// <summary>
/// The Factory Manager mark remembers the sessions that held it before (Factory Control, step 5, review note 2). A
/// session that lost the mark is refused on resolve; this record is what lets the refusal say why.
/// </summary>
public sealed class FactoryManagerMarkTests : IDisposable
{
    private const string First = "81000000-0000-4000-8000-000000000001";
    private const string Second = "81000000-0000-4000-8000-000000000002";
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantSettingsResolver _settings;

    public FactoryManagerMarkTests() => _settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SetFactoryManagerSessionId_TheMarkMovesToAnotherSession_RemembersTheOneThatLostIt()
    {
        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);

        _settings.SetFactoryManagerSessionId(TenantId.Local, Second, Now);

        Assert.Equal(Second, _settings.FactoryManagerSessionId(TenantId.Local));
        Assert.True(_settings.WasFactoryManager(TenantId.Local, First));
        Assert.False(_settings.WasFactoryManager(TenantId.Local, Second));
    }

    [Fact]
    public void SetFactoryManagerSessionId_TheMarkIsCleared_RemembersTheOneThatLostIt()
    {
        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);

        _settings.SetFactoryManagerSessionId(TenantId.Local, null, Now);

        Assert.Null(_settings.FactoryManagerSessionId(TenantId.Local));
        Assert.True(_settings.WasFactoryManager(TenantId.Local, First));
    }

    [Fact]
    public void SetFactoryManagerSessionId_TheSameSessionAgain_IsNotAnEarlierOne()
    {
        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);

        _settings.SetFactoryManagerSessionId(TenantId.Local, First.ToUpperInvariant(), Now);

        Assert.False(_settings.WasFactoryManager(TenantId.Local, First));
    }

    [Fact]
    public void SetFactoryManagerSessionId_AnEarlierSessionIsMarkedAgain_IsNoLongerAnEarlierOne()
    {
        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);
        _settings.SetFactoryManagerSessionId(TenantId.Local, Second, Now);

        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);

        Assert.False(_settings.WasFactoryManager(TenantId.Local, First));
        Assert.True(_settings.WasFactoryManager(TenantId.Local, Second));
    }

    [Fact]
    public void WasFactoryManager_ASessionThatNeverHeldTheMark_IsFalse()
    {
        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);

        Assert.False(_settings.WasFactoryManager(TenantId.Local, Second));
    }

    [Fact]
    public void WasFactoryManager_AnotherAccountsEarlierFactoryManager_IsFalse()
    {
        _settings.SetFactoryManagerSessionId(TenantId.Local, First, Now);
        _settings.SetFactoryManagerSessionId(TenantId.Local, Second, Now);

        Assert.False(_settings.WasFactoryManager(new TenantId("acct-someone"), First));
    }

    [Fact]
    public void SetFactoryManagerSessionId_MoreMovesThanAreKept_KeepsTheNewestAndForgetsTheOldest()
    {
        var ids = Enumerable.Range(0, TenantSettingsResolver.FactoryManagerEarlierKept + 2)
            .Select(i => $"82000000-0000-4000-8000-{i:D12}").ToList();

        foreach (var id in ids)
            _settings.SetFactoryManagerSessionId(TenantId.Local, id, Now);

        // The last one holds the mark; the one before it is the newest earlier one; the very first fell off the end.
        Assert.True(_settings.WasFactoryManager(TenantId.Local, ids[^2]));
        Assert.True(_settings.WasFactoryManager(TenantId.Local, ids[1]));
        Assert.False(_settings.WasFactoryManager(TenantId.Local, ids[0]));
    }

    [Fact]
    public void TheEarlierSessionsKey_IsAKnownAccountSetting()
        => Assert.Contains("factory_manager_earlier_session_ids", TenantSettingKeys.All);
}
