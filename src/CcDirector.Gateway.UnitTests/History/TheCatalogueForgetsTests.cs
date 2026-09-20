using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// THE CATALOGUE FORGETS A FOLDER THAT NO LONGER EXISTS (the one-repository-list mission, "the catalogue
/// forgets").
///
/// Until this, nothing ever removed a row that had been USED - phase 2's reconciliation is scoped to
/// never-opened rows on purpose - so a folder that was created, worked in and deleted stayed in every
/// screen's list for ever. Measured against the live Gateway on 20 September 2026: one machine's
/// catalogue held 90 repositories of which 76 no longer existed on disk, and phase 6 was about to point
/// the Director's working three-row dialog at that list.
///
/// What makes a removal safe is the Director's ROOT-FOLDER LISTING: for each registered root it could
/// positively read, the direct child folders that exist. Everything below is one of the four conditions
/// that listing has to satisfy before a row goes, or one of the ways it can fail to.
///
/// THE TRAP THIS CLASS EXISTS TO HOLD SHUT is the obvious rule that was NOT built: "forget a used row the
/// Director no longer reports". The root-folder SCAN reports a child only when its <c>.git</c> is a
/// DIRECTORY, so a git WORKTREE has never been in a push at all - eleven of the fourteen surviving
/// repositories on the machine measured above were worktrees, including the one the work was done in.
/// <see cref="AUsedRepositoryTheScanNeverReports_ButTheRootFolderStillHolds_IsKept"/> is that rule's
/// gravestone.
/// </summary>
public sealed class TheCatalogueForgetsTests : IDisposable
{
    private const string Machine = "SOREN_NORTH";
    private const string DirectorOne = "director-one";
    private const string DirectorTwo = "director-two";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _harness.Dispose();

    private KnownRepositoryStore NewStore() => new(_harness.Open());

    private static DiscoveredRepository Found(string path, string name) => new(path, name);

    private static WatchedRootFolder Root(string path, params string[] childPaths)
        => new(path, childPaths);

    private List<KnownRepositoryEntity> AllRows()
    {
        using var context = _harness.Open().CreateContext(TenantId.Local);
        return context.KnownRepositories.OrderBy(row => row.Path).ToList();
    }

    private string[] Paths() => AllRows().Select(row => row.Path).ToArray();

    // ---------- THE FLOW ----------

    /// <summary>
    /// THE FLOW, and the defect this work closes. A repository that was worked in and then deleted sits
    /// under a root the Director just listed, is not in that listing, and is forgotten.
    /// </summary>
    [Fact]
    public void AUsedRepositoryWhoseFolderIsGone_IsForgotten()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/still-here", "still-here", _now.AddHours(-1));
        store.Observe(TenantId.Local, Machine, "/roots/work/deleted-worktree", "deleted-worktree", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/still-here", "still-here") },
            new[] { Root("/roots/work", "/roots/work/still-here") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/still-here" }, Paths());
    }

    /// <summary>
    /// THE TRAP, HELD SHUT. A git worktree's <c>.git</c> is a FILE, and the root-folder scan only accepts
    /// a child whose <c>.git</c> is a DIRECTORY - so a live worktree is never in the pushed snapshot. It
    /// IS in the root folder's listing, because that is a plain directory listing, and that is the only
    /// reason it survives. A rule written against the snapshot alone would delete it.
    /// </summary>
    [Fact]
    public void AUsedRepositoryTheScanNeverReports_ButTheRootFolderStillHolds_IsKept()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/a-live-worktree", "", _now.AddHours(-1));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            // The scan reports the clone and nothing else. The listing reports both folders.
            new[] { Found("/roots/work/the-clone", "the-clone") },
            new[] { Root("/roots/work", "/roots/work/the-clone", "/roots/work/a-live-worktree") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/a-live-worktree", "/roots/work/the-clone" }, Paths());
    }

    /// <summary>
    /// A root the Director listed and found genuinely empty is a positive statement, and everything it
    /// held is forgotten. This is the contrast that makes
    /// <see cref="ARootTheDirectorCouldNotList_ForgetsNothingUnderIt"/> mean something: without it, that
    /// test would pass even if nothing ever forgot anything.
    /// </summary>
    [Fact]
    public void ARootTheDirectorListedAndFoundEmpty_ForgetsWhatItHeld()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/gone", "gone", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/elsewhere/kept", "kept", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/elsewhere/kept", "kept") },
            new[] { Root("/roots/work"), Root("/roots/elsewhere", "/roots/elsewhere/kept") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/elsewhere/kept" }, Paths());
    }

    // ---------- THE FAILURE CASES ----------

    /// <summary>
    /// FAILURE CASE. A root the Director could not read - an unmounted drive, a share that is not there -
    /// is absent from the listing altogether, and nothing under it is touched. This is the same push as
    /// the flow above with one root left out, so what differs is exactly the permission to forget.
    /// </summary>
    [Fact]
    public void ARootTheDirectorCouldNotList_ForgetsNothingUnderIt()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/mnt/unplugged/alpha", "alpha", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/still-here", "still-here", _now.AddHours(-1));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/still-here", "still-here") },
            // /mnt/unplugged is registered but could not be read, so it is not here at all.
            new[] { Root("/roots/work", "/roots/work/still-here") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/mnt/unplugged/alpha", "/roots/work/still-here" }, Paths());
    }

