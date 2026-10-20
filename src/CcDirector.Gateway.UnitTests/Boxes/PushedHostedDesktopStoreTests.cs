using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests.Boxes;

/// <summary>
/// Which connected Director hosts which desktop (the Factory Boxes mission, phase 1): a push replaces the Director's
/// list, a disconnect drops it, only the current connection may push, a box id is unique within an account, and one
/// account never sees another's desktops.
/// </summary>
public sealed class PushedHostedDesktopStoreTests
{
    private static readonly TenantId A = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId B = new("22222222-2222-2222-2222-222222222222");

    private static HostedDesktopDto Desktop(string boxId, string name = "A desktop") => new() { BoxId = boxId, DisplayName = name };

    private static PushedHostedDesktopStore WithDirector(TenantId tenant, string directorId = "dir-1", string connection = "conn-1")
    {
        var store = new PushedHostedDesktopStore();
        store.RegisterConnection(tenant, directorId, connection, "devlinux", "devlinux slot 1");
        return store;
    }

    [Fact]
    public void ApplySnapshot_FromTheCurrentConnection_IsListedWithItsDirectorAndMachine()
    {
        var store = WithDirector(A);

        var result = store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo-desktop-soren", "Soren's demo desktop") });

        Assert.True(result.Accepted);
        var row = Assert.Single(store.ListFor(A));
        Assert.Equal(new LocatedHostedDesktop("demo-desktop-soren", "Soren's demo desktop", "dir-1", "devlinux slot 1", "devlinux"), row);
    }

