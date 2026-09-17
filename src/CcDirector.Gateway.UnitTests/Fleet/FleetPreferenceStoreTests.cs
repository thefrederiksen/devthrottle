using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>The owner's standing preferences for the Fleet Manager (the Fleet Manager mission, step 3).</summary>
public sealed class FleetPreferenceStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("acct-pref-a");
    private static readonly TenantId TenantB = new("acct-pref-b");
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private FleetPreferenceStore NewStore() => new(_harness.Open());

    [Fact]
    public void Add_KeepsTheWordsExactly_AndSurvivesAReopen()
    {
        const string words = "  stop asking me about draft posts, just stage them\n";

        var added = NewStore().Add(TenantA, words, "owner", Now);
        var listed = NewStore().List(TenantA);

        var row = Assert.Single(listed);
        Assert.Equal(added.Id, row.Id);
        Assert.Equal(words, row.Text);
        Assert.Equal("owner", row.CreatedBy);
        Assert.Equal(Now, row.CreatedAtUtc);
    }

    [Fact]
    public void List_IsOldestFirst()
    {
        var store = NewStore();
        store.Add(TenantA, "second", "owner", Now.AddMinutes(1));
        store.Add(TenantA, "first", "owner", Now);

        Assert.Equal(new[] { "first", "second" }, store.List(TenantA).Select(p => p.Text));
    }

    [Fact]
    public void Delete_RemovesOnlyInItsOwnAccount()
    {
        var store = NewStore();
        var kept = store.Add(TenantA, "merge docs changes on green", "owner", Now);

        Assert.False(store.Delete(TenantB, Guid.Parse(kept.Id)));
        Assert.Single(store.List(TenantA));
        Assert.Empty(store.List(TenantB));

        Assert.True(store.Delete(TenantA, Guid.Parse(kept.Id)));
        Assert.Empty(store.List(TenantA));
        Assert.False(store.Delete(TenantA, Guid.Parse(kept.Id)));
    }

    [Theory]
    [InlineData("", "text is required")]
    [InlineData("   ", "text is required")]
    public void Add_Blank_IsRefused(string text, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => NewStore().Add(TenantA, text, "owner", Now));
        Assert.StartsWith(expected, ex.Message);
    }
}