    /// <summary>
    /// FAILURE CASE. An EMPTY push forgets nothing, whatever its listing says. A cold Director before its
    /// first scan pushes nothing, and "I have not looked yet" must never read as "everything has gone".
    /// </summary>
    [Fact]
    public void AnEmptyPush_ForgetsNothing()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/alpha", "alpha", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            Array.Empty<DiscoveredRepository>(),
            new[] { Root("/roots/work") },
            _now, reconcile: false);

        Assert.Equal(new[] { "/roots/work/alpha" }, Paths());
    }

    /// <summary>
    /// FAILURE CASE. A push the caller did not mark as a real observation - a warm-start cache, a partial
    /// view - forgets nothing even when it carries a listing. It is the same guard phase 2 put on the
    /// never-opened half, and it is not weakened here.
    /// </summary>
    [Fact]
    public void APushThatIsNotAFullObservation_ForgetsNothing()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/alpha", "alpha", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/beta", "beta") },
            new[] { Root("/roots/work", "/roots/work/beta") },
            _now, reconcile: false);

        Assert.Contains("/roots/work/alpha", Paths());
    }

    /// <summary>
    /// FAILURE CASE. A Director that predates this change carries no listing, so its pushes forget
    /// nothing - which is exactly what every Director in the field does until it is upgraded. The rows it
    /// pushes still land; only the deletions are withheld.
    /// </summary>
    [Fact]
    public void APushWithNoRootFolderListingAtAll_ForgetsNothing()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/gone", "gone", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/still-here", "still-here") },
            rootFolders: null,
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/gone", "/roots/work/still-here" }, Paths());
    }

    /// <summary>
    /// FAILURE CASE. A repository that sits under no reported root is never looked at - a folder somebody
    /// opened once from anywhere on the disk keeps its place in the list, because no Director has claimed
    /// to speak for where it lives.
    /// </summary>
    [Fact]
    public void AUsedRepositoryOutsideEveryReportedRoot_IsKept()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/somewhere/else/entirely", "entirely", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/alpha", "alpha") },
            new[] { Root("/roots/work", "/roots/work/alpha") },
            _now, reconcile: true);

        Assert.Contains("/somewhere/else/entirely", Paths());
    }

    /// <summary>
    /// FAILURE CASE, and the one a machine running several Directors depends on. A second Director on the
    /// same machine reports its OWN roots, so it can only ever forget what is under those. It cannot wipe
    /// the other Director's rows by staying silent about them.
    /// </summary>
    [Fact]
    public void AnotherDirectorOnTheSameMachine_ForgetsNothingOutsideItsOwnRoots()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/alpha/one", "one", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/beta/two", "two", _now.AddHours(-2));

        // The second Director watches /roots/beta only, and two has gone.
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorTwo,
            new[] { Found("/roots/beta/three", "three") },
            new[] { Root("/roots/beta", "/roots/beta/three") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/alpha/one", "/roots/beta/three" }, Paths());
    }

    /// <summary>
    /// FAILURE CASE - a Director that has gone quiet. Forgetting only ever happens ON a push, so a
    /// Director that stops speaking removes nothing, however long it is away. That is what makes the
    /// catalogue durable in the first place: it is the list the Cockpit and the phone read while a
    /// Director is unreachable.
    /// </summary>
    [Fact]
    public void ADirectorThatHasGoneQuiet_ForgetsNothing()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/alpha", "alpha", _now.AddHours(-2));
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/alpha", "alpha") },
            new[] { Root("/roots/work", "/roots/work/alpha") },
            _now, reconcile: true);

        // No further push, ever, and a Gateway that has restarted since.
        var afterRestart = new KnownRepositoryStore(_harness.Open());

        Assert.Equal("/roots/work/alpha",
            Assert.Single(afterRestart.ReadForMachine(TenantId.Local, Machine)).Path);
    }

    /// <summary>
    /// FAILURE CASE. A repository two levels below a root is not something that root ever spoke for - the
    /// scan and the listing both reach exactly one level - so it is kept. The comparison is the row's
    /// PARENT folder and not a prefix, which is what makes a broad root such as <c>/roots</c> unable to
    /// claim everything beneath a narrower one.
    /// </summary>
    [Fact]
    public void ARepositoryDeeperThanOneLevelUnderARoot_IsKept()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/team/nested", "nested", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/alpha", "alpha") },
            new[] { Root("/roots/work", "/roots/work/alpha") },
            _now, reconcile: true);

        Assert.Contains("/roots/work/team/nested", Paths());
    }

    /// <summary>
    /// PATH COMPARISON, which is this mission's recurring defect. The Director writes a Windows path one
    /// way and the catalogue holds it another; the two are the same folder, and it is not forgotten. The
    /// Gateway is a Linux container holding paths from Windows and macOS machines, so this is decided
    /// from the path's own shape and never from the host.
    /// </summary>
    [Fact]
    public void AFolderWrittenTwoWays_IsRecognisedAsStillThere()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, @"D:\Roots\Work\Alpha", "Alpha", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found(@"D:\Roots\Work\beta", "beta") },
            new[] { Root("d:/roots/work", "d:/roots/work/alpha", @"D:\Roots\Work\beta") },
            _now, reconcile: true);

        Assert.Contains(@"D:\Roots\Work\Alpha", Paths());
    }

    /// <summary>
    /// A repository that IS in the pushed snapshot is still there whatever the listing says, so the two
    /// statements can only ever add up to "kept". The snapshot is the Director speaking about a
    /// repository directly, which is the stronger of the two.
    /// </summary>
    [Fact]
    public void ARepositoryInTheSnapshot_IsKeptEvenIfTheListingMissedIt()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/alpha", "alpha", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/alpha", "alpha") },
            new[] { Root("/roots/work") },
            _now, reconcile: true);

        Assert.Contains("/roots/work/alpha", Paths());
    }

    /// <summary>
    /// The never-opened half keeps its own rule, unchanged by any of this: a never-opened row this
    /// Director reported and no longer reports goes, which is how un-watching a folder has always taken
    /// effect - and it goes whether or not there is a root-folder listing.
    /// </summary>
    [Fact]
    public void TheNeverOpenedHalf_StillReconcilesAsItAlwaysDid()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/alpha", "alpha"), Found("/roots/work/beta", "beta") },
            rootFolders: null, _now, reconcile: true);
        Assert.Equal(2, AllRows().Count);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/alpha", "alpha") },
            rootFolders: null, _now.AddHours(2), reconcile: true);

        Assert.Equal(new[] { "/roots/work/alpha" }, Paths());
    }

    // ---------- THE PARENT OF A PATH, WHICH IS WHAT THE SCOPE IS BUILT ON ----------

    /// <summary>
    /// The folder a path sits in, decided from the path's own shape. Both separators, either case of a
    /// Windows drive, a trailing separator, and the two roots that are not simply "everything before the
    /// last separator" - the POSIX root and a Windows drive root.
    /// </summary>
    [Theory]
    [InlineData("/roots/work/alpha", "/roots/work")]
    [InlineData(@"D:\Roots\Work\Alpha", "D:/ROOTS/WORK")]
    [InlineData("d:/roots/work/alpha/", "D:/ROOTS/WORK")]
    [InlineData("/alpha", "/")]
    [InlineData("C:/alpha", "C:/")]
    [InlineData(@"C:\alpha", "C:/")]
    [InlineData("alpha", "")]
    [InlineData("//server/share/alpha", "//SERVER/SHARE")]
    public void ParentPathKey_IsTheFolderThePathSitsIn(string path, string expected)
        => Assert.Equal(expected, KnownRepositoryStore.ParentPathKey(KnownRepositoryStore.NormalizePathKey(path)));

    /// <summary>
    /// And the one that proves the pair agree rather than merely each being plausible: a repository
    /// registered directly under a drive root is forgotten by a Director watching that drive root, which
    /// only works if <c>ParentPathKey</c> spells a drive root the same way <c>NormalizePathKey</c> does.
    /// </summary>
    [Fact]
    public void ARepositoryDirectlyUnderADriveRoot_IsForgottenByADirectorWatchingThatDrive()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, @"D:\gone", "gone", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found(@"D:\still-here", "still-here") },
            new[] { Root(@"D:\", @"D:\still-here") },
            _now, reconcile: true);

        Assert.Equal(new[] { @"D:\still-here" }, Paths());
    }
}
