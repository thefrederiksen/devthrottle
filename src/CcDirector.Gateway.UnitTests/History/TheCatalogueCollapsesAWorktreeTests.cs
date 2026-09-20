using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// A WORKTREE IS NOT A REPOSITORY: ITS ROW IS COLLAPSED INTO ITS REPOSITORY'S (the one-repository-list
/// mission, "a worktree is not a repository").
///
/// The defect: every agent session on this fleet runs in a git worktree, so every worktree that ever
/// hosted one became its own row. Measured against the live Gateway on 20 September 2026, one Windows
/// machine's list served 559 repositories, of which 110 were live worktrees of four repositories, and
/// thirteen of its top twenty rows were worktrees. Those rows could never leave on their own: the
/// root-folder listing that makes forgetting safe PROTECTS a live worktree's row, and once its folder
/// dies the row is out of the forgetting rule's reach whenever it sits more than one level below a
/// registered root. A rule applied at session start stops the list growing; only this shortens it.
///
/// <b>THE SAFETY PROPERTY these tests hold shut, in the words it was ruled in: the Gateway collapses a
/// row only on a POSITIVE OBSERVATION FROM THE DIRECTOR that the folder is a worktree of a named parent;
/// SILENCE IS NEVER PERMISSION.</b> Most of what is below is one way of saying nothing, and the row that
/// survives it.
/// </summary>
public sealed class TheCatalogueCollapsesAWorktreeTests : IDisposable
{
    private const string Machine = "SOREN_NORTH";
    private const string DirectorOne = "director-one";
    private const string DirectorTwo = "director-two";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _harness.Dispose();

    private KnownRepositoryStore NewStore() => new(_harness.Open());

    private static DiscoveredRepository Found(string path, string name) => new(path, name);

    private static WatchedRootFolder Root(string path, params string[] childPaths) => new(path, childPaths);

    /// <summary>One positive statement by the Director: that folder is a worktree of this repository.</summary>
    private static WorktreeOfRepository WorktreeOf(string repository, string worktree)
        => new(worktree, repository);

    private List<KnownRepositoryEntity> AllRows()
    {
        using var context = _harness.Open().CreateContext(TenantId.Local);
        return context.KnownRepositories.OrderBy(row => row.Path).ToList();
    }

    private string[] Paths() => AllRows().Select(row => row.Path).ToArray();

    private DateTime? LastUsedOf(string path)
        => AllRows().Single(row => row.Path == path).LastUsedUtc;

    // ---------- THE FLOW ----------

