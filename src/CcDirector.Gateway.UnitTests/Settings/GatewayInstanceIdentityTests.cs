using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Settings;

/// <summary>
/// This Gateway's stable id (devthrottle_internal#2311, review finding SK-F3). A Director names the library its
/// skills came from by it, so it must be created once and never change: a new id would make every Director treat
/// its own skills as another library's and stop refreshing or withdrawing them.
/// </summary>
public sealed class GatewayInstanceIdentityTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void The_id_is_created_once_and_read_back_unchanged_by_a_new_process()
    {
        var first = new GatewayInstanceIdentity(new TenantSettingsStore(_harness.Open())).Get();

        // A new GatewayInstanceIdentity over the same database is what a restart is.
        var afterRestart = new GatewayInstanceIdentity(new TenantSettingsStore(_harness.Open())).Get();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, afterRestart);
    }

    [Fact]
    public void GetOrAdd_never_replaces_a_value_that_exists()
    {
        var store = new TenantSettingsStore(_harness.Open());
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        var created = store.GetOrAdd(TenantId.System, TenantSettingKeys.GatewayInstanceId, () => "first", now);
        var again = store.GetOrAdd(TenantId.System, TenantSettingKeys.GatewayInstanceId, () => "second", now);

        Assert.Equal("first", created);
        Assert.Equal("first", again);
        Assert.Equal("first", store.Get(TenantId.System, TenantSettingKeys.GatewayInstanceId));
    }

    [Fact]
    public void The_id_is_the_System_tenants_and_no_account_sees_it()
    {
        var store = new TenantSettingsStore(_harness.Open());
        new GatewayInstanceIdentity(store).Get();

        Assert.Null(store.Get(new TenantId("acct-someone"), TenantSettingKeys.GatewayInstanceId));
    }
}