    [Fact]
    public void ApplySnapshot_ASecondPush_ReplacesTheWholeList()
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("one"), Desktop("two") });

        store.ApplySnapshot(A, "dir-1", "conn-1", 2, new[] { Desktop("three") });

        Assert.Equal(new[] { "three" }, store.ListFor(A).Select(d => d.BoxId));
    }

    [Fact]
    public void ApplySnapshot_AnEmptyList_ClearsTheDirectorsDesktops()
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("one") });

        store.ApplySnapshot(A, "dir-1", "conn-1", 2, Array.Empty<HostedDesktopDto>());

        Assert.Empty(store.ListFor(A));
    }

    [Fact]
    public void UnregisterConnection_TheCurrentConnection_DropsItsDesktopsAtOnce()
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("one") });

        store.UnregisterConnection(A, "dir-1", "conn-1");

        Assert.Empty(store.ListFor(A));
        Assert.Null(store.TryLocate(A, "one"));
    }

    [Fact]
    public void UnregisterConnection_ASupersededConnection_LeavesTheNewOnesDesktops()
    {
        var store = WithDirector(A, connection: "conn-old");
        store.RegisterConnection(A, "dir-1", "conn-new", "devlinux", "devlinux slot 1");
        store.ApplySnapshot(A, "dir-1", "conn-new", 1, new[] { Desktop("one") });

        store.UnregisterConnection(A, "dir-1", "conn-old");

        Assert.Single(store.ListFor(A));
    }

    [Fact]
    public void ApplySnapshot_FromASupersededConnection_IsRefused()
    {
        var store = WithDirector(A, connection: "conn-old");
        store.RegisterConnection(A, "dir-1", "conn-new", "devlinux", "devlinux slot 1");

        var result = store.ApplySnapshot(A, "dir-1", "conn-old", 1, new[] { Desktop("one") });

        Assert.False(result.Accepted);
        Assert.Empty(store.ListFor(A));
    }

    [Fact]
    public void ApplySnapshot_WithoutAHello_IsRefused()
    {
        var store = new PushedHostedDesktopStore();

        Assert.False(store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("one") }).Accepted);
    }

    [Fact]
    public void ApplySnapshot_StaleOrRepeatedSequence_IsRefusedAndTheNewerListStays()
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 5, new[] { Desktop("newer") });

        Assert.False(store.ApplySnapshot(A, "dir-1", "conn-1", 5, new[] { Desktop("again") }).Accepted);
        Assert.False(store.ApplySnapshot(A, "dir-1", "conn-1", 3, new[] { Desktop("older") }).Accepted);

        Assert.Equal("newer", Assert.Single(store.ListFor(A)).BoxId);
    }

    [Fact]
    public void ListFor_AnotherAccount_NeverSeesTheseDesktops()
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo") });

        Assert.Empty(store.ListFor(B));
        Assert.Null(store.TryLocate(B, "demo"));
    }

    [Fact]
    public void ApplySnapshot_TheSameBoxIdInTwoAccounts_IsAllowed()
    {
        var store = WithDirector(A);
        store.RegisterConnection(B, "dir-1", "conn-b", "other-machine", "");
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo", "A's") });

        store.ApplySnapshot(B, "dir-1", "conn-b", 1, new[] { Desktop("demo", "B's") });

        Assert.Equal("A's", store.TryLocate(A, "demo")!.DisplayName);
        Assert.Equal("B's", store.TryLocate(B, "demo")!.DisplayName);
    }

    [Fact]
    public void ApplySnapshot_ABoxIdAnotherDirectorInTheAccountHolds_IsRefusedForTheSecondOnly()
    {
        var store = WithDirector(A, "dir-1", "conn-1");
        store.RegisterConnection(A, "dir-2", "conn-2", "other-machine", "");
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo") });

        var result = store.ApplySnapshot(A, "dir-2", "conn-2", 1, new[] { Desktop("demo"), Desktop("other") });

        Assert.True(result.Accepted);
        Assert.Equal(new[] { "demo" }, result.RefusedBoxIds);
        Assert.Equal("dir-1", store.TryLocate(A, "demo")!.DirectorId);
        Assert.Equal("dir-2", store.TryLocate(A, "other")!.DirectorId);
        Assert.Equal(2, store.ListFor(A).Count);
    }

    [Fact]
    public void ApplySnapshot_AfterTheHolderDisconnects_TheSecondDirectorClaimsTheBoxId()
    {
        var store = WithDirector(A, "dir-1", "conn-1");
        store.RegisterConnection(A, "dir-2", "conn-2", "other-machine", "");
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo") });
        store.ApplySnapshot(A, "dir-2", "conn-2", 1, new[] { Desktop("demo") });

        store.UnregisterConnection(A, "dir-1", "conn-1");
        store.ApplySnapshot(A, "dir-2", "conn-2", 2, new[] { Desktop("demo") });

        Assert.Equal("dir-2", store.TryLocate(A, "demo")!.DirectorId);
    }

    [Theory]
    [InlineData("Demo")]
    [InlineData("127.0.0.1")]
    [InlineData("demo:3001")]
    [InlineData("3001")]
    [InlineData("")]
    public void ApplySnapshot_AnInvalidBoxId_IsRefusedAndTheRestKept(string boxId)
    {
        var store = WithDirector(A);

        var result = store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop(boxId), Desktop("good") });

        Assert.True(result.Accepted);
        Assert.Single(result.RefusedBoxIds);
        Assert.Equal("good", Assert.Single(store.ListFor(A)).BoxId);
    }

    [Fact]
    public void ApplySnapshot_NoDisplayNameOrAnEmptyEntry_IsRefused()
    {
        var store = WithDirector(A);

        var result = store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("no-name", "  "), null!, Desktop("good") });

        Assert.Equal(2, result.RefusedBoxIds.Count);
        Assert.Equal("good", Assert.Single(store.ListFor(A)).BoxId);
    }

    [Fact]
    public void ApplySnapshot_TheSameBoxIdTwiceInOnePush_KeepsTheFirst()
    {
        var store = WithDirector(A);

        var result = store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo", "First"), Desktop("demo", "Second") });

        Assert.Single(result.RefusedBoxIds);
        Assert.Equal("First", Assert.Single(store.ListFor(A)).DisplayName);
    }

    [Fact]
    public void RegisterConnection_ARepeatedHello_UpdatesTheDirectorsNameAndKeepsTheList()
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo") });

        store.RegisterConnection(A, "dir-1", "conn-1", "devlinux", "renamed");

        Assert.Equal("renamed", Assert.Single(store.ListFor(A)).DirectorName);
    }

    [Fact]
    public void RegisterConnection_ANewConnection_StartsWithNoDesktopsUntilItPushes()
    {
        var store = WithDirector(A, connection: "conn-old");
        store.ApplySnapshot(A, "dir-1", "conn-old", 1, new[] { Desktop("demo") });

        store.RegisterConnection(A, "dir-1", "conn-new", "devlinux", "devlinux slot 1");

        Assert.Empty(store.ListFor(A));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.1.2.3:3001")]
    public void TryLocate_NotABoxId_FindsNothing(string? boxId)
    {
        var store = WithDirector(A);
        store.ApplySnapshot(A, "dir-1", "conn-1", 1, new[] { Desktop("demo") });

        Assert.Null(store.TryLocate(A, boxId));
    }
}