    /// <summary>
    /// THE FLOW, and the list the owner was looking at in miniature: a repository and three of its
    /// worktrees, all four with their own row, become one row - and it carries the NEWEST of the four
    /// times, because using a worktree of a repository is using the repository.
    /// </summary>
    [Fact]
    public void TheWorktreeRowsOfOneRepository_BecomeThatOneRepositorysRow()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-9));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now.AddHours(-3));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-smart-restart", "", _now.AddHours(-1));
        store.Observe(TenantId.Local, Machine, "/roots/worktrees/idle-p5-a", "", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            new[] { Root("/roots/work", "/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a",
                "/roots/work/devthrottle-smart-restart") },
            new[]
            {
                WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a"),
                WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-smart-restart"),
                // NOT under any listed root, and it collapses anyway: what decides this is the
                // Director's statement about the folder, never where the folder sits.
                WorktreeOf("/roots/work/devthrottle", "/roots/worktrees/idle-p5-a"),
            },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
        Assert.Equal(_now.AddHours(-1), LastUsedOf("/roots/work/devthrottle"));
    }

    [Fact]
    public void ARepositoryUsedMoreRecentlyThanItsWorktree_KeepsItsOwnTime()
    {
        // The other direction of the same rule: the newer of the two wins, and the repository's own use
        // is not moved backwards by an older worktree.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now);
        store.Observe(TenantId.Local, Machine, "/roots/work/old-worktree", "", _now.AddDays(-30));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/old-worktree") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
        Assert.Equal(_now, LastUsedOf("/roots/work/devthrottle"));
    }

    [Fact]
    public void AWorktreeOfARepositoryNobodyHasOpened_MakesThatRepositoryAUsedOne()
    {
        // The repository has only ever been FOUND; the work all happened in its worktrees. Folding the
        // worktree's time onto it is what makes the list describe where the work actually went.
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null, worktrees: null, _now.AddDays(-1), reconcile: true);
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        var row = Assert.Single(AllRows());
        Assert.Equal("/roots/work/devthrottle", row.Path);
        Assert.Equal(_now, row.LastUsedUtc);
    }

    [Fact]
    public void AWorktreeThatWasOnlyEverFound_IsCollapsedToo_AndTheRepositoryStaysNeverOpened()
    {
        // A worktree added to the registry BY HAND reaches the catalogue as a never-opened row. It is a
        // worktree all the same, and collapsing it must not invent a last-used time for the repository
        // out of a row that never had one.
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle"), Found("/roots/work/by-hand", "by-hand") },
            rootFolders: null, worktrees: null, _now.AddDays(-1), reconcile: true);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/by-hand") },
            _now, reconcile: true);

        var row = Assert.Single(AllRows());
        Assert.Equal("/roots/work/devthrottle", row.Path);
        Assert.Null(row.LastUsedUtc);
    }

    [Fact]
    public void AWorktreeOfARepositoryThisPushIsTheFirstToReport_CollapsesIntoIt()
    {
        // The repository's row is INSERTED by this same push, moments earlier in the same call. The
        // worktree's row must still find it.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        var row = Assert.Single(AllRows());
        Assert.Equal("/roots/work/devthrottle", row.Path);
        Assert.Equal(_now, row.LastUsedUtc);
    }

    [Fact]
    public void TheSameFolderWrittenTwoWays_IsOneRow()
    {
        // The Gateway keys every path its own way, from the PATH'S OWN shape, because it is a Linux
        // container holding paths written by Windows and macOS machines. A statement spelled with the
        // other separator and the other case is the same statement.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, @"D:\ReposFred\devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, @"D:\ReposFred\devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found(@"D:\ReposFred\devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("d:/reposfred/devthrottle", "d:/reposfred/devthrottle-p5-run-a") },
            _now, reconcile: true);

        Assert.Equal(new[] { @"D:\ReposFred\devthrottle" }, Paths());
        Assert.Equal(_now, LastUsedOf(@"D:\ReposFred\devthrottle"));
    }

    // ---------- SILENCE IS NEVER PERMISSION ----------

    [Fact]
    public void APushWithNoWorktreeStatementsAtAll_CollapsesNothing()
    {
        // Every Director in the field before this shipped, and every Director whose repositories have no
        // worktrees. Saying nothing removes nothing.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            new[] { Root("/roots/work", "/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            worktrees: null, _now, reconcile: true);

        Assert.Equal(
            new[] { "/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a" }, Paths());
    }

    [Fact]
    public void AFolderTheDirectorSaidNothingAbout_IsNotCollapsed()
    {
        // THE PROPERTY, stated as a test. Two worktree-looking folders beside each other; the Director
        // names one and says nothing about the other. Only the named one goes.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-5));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-named", "", _now.AddHours(-1));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-unnamed", "", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-named") },
            _now, reconcile: true);

        Assert.Equal(
            new[] { "/roots/work/devthrottle", "/roots/work/devthrottle-unnamed" }, Paths());
        Assert.Equal(_now.AddHours(-1), LastUsedOf("/roots/work/devthrottle"));
    }

    /// <summary>
    /// A FOLDER INSIDE THE REPOSITORY IS NOT A WORKTREE OF IT. Somebody started a session in a
    /// sub-folder once, so the catalogue holds a row for it; it is not named as a worktree, and it
    /// stays. This is the test that stands between this rule and the obvious wrong one nobody wrote -
    /// "collapse anything under the repository's folder" - which would fold a person's own choice of
    /// working directory into a repository they did not pick.
    /// </summary>
    [Fact]
    public void AFolderInsideTheRepository_IsNotCollapsedIntoIt()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-5));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle/docs", "", _now.AddHours(-1));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now.AddHours(-2));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        Assert.Equal(
            new[] { "/roots/work/devthrottle", "/roots/work/devthrottle/docs" }, Paths());
        Assert.Equal(_now.AddHours(-2), LastUsedOf("/roots/work/devthrottle"));
    }

    [Fact]
    public void APushThatIsNotARealObservation_CollapsesNothing()
    {
        // A cold start, and a warm-cache push carrying entries the Director has not re-verified. Neither
        // is a statement about now, so neither may delete a row.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: false);

        Assert.Equal(
            new[] { "/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a" }, Paths());
    }

    [Fact]
    public void AWorktreeOfARepositoryThisPushDoesNotReport_IsNotCollapsed()
    {
        // The named repository is not in this push's snapshot - the root it lives under is not being
        // reported by this Director just now. There is nothing to fold into that the Director can
        // currently see, and inventing a row for it would be the Gateway deciding something it cannot.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/elsewhere/devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/something-else", "something-else") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/elsewhere/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        Assert.Contains("/roots/work/devthrottle-p5-run-a", Paths());
        Assert.Equal(_now, LastUsedOf("/roots/work/devthrottle-p5-run-a"));
    }

    [Fact]
    public void ARepositoryNamedAsAWorktreeOfItself_IsLeftAlone()
    {
        // A statement that would delete the row it folds into. It is refused rather than obeyed.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
        Assert.Equal(_now, LastUsedOf("/roots/work/devthrottle"));
    }

    [Fact]
    public void ABlankStatement_IsIgnoredAndTheRestOfThePushIsNotLost()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[]
            {
                WorktreeOf("/roots/work/devthrottle", "   "),
                WorktreeOf("   ", "/roots/work/devthrottle-p5-run-a"),
                WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a"),
            },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
    }

    [Fact]
    public void AnotherMachinesRowIsNeverCollapsed()
    {
        // The scope is the machine, as it is for everything else here: one machine's Director can only
        // ever speak about its own disk.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, "SORENLAPTOP", "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        Assert.Contains(
            AllRows(), row => row.Path == "/roots/work/devthrottle-p5-run-a" && row.MachineName == "SORENLAPTOP");
    }

    [Fact]
    public void AnotherDirectorOnTheSameMachine_CanCollapseItsOwnWorktrees()
    {
        // A used row has no owner, so the collapse is not scoped by Director - the same reasoning the
        // forgetting rule states. What scopes it is the statement itself: a Director can only name the
        // worktrees of repositories it scanned.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorTwo,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            rootFolders: null,
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
    }

    // ---------- WHERE IT MEETS THE RULE NEXT TO IT ----------

    /// <summary>
    /// THE TWO DESTRUCTIVE RULES MEET, and the order is what decides whether a time survives. A worktree
    /// whose folder has gone is reachable by BOTH: the forgetting rule would delete it and its
    /// last-access time with it, and this rule moves that time onto the repository. The collapse runs
    /// first, so what can be accounted for is accounted for, and only what nobody claims is forgotten.
    ///
    /// This is a WRONG RULE NOTHING REMOVED: nobody deleted the ordering, and this test fails if anyone
    /// ever swaps the two blocks.
    /// </summary>
    [Fact]
    public void AWorktreeThatIsBothGoneAndNamed_IsCollapsedRatherThanForgotten()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-9));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p5-run-a", "", _now);

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            // The listing says the worktree's folder is NOT there any more, which on its own would
            // forget the row and lose its time.
            new[] { Root("/roots/work", "/roots/work/devthrottle") },
            new[] { WorktreeOf("/roots/work/devthrottle", "/roots/work/devthrottle-p5-run-a") },
            _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
        Assert.Equal(_now, LastUsedOf("/roots/work/devthrottle"));
    }

    [Fact]
    public void TheForgettingRuleStillWorksBesideIt()
    {
        // The contrast that makes the test above mean something: a row nobody names, whose folder is
        // gone, under a root the Director just read, is still forgotten.
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "devthrottle", _now.AddHours(-9));
        store.Observe(TenantId.Local, Machine, "/roots/work/deleted", "", _now.AddHours(-1));

        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/work/devthrottle", "devthrottle") },
            new[] { Root("/roots/work", "/roots/work/devthrottle") },
            worktrees: null, _now, reconcile: true);

        Assert.Equal(new[] { "/roots/work/devthrottle" }, Paths());
        Assert.Equal(_now.AddHours(-9), LastUsedOf("/roots/work/devthrottle"));
    }
}
