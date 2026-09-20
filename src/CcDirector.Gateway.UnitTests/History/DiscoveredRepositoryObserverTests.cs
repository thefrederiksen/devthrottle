using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The third observer on the repository snapshot a Director already pushes (the one-repository-list
/// mission, phase 2): which pushes it folds, which it refuses to reconcile from, and where it gets the
/// machine name it writes rows under.
/// </summary>
public sealed class DiscoveredRepositoryObserverTests : IDisposable
{
    private const string DirectorId = "observer-director";
    private const string RegisteredMachine = "SOREN_NORTH";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private readonly KnownRepositoryStore _catalog;

    /// <summary>What the Director REGISTRATION reports, which is what the read side looks rows up by.</summary>
    private string? _registeredMachine = RegisteredMachine;

    public DiscoveredRepositoryObserverTests()
    {
        _catalog = new KnownRepositoryStore(_harness.Open());
    }

    public void Dispose() => _harness.Dispose();

    private DiscoveredRepositoryObserver NewObserver() =>
        new(_catalog, (_, _) => _registeredMachine);

    private List<KnownRepositoryEntity> AllRows()
    {
        using var context = _harness.Open().CreateContext(TenantId.Local);
        return context.KnownRepositories.OrderBy(row => row.Path).ToList();
    }

    private static RepoStatusDto Pushed(string path, string name, bool provisional = false) => new()
    {
        DirectorId = DirectorId,
        // Deliberately NOT the registered machine: nothing may be written under the payload's name.
        MachineName = "a-name-only-the-payload-uses",
        Path = path,
        Name = name,
        Provisional = provisional,
    };

    [Fact]
    public void ObserveSnapshot_RepositoryUnderARootFolder_IsHeldAsFoundButNeverOpened()
    {
        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        var row = Assert.Single(AllRows());
        Assert.Null(row.LastUsedUtc);
        Assert.Equal(DirectorId, row.DiscoveredByDirectorId);
        Assert.Equal("one", row.Name);
    }

    [Fact]
    public void ObserveSnapshot_MachineName_ComesFromTheRegistrationAndNotThePayload()
    {
        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // The read side resolves the owned Director and reads director.MachineName. A row written under
        // the payload's machine name would exist and no screen would ever show it. That the two ENDS agree
        // is proved through the hub and the endpoint themselves, in
        // CcDirector.Gateway.Tests.DiscoveredRepositoryTunnelProofTests.
        var row = Assert.Single(AllRows());
        Assert.Equal(RegisteredMachine, row.MachineName);
        Assert.Equal(RegisteredMachine.ToUpperInvariant(), row.MachineKey);
    }

    [Fact]
    public void ObserveSnapshot_RegistrationReportsNoMachineName_WritesNothing()
    {
        _registeredMachine = "";

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // There is no second-best machine name to fall back on, so nothing is written at all - a row under
        // the wrong machine is worse than no row, because no screen can ever find it.
        Assert.Empty(AllRows());
    }

    [Fact]
    public void ObserveSnapshot_NoBoundDirectorId_WritesNothing()
    {
        NewObserver().ObserveSnapshot(TenantId.Local, "  ",
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        Assert.Empty(AllRows());
    }

    [Fact]
    public void ObserveSnapshot_EmptyPush_RemovesNothing()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // A cold start before the first live scan pushes nothing, and that is not "every repository was
        // removed".
        observer.ObserveSnapshot(TenantId.Local, DirectorId, Array.Empty<RepoStatusDto>(), _now.AddHours(2));

        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveSnapshot_AllProvisionalPush_RemovesNothing()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // A warm-cache push carries only entries the Director has not re-verified yet, and looks exactly
        // like an emptied root folder.
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/two", "two", provisional: true) }, _now.AddHours(2));

        var row = Assert.Single(AllRows());
        Assert.Equal("/roots/alpha/one", row.Path);
    }

    [Fact]
    public void ObserveSnapshot_MixedPush_InsertsWhatItSawAndReconcilesNothing()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/two", "two"),
        }, _now);

        // Some entries verified, others still warming up: a PARTIAL view. Reconciling from it would delete
        // a repository that had simply not finished warming up yet.
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/three", "three", provisional: true),
        }, _now.AddHours(2));

        var paths = AllRows().Select(row => row.Path).ToList();
        Assert.Equal(new[] { "/roots/alpha/one", "/roots/alpha/two" }, paths);
    }

    [Fact]
    public void ObserveSnapshot_RootFolderRemoved_ReconcilesOnACompleteObservation()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/two", "two"),
        }, _now);

        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now.AddHours(2));

        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveSnapshot_IdenticalRePush_CostsNothingUntilTheLastSeenStampIsDue()
    {
        var observer = NewObserver();
        var push = new[] { Pushed("/roots/alpha/one", "one") };
        observer.ObserveSnapshot(TenantId.Local, DirectorId, push, _now);

        // Delete the row behind the observer's back. An identical re-push inside the freshness interval
        // must not bring it back, because it must not reach the database at all - a Director re-pushes its
        // whole snapshot every ten seconds, and on a hosted Gateway that is every Director of every account.
        using (var context = _harness.Open().CreateContext(TenantId.Local))
        {
            context.KnownRepositories.RemoveRange(context.KnownRepositories);
            context.SaveChanges();
        }
        observer.ObserveSnapshot(TenantId.Local, DirectorId, push,
            _now.Add(KnownRepositoryStore.LastSeenFreshnessInterval).AddMinutes(-1));
        Assert.Empty(AllRows());

        // Once the stamp is due, the fold runs in full again.
        observer.ObserveSnapshot(TenantId.Local, DirectorId, push,
            _now.Add(KnownRepositoryStore.LastSeenFreshnessInterval));
        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveSnapshot_ChangedScan_IsFoldedImmediatelyRatherThanWaitingForTheStamp()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // Well inside the freshness interval: the skip is keyed on the snapshot's own content, so anything
        // this fold would write defeats it.
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/two", "two"),
        }, _now.AddSeconds(10));

        Assert.Equal(2, AllRows().Count);
    }

    [Fact]
    public void ObserveSnapshot_RepositoryThatIsUsed_KeepsItsLastUsedTime()
    {
        var used = _now.AddDays(-2);
        _catalog.Observe(TenantId.Local, RegisteredMachine, "/repos/used", "used", used);

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/repos/used", "used"),
            Pushed("/roots/alpha/one", "one"),
        }, _now);

        Assert.Equal(used, AllRows().Single(row => row.Path == "/repos/used").LastUsedUtc);
        Assert.Null(AllRows().Single(row => row.Path == "/roots/alpha/one").LastUsedUtc);
    }

    // ---------- THE ROOT-FOLDER LISTING (the catalogue forgets) ----------

    private static RootFolderListingDto Listing(string root, params string[] children)
        => new() { Path = root, ChildPaths = children.ToList() };

    /// <summary>
    /// THE FLOW, through the observer: the listing rides on the push and the catalogue forgets the folder
    /// that is not in it.
    /// </summary>
    [Fact]
    public void ObserveSnapshot_TheRootFolderListing_LetsTheCatalogueForgetAFolderThatIsGone()
    {
        _catalog.Observe(TenantId.Local, RegisteredMachine, "/roots/alpha/gone", "gone", _now.AddHours(-2));
        var pushed = Pushed("/roots/alpha/one", "one");
        pushed.RootFolders = new List<RootFolderListingDto> { Listing("/roots/alpha", "/roots/alpha/one") };

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId, new[] { pushed }, _now);

        Assert.Equal(new[] { "/roots/alpha/one" }, AllRows().Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// The listing is a push-level fact that rides on one row, so it is taken from the FIRST row that
    /// carries it rather than from row zero. Nothing that re-orders or filters a push can lose it.
    /// </summary>
    [Fact]
    public void ObserveSnapshot_TheListingOnALaterRow_IsStillUsed()
    {
        _catalog.Observe(TenantId.Local, RegisteredMachine, "/roots/alpha/gone", "gone", _now.AddHours(-2));
        var second = Pushed("/roots/alpha/two", "two");
        second.RootFolders = new List<RootFolderListingDto>
        {
            Listing("/roots/alpha", "/roots/alpha/one", "/roots/alpha/two"),
        };

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one"), second }, _now);

        Assert.Equal(new[] { "/roots/alpha/one", "/roots/alpha/two" },
            AllRows().Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// FAILURE CASE. A push with anything provisional in it is a partial view and reconciles nothing, so
    /// a listing riding on it forgets nothing either. A warm-start Director must not be able to empty the
    /// catalogue on its way up.
    /// </summary>
    [Fact]
    public void ObserveSnapshot_AProvisionalPushCarryingAListing_ForgetsNothing()
    {
        _catalog.Observe(TenantId.Local, RegisteredMachine, "/roots/alpha/gone", "gone", _now.AddHours(-2));
        var pushed = Pushed("/roots/alpha/one", "one");
        pushed.RootFolders = new List<RootFolderListingDto> { Listing("/roots/alpha", "/roots/alpha/one") };

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { pushed, Pushed("/roots/alpha/warming-up", "warming-up", provisional: true) }, _now);

        Assert.Contains("/roots/alpha/gone", AllRows().Select(row => row.Path));
    }

    /// <summary>
    /// THE ONE THAT WOULD HAVE MADE THE WHOLE FEATURE SILENTLY DO NOTHING. The observer skips an
    /// identical re-push without touching the database at all, and a folder DELETED under a watched root
    /// changes nothing about the pushed repositories - the scan never reported it. So the skip's
    /// signature has to cover the root-folder listing, or the very push that was meant to forget the
    /// folder would be the one that is skipped.
    ///
    /// The row is deleted behind the observer's back and re-inserted, which is only visible if the fold
    /// actually reached the database.
    /// </summary>
    [Fact]
    public void ObserveSnapshot_AFolderDisappearsUnderAWatchedRoot_DefeatsTheUnchangedRePushSkip()
    {
        _catalog.Observe(TenantId.Local, RegisteredMachine, "/roots/alpha/doomed", "doomed", _now.AddHours(-2));

        var observer = NewObserver();
        var before = Pushed("/roots/alpha/one", "one");
        before.RootFolders = new List<RootFolderListingDto>
        {
            Listing("/roots/alpha", "/roots/alpha/one", "/roots/alpha/doomed"),
        };
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[] { before }, _now);
        Assert.Contains("/roots/alpha/doomed", AllRows().Select(row => row.Path));

        // The repositories pushed are byte-for-byte what they were. Only the listing shrank.
        var after = Pushed("/roots/alpha/one", "one");
        after.RootFolders = new List<RootFolderListingDto> { Listing("/roots/alpha", "/roots/alpha/one") };
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[] { after }, _now.AddMinutes(1));

        Assert.Equal(new[] { "/roots/alpha/one" }, AllRows().Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// And the saving is not lost: a re-push whose listing is the same set in a different order is still
    /// skipped, because a directory listing publishes in whatever order the filesystem gave it.
    /// </summary>
    [Fact]
    public void ObserveSnapshot_TheSameListingInADifferentOrder_IsStillSkipped()
    {
        var observer = NewObserver();
        var first = Pushed("/roots/alpha/one", "one");
        first.RootFolders = new List<RootFolderListingDto>
        {
            Listing("/roots/alpha", "/roots/alpha/one", "/roots/alpha/two"),
        };
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[] { first }, _now);

        // Deleted behind the observer's back: if the fold runs again it comes back.
        using (var context = _harness.Open().CreateContext(TenantId.Local))
        {
            context.KnownRepositories.RemoveRange(context.KnownRepositories.ToList());
            context.SaveChanges();
        }

        var second = Pushed("/roots/alpha/one", "one");
        second.RootFolders = new List<RootFolderListingDto>
        {
            Listing("/roots/alpha", "/roots/alpha/two", "/roots/alpha/one"),
        };
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[] { second }, _now.AddMinutes(1));

        Assert.Empty(AllRows());
    }
}
