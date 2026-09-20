using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The DISCOVERED half of the one repository catalog (the one-repository-list mission, phase 2): what a
/// Director's root-folder scan writes, and the four things it must never do to the used half.
/// </summary>
public sealed class DiscoveredRepositoryCatalogTests : IDisposable
{
    private const string Machine = "SOREN_NORTH";
    private const string DirectorOne = "director-one";
    private const string DirectorTwo = "director-two";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _harness.Dispose();

    private KnownRepositoryStore NewStore() => new(_harness.Open());

    /// <summary>Every row the catalog holds for one machine, read straight out of the table. These tests
    /// are about what is WRITTEN - the row's own discovered facts, which no read projects - so they look
    /// at the rows rather than at what <see cref="KnownRepositoryStore.ReadForMachine"/> serves.</summary>
    private List<KnownRepositoryEntity> AllRows()
    {
        using var context = _harness.Open().CreateContext(TenantId.Local);
        return context.KnownRepositories.OrderBy(row => row.Path).ToList();
    }

    private static DiscoveredRepository Found(string path, string name) => new(path, name);

    [Fact]
    public void ObserveDiscovered_RepositoryNeverOpened_IsHeldWithNoLastUsedTime()
    {
        var store = NewStore();

        Assert.True(store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found(@"D:\Repos\never-opened", "never-opened") }, rootFolders: null, _now, reconcile: true));

        var row = Assert.Single(AllRows());
        Assert.Null(row.LastUsedUtc);
        Assert.Equal(DirectorOne, row.DiscoveredByDirectorId);
        Assert.Equal(_now, row.LastSeenUtc);
        Assert.Equal(@"D:\Repos\never-opened", row.Path);
        // The name rides in from the Director, which computed it on the machine that owns the path. The
        // Gateway is a Linux container and never re-derives a leaf from a Windows path.
        Assert.Equal("never-opened", row.Name);
    }

    [Fact]
    public void ObserveDiscovered_RowAlreadyUsed_LeavesTheLastUsedTimeAlone()
    {
        var store = NewStore();
        var used = _now.AddDays(-3);
        store.Observe(TenantId.Local, Machine, "/repos/in-use", "in-use", used);

        Assert.False(store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/repos/in-use", "in-use") }, rootFolders: null, _now, reconcile: true));

        var row = Assert.Single(AllRows());
        Assert.Equal(used, row.LastUsedUtc);
        // Untouchable means untouchable: the discovered observation does not claim the row either.
        Assert.Null(row.DiscoveredByDirectorId);
        Assert.Null(row.LastSeenUtc);
    }

    [Fact]
    public void ObserveDiscovered_RepositoryIsOpenedLater_StaysOneRowAndGainsTheTime()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/repos/opened-tomorrow", "opened-tomorrow") }, rootFolders: null, _now, reconcile: true);

        Assert.True(store.Observe(TenantId.Local, Machine, "/repos/opened-tomorrow", "opened-tomorrow", _now.AddHours(1)));

        var row = Assert.Single(AllRows());
        Assert.Equal(_now.AddHours(1), row.LastUsedUtc);
        // One row, not two: this is why both halves live in one table.
        Assert.Equal(DirectorOne, row.DiscoveredByDirectorId);
        var served = Assert.Single(store.ReadForMachine(TenantId.Local, Machine));
        Assert.Equal("/repos/opened-tomorrow", served.Path);
    }

    [Fact]
    public void ObserveDiscovered_RootFolderRemoved_RemovesOnlyTheNeverOpenedRows()
    {
        var store = NewStore();
        var used = _now.AddDays(-1);
        store.Observe(TenantId.Local, Machine, "/repos/used", "used", used);
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, new[]
        {
            Found("/repos/used", "used"),
            Found("/roots/alpha/one", "one"),
            Found("/roots/alpha/two", "two"),
        }, rootFolders: null, _now, reconcile: true);

        // The alpha root folder is unregistered, so the next scan reports neither repository under it.
        Assert.True(store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/repos/used", "used") }, rootFolders: null, _now.AddMinutes(1), reconcile: true));

        var row = Assert.Single(AllRows());
        Assert.Equal("/repos/used", row.Path);
        Assert.Equal(used, row.LastUsedUtc);
    }

    [Fact]
    public void ObserveDiscovered_ReconcileIsRefused_RemovesNothing()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, rootFolders: null, _now, reconcile: true);

        // The caller could not tell this was a complete observation - a cold start, or a warm-cache push.
        Assert.False(store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            Array.Empty<DiscoveredRepository>(), rootFolders: null, _now.AddMinutes(1), reconcile: false));

        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveDiscovered_TwoDirectorsOnOneMachine_DoNotWipeEachOthersRows()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, rootFolders: null, _now, reconcile: true);
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorTwo,
            new[] { Found("/roots/beta/two", "two") }, rootFolders: null, _now, reconcile: true);

        // The first Director rescans its own - unchanged - root folder. The second Director's finding is
        // not in that snapshot, and must survive it.
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, rootFolders: null, _now.AddHours(2), reconcile: true);

        var rows = AllRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(DirectorOne, rows.Single(row => row.Path == "/roots/alpha/one").DiscoveredByDirectorId);
        Assert.Equal(DirectorTwo, rows.Single(row => row.Path == "/roots/beta/two").DiscoveredByDirectorId);
    }

    [Fact]
    public void ObserveDiscovered_AnotherDirectorReportsTheSamePath_LeavesThatRowAlone()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/shared/one", "one") }, rootFolders: null, _now, reconcile: true);

        Assert.False(store.ObserveDiscovered(TenantId.Local, Machine, DirectorTwo,
            new[] { Found("/roots/shared/one", "one") }, rootFolders: null, _now.AddHours(2), reconcile: true));

        var row = Assert.Single(AllRows());
        Assert.Equal(DirectorOne, row.DiscoveredByDirectorId);
        Assert.Equal(_now, row.LastSeenUtc);
    }

    [Fact]
    public void ObserveDiscovered_WindowsPathSpellingDiffers_StaysOneRow()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found(@"D:\Repos\Project\", "Project") }, rootFolders: null, _now, reconcile: true);

        // The same repository, spelled the other way. Windows-ness is decided from the PATH'S OWN SHAPE -
        // the Gateway is never the machine the path describes.
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("d:/repos/project", "Project") }, rootFolders: null, _now.AddHours(2), reconcile: true);

        var row = Assert.Single(AllRows());
        Assert.Equal("d:/repos/project", row.Path);
        Assert.Null(row.LastUsedUtc);
    }

    [Fact]
    public void ObserveDiscovered_MachineNamesDiffer_WritesUnderTheNameItWasGiven()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, "north", DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, rootFolders: null, _now, reconcile: true);

        // A scan reported under a different machine is a different machine's catalog, and reconciling one
        // from the other would empty it.
        store.ObserveDiscovered(TenantId.Local, "south", DirectorOne,
            new[] { Found("/roots/beta/two", "two") }, rootFolders: null, _now, reconcile: true);

        Assert.Equal(2, AllRows().Count);
    }

    [Fact]
    public void ObserveDiscovered_UnchangedScan_RefreshesTheLastSeenStampOncePastTheInterval()
    {
        var store = NewStore();
        var found = new[] { Found("/roots/alpha/one", "one") };
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, found, rootFolders: null, _now, reconcile: true);

        // Inside the freshness interval the stamp is left where it is - a ten-second reseed must not be a
        // database write per Director for a fact nothing reads to the second.
        Assert.False(store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, found,
            rootFolders: null, _now.Add(KnownRepositoryStore.LastSeenFreshnessInterval).AddMinutes(-1), reconcile: true));
        Assert.Equal(_now, Assert.Single(AllRows()).LastSeenUtc);

        var due = _now.Add(KnownRepositoryStore.LastSeenFreshnessInterval);
        Assert.True(store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, found, rootFolders: null, due, reconcile: true));
        Assert.Equal(due, Assert.Single(AllRows()).LastSeenUtc);
    }

    [Fact]
    public void ObserveDiscovered_NameChanges_CarriesTheDirectorsNameAndNeverBlanksIt()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, rootFolders: null, _now, reconcile: true);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "renamed") }, rootFolders: null, _now, reconcile: true);
        Assert.Equal("renamed", Assert.Single(AllRows()).Name);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "") }, rootFolders: null, _now, reconcile: true);
        Assert.Equal("renamed", Assert.Single(AllRows()).Name);
    }

    [Fact]
    public void ObserveDiscovered_PathlessRow_IsIgnoredAndCannotReconcileAgainstItself()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, rootFolders: null, _now, reconcile: true);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, new[]
        {
            Found("   ", "pathless"),
            Found("/roots/alpha/one", "one"),
        }, rootFolders: null, _now, reconcile: true);

        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ReadForMachine_DiscoveredRepository_IsServedBeneathTheUsedHalf()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/repos/used", "used", _now.AddDays(-1));
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, new[]
        {
            Found("/repos/used", "used"),
            Found("/roots/alpha/never-opened", "never-opened"),
        }, rootFolders: null, _now, reconcile: true);

        // Stored as two rows - phase 2.
        Assert.Equal(2, AllRows().Count);

        // Served as ONE list in ONE order - phase 3. The used repository is first and the never-opened one
        // is beneath it, and the never-opened row says what it is rather than leaving a client to read a
        // missing date.
        var served = store.ReadForMachine(TenantId.Local, Machine);
        Assert.Equal(new[] { "/repos/used", "/roots/alpha/never-opened" },
            served.Select(row => row.Path).ToArray());
        Assert.Equal(_now.AddDays(-1), served[0].LastUsed);
        Assert.False(served[0].NeverOpened);
        Assert.Null(served[1].LastUsed);
        Assert.True(served[1].NeverOpened);
    }

    [Fact]
    public void ObserveDiscovered_BlankDirectorOrMachine_IsRefusedRatherThanWrittenSomewhereWrong()
    {
        var store = NewStore();
        var found = new[] { Found("/roots/alpha/one", "one") };

        Assert.Throws<ArgumentException>(() =>
            store.ObserveDiscovered(TenantId.Local, Machine, "  ", found, rootFolders: null, _now, reconcile: true));
        Assert.Throws<ArgumentException>(() =>
            store.ObserveDiscovered(TenantId.Local, "  ", DirectorOne, found, rootFolders: null, _now, reconcile: true));
        Assert.Throws<ArgumentException>(() =>
            store.ObserveDiscovered(default, Machine, DirectorOne, found, rootFolders: null, _now, reconcile: true));

        Assert.Empty(AllRows());
    }
}
