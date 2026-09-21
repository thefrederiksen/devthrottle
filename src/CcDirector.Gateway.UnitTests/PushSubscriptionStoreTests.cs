using System.Text.Json;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The push subscription store is the Gateway's set of subscribed devices, keyed by endpoint and persisted
/// (in the EF data layer's push_subscriptions table) so opt-in survives a restart. These tests run over an
/// isolated on-disk SQLite database (a "restart" is a new store over the same file).
/// </summary>
public sealed class PushSubscriptionStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string LegacyPath() => _h.LegacyPath("push-subscriptions-" + Guid.NewGuid().ToString("N") + ".json");
    private PushSubscriptionStore NewStore() => new(_h.Open(), LegacyPath());

    [Fact]
    public void Add_NewEndpoint_ReturnsTrueAndStoresIt()
    {
        var store = NewStore();

        var isNew = store.Add("https://push.example/aaa", "p256-a", "auth-a");

        Assert.True(isNew);
        Assert.Equal(1, store.Count);
        var only = Assert.Single(store.All());
        Assert.Equal("https://push.example/aaa", only.Endpoint);
        Assert.Equal("p256-a", only.P256dh);
        Assert.Equal("auth-a", only.Auth);
    }

    [Fact]
    public void Add_SameEndpointAgain_RefreshesInPlace_ReturnsFalse()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p256-old", "auth-old");

        var isNew = store.Add("https://push.example/aaa", "p256-new", "auth-new");

        Assert.False(isNew);
        Assert.Equal(1, store.Count);
        var only = Assert.Single(store.All());
        Assert.Equal("p256-new", only.P256dh);
        Assert.Equal("auth-new", only.Auth);
    }

    [Fact]
    public void Remove_ExistingEndpoint_ReturnsTrueThenFalse()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");

        Assert.True(store.Remove("https://push.example/aaa"));
        Assert.Equal(0, store.Count);
        Assert.False(store.Remove("https://push.example/aaa"));
    }

    [Fact]
    public void Add_MissingKeys_Throws()
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Add("", "p", "a"));
        Assert.Throws<ArgumentException>(() => store.Add("https://push.example/aaa", "", "a"));
        Assert.Throws<ArgumentException>(() => store.Add("https://push.example/aaa", "p", ""));
    }

    [Fact]
    public void Subscriptions_SurviveAReload()
    {
        var first = NewStore();
        first.Add("https://push.example/aaa", "p256-a", "auth-a");
        first.Add("https://push.example/bbb", "p256-b", "auth-b");

        var reloaded = new PushSubscriptionStore(_h.Open(), LegacyPath());

        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded.All(), s => s.Endpoint == "https://push.example/aaa" && s.P256dh == "p256-a");
        Assert.Contains(reloaded.All(), s => s.Endpoint == "https://push.example/bbb" && s.P256dh == "p256-b");
    }

    [Fact]
    public void LegacyJson_ImportedOnce_Lossless_ThenRenamedAside()
    {
        // A legacy push-subscriptions.json written by the old store: a top-level array of subscriptions,
        // each endpoint + keys + a created stamp.
        var legacy = LegacyPath();
        var created = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        WriteLegacyFile(legacy,
            new StoredPushSubscription { Endpoint = "https://push.example/aaa", P256dh = "p256-a", Auth = "auth-a", CreatedAtUtc = created },
            new StoredPushSubscription { Endpoint = "https://push.example/bbb", P256dh = "p256-b", Auth = "auth-b", CreatedAtUtc = created });

        var store = new PushSubscriptionStore(_h.Open(), legacy);

        Assert.Equal(2, store.Count);
        var a = Assert.Single(store.All(), s => s.Endpoint == "https://push.example/aaa");
        Assert.Equal("p256-a", a.P256dh);
        Assert.Equal("auth-a", a.Auth);
        Assert.Equal(created, a.CreatedAtUtc);

        // The legacy file is renamed aside (kept as a backup), never left to re-import.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));

        // A fresh store over the same DB does NOT re-import (the file is gone) and still has both.
        Assert.Equal(2, new PushSubscriptionStore(_h.Open(), legacy).Count);
    }

    [Fact]
    public void CorruptLegacyJson_FailsLoud_AndLeavesTheFileInPlace()
    {
        var legacy = LegacyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        const string corrupt = "{ this is not json !!!";
        File.WriteAllText(legacy, corrupt);

        Assert.Throws<InvalidOperationException>(() => new PushSubscriptionStore(_h.Open(), legacy));

        Assert.True(File.Exists(legacy));
        Assert.Equal(corrupt, File.ReadAllText(legacy));
    }

    private static void WriteLegacyFile(string path, params StoredPushSubscription[] subs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(subs, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ----- the held count (devthrottle_internal#2199: asked every few seconds per account) -----

    [Fact]
    public void Count_IsHeld_AndEveryWriteIsSeenAtOnce()
    {
        var store = NewStore();
        Assert.Equal(0, store.Count);

        store.Add("https://push.example/aaa", "p", "a");
        Assert.Equal(1, store.Count);
        store.Add("https://push.example/bbb", "p", "a");
        Assert.Equal(2, store.Count);
        store.Remove("https://push.example/aaa");
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Count_AskedAgainWithNoWrite_DoesNotAskTheDatabase()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        Assert.Equal(1, store.Count);

        using var counter = new ReaderCommandCounter(_h.DbPath);
        for (var i = 0; i < 10; i++) Assert.Equal(1, store.Count);

        Assert.Equal(0, counter.Reads);
    }

    /// <summary>A subscription another Gateway process accepted (a deploy overlap) is counted once the held count
    /// is older than its ceiling, and not before.</summary>
    [Fact]
    public void Count_SeesAnotherProcesssSubscription_AfterItsCeiling()
    {
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var db = _h.Open();
        var store = new PushSubscriptionStore(db, LegacyPath(), () => now);
        var other = new PushSubscriptionStore(db, LegacyPath(), () => now);
        Assert.Equal(0, store.Count);

        other.Add("https://push.example/aaa", "p", "a");

        now += PushSubscriptionStore.CountMaxAge - TimeSpan.FromSeconds(1);
        Assert.Equal(0, store.Count);
        now += TimeSpan.FromSeconds(1);
        Assert.Equal(1, store.Count);
    }
}
