using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The settings store remembers what it read (issue #4405). These prove both halves: a remembered value IS served
/// from memory (a row changed behind every context's back is not seen until the value ages out), and no write made
/// through a context is ever missed - the store's own, another writer's, and one saved inside a transaction.
///
/// Every test uses its own account id. The settings numbers are process-wide, so a shared id would let another test's
/// write make a value here forget early - which is correct behaviour, and would turn the "served from memory" facts
/// into flakes.
/// </summary>
public sealed class TenantSettingsStoreCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private const string Key = TenantSettingKeys.TtsVoice;

    private long _clockMs = 1_000_000;

    private static TenantId NewTenant() => new("cache-" + Guid.NewGuid().ToString("N"));

    /// <summary>Change the row with SQL that bypasses SaveChanges - the one kind of write the cache cannot see, and
    /// so the probe for whether a read came from memory.</summary>
    private static void ChangeBehindTheCache(GatewayDatabase db, TenantId tenant, string value)
    {
        using var ctx = db.CreateContext(tenant);
        var rows = ctx.Database.ExecuteSqlRaw(
            "UPDATE tenant_settings SET \"Value\" = {0} WHERE tenant_id = {1} AND \"Key\" = {2}", value, tenant.Value, Key);
        Assert.Equal(1, rows);
    }

    [Fact]
    public void Get_RemembersTheValue_UntilMaxAge()
    {
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var store = new TenantSettingsStore(db, () => _clockMs);
        var tenant = NewTenant();
        store.Set(tenant, Key, "first", Now);
        Assert.Equal("first", store.Get(tenant, Key));

        ChangeBehindTheCache(db, tenant, "second");

        _clockMs += (long)TenantSettingsStore.MaxAge.TotalMilliseconds - 1;
        Assert.Equal("first", store.Get(tenant, Key));
        _clockMs += 1;
        Assert.Equal("second", store.Get(tenant, Key));
    }

    [Fact]
    public void Get_RemembersThatThereIsNoOverride()
    {
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var store = new TenantSettingsStore(db, () => _clockMs);
        var tenant = NewTenant();
        Assert.Null(store.Get(tenant, Key));

        // Inserted with SQL, so nothing moves the account's number.
        using (var ctx = db.CreateContext(tenant))
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO tenant_settings (tenant_id, \"Key\", \"Value\", \"UpdatedAtUtc\") VALUES ({0}, {1}, {2}, {3})",
                tenant.Value, Key, "added", Now);

        Assert.Null(store.Get(tenant, Key));
        _clockMs += (long)TenantSettingsStore.MaxAge.TotalMilliseconds;
        Assert.Equal("added", store.Get(tenant, Key));
    }

    [Fact]
    public void Get_AfterTheStoresOwnWrites_ReadsTheNewValueAtOnce()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open(), () => _clockMs);
        var tenant = NewTenant();
        Assert.Null(store.Get(tenant, Key));

        store.Set(tenant, Key, "set", Now);
        Assert.Equal("set", store.Get(tenant, Key));

        store.Apply(tenant, new Dictionary<string, string?> { [Key] = "applied" }, Now);
        Assert.Equal("applied", store.Get(tenant, Key));

        Assert.True(store.Remove(tenant, Key));
        Assert.Null(store.Get(tenant, Key));
    }

    [Fact]
    public void Get_AfterAnotherWritersSave_ReadsTheNewValueAtOnce()
    {
        // The Mail Desk settings write the table through their own context, never through the store.
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var store = new TenantSettingsStore(db, () => _clockMs);
        var tenant = NewTenant();
        Assert.Null(store.Get(tenant, Key));

        using (var ctx = db.CreateContext(tenant))
        {
            ctx.TenantSettings.Add(new TenantSettingEntity { TenantId = tenant.Value, Key = Key, Value = "direct", UpdatedAtUtc = Now });
            ctx.SaveChanges();
        }

        Assert.Equal("direct", store.Get(tenant, Key));
    }

    [Fact]
    public void Get_ReadBetweenASaveAndItsCommit_IsNotTrustedAfterTheCommit()
    {
        // The Fleet Manager promotion: ApplyIn, SaveChanges, more rows, then Commit. A read in between sees the old
        // committed row under the account's NEW number; only the commit moving the number again makes it forgotten.
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var store = new TenantSettingsStore(db, () => _clockMs);
        var tenant = NewTenant();
        store.Set(tenant, Key, "old", Now);

        using (var ctx = db.CreateContext(tenant))
        using (var tx = ctx.Database.BeginTransaction())
        {
            TenantSettingsStore.ApplyIn(ctx, tenant, new Dictionary<string, string?> { [Key] = "new" }, Now);
            ctx.SaveChanges();

            Assert.Equal("old", store.Get(tenant, Key));

            tx.Commit();
        }

        Assert.Equal("new", store.Get(tenant, Key));
    }

    [Fact]
    public void Get_AfterARolledBackTransaction_ReadsTheUnchangedValue()
    {
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var store = new TenantSettingsStore(db, () => _clockMs);
        var tenant = NewTenant();
        store.Set(tenant, Key, "kept", Now);

        using (var ctx = db.CreateContext(tenant))
        using (var tx = ctx.Database.BeginTransaction())
        {
            TenantSettingsStore.ApplyIn(ctx, tenant, new Dictionary<string, string?> { [Key] = "discarded" }, Now);
            ctx.SaveChanges();
            tx.Rollback();
        }

        Assert.Equal("kept", store.Get(tenant, Key));
    }

    [Fact]
    public void GetOrAdd_ExistingValue_IsRememberedAndNeverCreatedAgain()
    {
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var store = new TenantSettingsStore(db, () => _clockMs);
        var tenant = NewTenant();
        Assert.Equal("made", store.GetOrAdd(tenant, Key, () => "made", Now));
        Assert.Equal("made", store.GetOrAdd(tenant, Key, () => throw new InvalidOperationException("created twice"), Now));

        ChangeBehindTheCache(db, tenant, "changed");
        Assert.Equal("made", store.GetOrAdd(tenant, Key, () => "unused", Now));
        _clockMs += (long)TenantSettingsStore.MaxAge.TotalMilliseconds;
        Assert.Equal("changed", store.GetOrAdd(tenant, Key, () => "unused", Now));
    }
}
