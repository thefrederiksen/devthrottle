using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hostile two-tenant isolation for the per-tenant settings store (issue #2017). The store takes an EXPLICIT
/// <see cref="TenantId"/> on every call and never infers one, so these tests drive two distinct tenants over a
/// single database and prove one can neither read nor overwrite the other's value. This is the security proof
/// the hosted deny (issue #1863) demanded before these settings could be served on a shared Gateway.
/// </summary>
public sealed class TenantSettingsStoreTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SetThenGet_SameTenant_RoundTrips()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        store.Set(TenantA, TenantSettingKeys.TtsVoice, "shimmer", Now);

        Assert.Equal("shimmer", store.Get(TenantA, TenantSettingKeys.TtsVoice));
    }

    [Fact]
    public void Get_UnsetKey_ReturnsNull()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        Assert.Null(store.Get(TenantA, TenantSettingKeys.WingmanModel));
    }

    [Fact]
    public void Get_OtherTenantsValue_IsNotVisible()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        store.Set(TenantA, TenantSettingKeys.WingmanModel, "a-only-model", Now);

        // Tenant B has set nothing; it must NOT see tenant A's override.
        Assert.Null(store.Get(TenantB, TenantSettingKeys.WingmanModel));
    }

    [Fact]
    public void Set_OneTenant_DoesNotOverwriteAnother()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        store.Set(TenantA, TenantSettingKeys.WingmanFastModel, "a-model", Now);
        store.Set(TenantB, TenantSettingKeys.WingmanFastModel, "b-model", Now);

        Assert.Equal("a-model", store.Get(TenantA, TenantSettingKeys.WingmanFastModel));
        Assert.Equal("b-model", store.Get(TenantB, TenantSettingKeys.WingmanFastModel));
    }

    [Fact]
    public void Set_ExistingKey_UpsertsInPlace()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        store.Set(TenantA, TenantSettingKeys.TimeZone, "America/New_York", Now);
        store.Set(TenantA, TenantSettingKeys.TimeZone, "Europe/London", Now.AddMinutes(1));

        Assert.Equal("Europe/London", store.Get(TenantA, TenantSettingKeys.TimeZone));
    }

    [Fact]
    public void GetAll_ReturnsOnlyThisTenantsOverrides()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        store.Set(TenantA, TenantSettingKeys.TtsVoice, "shimmer", Now);
        store.Set(TenantA, TenantSettingKeys.WingmanFastModel, "a-model", Now);
        store.Set(TenantB, TenantSettingKeys.TtsVoice, "echo", Now);

        var a = store.GetAll(TenantA);
        Assert.Equal(2, a.Count);
        Assert.Equal("shimmer", a[TenantSettingKeys.TtsVoice]);
        Assert.Equal("a-model", a[TenantSettingKeys.WingmanFastModel]);

        var b = store.GetAll(TenantB);
        Assert.Single(b);
        Assert.Equal("echo", b[TenantSettingKeys.TtsVoice]);
    }

    [Fact]
    public void Remove_ClearsOnlyThatTenant()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        store.Set(TenantA, TenantSettingKeys.TtsVoice, "shimmer", Now);
        store.Set(TenantB, TenantSettingKeys.TtsVoice, "echo", Now);

        Assert.True(store.Remove(TenantA, TenantSettingKeys.TtsVoice));

        Assert.Null(store.Get(TenantA, TenantSettingKeys.TtsVoice));
        Assert.Equal("echo", store.Get(TenantB, TenantSettingKeys.TtsVoice));
    }

    [Fact]
    public void Remove_UnsetKey_ReturnsFalse()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        Assert.False(store.Remove(TenantA, TenantSettingKeys.TtsVoice));
    }

    [Fact]
    public void Values_SurviveRestart()
    {
        using var h = new GatewayDbTestHarness();
        new TenantSettingsStore(h.Open()).Set(TenantA, TenantSettingKeys.TimeZone, "Asia/Tokyo", Now);

        // Reopen the same file - a simulated Gateway restart.
        var reopened = new TenantSettingsStore(h.Open());
        Assert.Equal("Asia/Tokyo", reopened.Get(TenantA, TenantSettingKeys.TimeZone));
    }

    [Fact]
    public void Set_UnknownKey_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        Assert.Throws<ArgumentException>(() => store.Set(TenantA, "not_a_real_key", "x", Now));
    }

    [Fact]
    public void Get_InvalidTenant_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());

        // A default(TenantId) is not valid - it must never reach a query as an ambient fallback.
        Assert.Throws<ArgumentException>(() => store.Get(default, TenantSettingKeys.TtsVoice));
    }
}
