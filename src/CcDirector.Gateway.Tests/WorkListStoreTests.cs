using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkListStore"/> (issue #273): create/append/round-trip,
/// mixed-source ordering, reorder, remove-by-source+id, and the single-consumer claim/refusal.
/// These cover the store layer directly; <see cref="WorkListEndpointsTests"/> covers the wire.
/// Persistence behavior (issue #301) is covered by <see cref="WorkListStorePersistenceTests"/>;
/// here each store gets its own isolated temp file (the path is required by design so a test can
/// never land on the real user's worklists.json).
/// </summary>
public sealed class WorkListStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-worklist-store-tests-" + Guid.NewGuid().ToString("N"));

    private WorkListStore NewStore() =>
        new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static WorkListItemRef Ref(string source, string id, string? area = null) =>
        new() { Source = source, Id = id, Area = area };

    [Fact]
    public void Create_ThenGet_ReturnsEmptyList()
    {
        var store = NewStore();

        Assert.True(store.Create("backlog"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal("backlog", list.Name);
        Assert.Empty(list.Items);
        Assert.Null(list.Consumer);
    }

    [Fact]
    public void Create_DuplicateName_ReturnsFalse()
    {
        var store = NewStore();
        store.Create("backlog");

        Assert.False(store.Create("backlog"));
    }

    [Fact]
    public void AppendItem_ThreeItems_PreservesAppendOrder()
    {
        var store = NewStore();
        store.Create("backlog");

        store.AppendItem("backlog", Ref("github", "262", "Gateway"));
        store.AppendItem("backlog", Ref("github", "263"));
        store.AppendItem("backlog", Ref("github", "264"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "262", "263", "264" }, list.Items.Select(i => i.Id).ToArray());
        Assert.Equal("Gateway", list.Items[0].Area);
    }

    [Fact]
    public void AppendItem_MixedSources_AllStoredInOrderWithSourcePreserved()
    {
        var store = NewStore();
        store.Create("backlog");

        store.AppendItem("backlog", Ref("github", "262"));
        store.AppendItem("backlog", Ref("devops", "1203"));
        store.AppendItem("backlog", Ref("jira", "CCD-44"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "github", "devops", "jira" }, list.Items.Select(i => i.Source).ToArray());
        Assert.Equal(new[] { "262", "1203", "CCD-44" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void AppendItem_NoSuchList_ReturnsFalse()
    {
        var store = NewStore();

        Assert.False(store.AppendItem("ghost", Ref("github", "1")));
    }

    [Fact]
    public void Reorder_ReversedArray_ReflectsNewOrder()
    {
        var store = NewStore();
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "1"));
        store.AppendItem("backlog", Ref("github", "2"));
        store.AppendItem("backlog", Ref("github", "3"));

        var reordered = new List<WorkListItemRef> { Ref("github", "3"), Ref("github", "1"), Ref("github", "2") };
        Assert.True(store.Reorder("backlog", reordered));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "3", "1", "2" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void RemoveItem_BySourceAndId_RemovesOnlyThatItem_KeepsOrder()
    {
        var store = NewStore();
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "1"));
        store.AppendItem("backlog", Ref("devops", "2"));
        store.AppendItem("backlog", Ref("github", "3"));

        Assert.True(store.RemoveItem("backlog", "devops", "2"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "1", "3" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Claim_FirstSucceeds_SecondRefused_ReleaseThenReclaimSucceeds()
    {
        var store = NewStore();
        store.Create("backlog");

        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("backlog", "consumer-a"));
        Assert.Equal(WorkListStore.ClaimResult.AlreadyClaimed, store.Claim("backlog", "consumer-b"));

        Assert.True(store.Release("backlog"));
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("backlog", "consumer-b"));
    }

    [Fact]
    public void Claim_NoSuchList_ReturnsNoSuchList()
    {
        var store = NewStore();

        Assert.Equal(WorkListStore.ClaimResult.NoSuchList, store.Claim("ghost", "consumer-a"));
    }

    [Fact]
    public void StoredList_HasNoStatusField()
    {
        // The DTO type itself must carry only name/items/consumer - no status/flow property.
        var props = typeof(WorkListDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Name", "Items", "Consumer" }.OrderBy(n => n), props.OrderBy(n => n));
        Assert.DoesNotContain("Status", props);
        Assert.DoesNotContain("Flow", props);
    }
}
