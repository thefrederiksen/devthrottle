using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one-repository-list mission, "the catalogue forgets": what the Director says exists under its
/// registered root folders, which is the ONLY thing that lets the Gateway catalogue delete a row.
///
/// The rule being defended here is the one this whole change turns on: <b>"I could not look" must never
/// read as "there is nothing to see"</b>. A root that is reported with no children tells the Gateway to
/// forget every repository it holds under that root, so a root that could not be read is left out of the
/// listing altogether.
/// </summary>
public sealed class DirectorRootFoldersTests
{
    private static Func<string, IReadOnlyList<string>?> Lister(
        Dictionary<string, IReadOnlyList<string>?> answers)
        => root => answers.TryGetValue(root, out var children) ? children : null;

    // ---------- THE FLOW ----------

    /// <summary>
    /// THE FLOW. Each registered root is reported with the direct child folders that exist under it,
    /// which is what the Gateway measures a used row against.
    /// </summary>
    [Fact]
    public void Build_EachRegisteredRoot_ReportsTheDirectChildFoldersThatExist()
    {
        var listings = DirectorRootFolders.Build(
            new[] { "/roots/alpha", "/roots/beta" },
            Lister(new()
            {
                ["/roots/alpha"] = new[] { "/roots/alpha/one", "/roots/alpha/two" },
                ["/roots/beta"] = new[] { "/roots/beta/three" },
            }));

        Assert.Equal(2, listings.Count);
        Assert.Equal(new[] { "/roots/alpha/one", "/roots/alpha/two" },
            Assert.Single(listings, listing => listing.Path == "/roots/alpha").ChildPaths);
        Assert.Equal(new[] { "/roots/beta/three" },
            Assert.Single(listings, listing => listing.Path == "/roots/beta").ChildPaths);
    }

    /// <summary>
    /// THE WHOLE REASON THIS EXISTS. The Director's root-folder SCAN reports a child only when its
    /// <c>.git</c> is a directory, so a git WORKTREE has never been in a push - on the machine this work
    /// was measured against, ELEVEN of the fourteen surviving repositories were worktrees. The listing
    /// is a plain directory listing and knows nothing about git, so a worktree, an ordinary clone and a
    /// folder that is not a repository at all are all equally "there".
    /// </summary>
    [Fact]
    public void Build_TheChildren_AreNotFilteredToGitRepositories()
    {
        var listings = DirectorRootFolders.Build(
            new[] { "/roots/alpha" },
            Lister(new()
            {
                ["/roots/alpha"] = new[]
                {
                    "/roots/alpha/a-clone",
                    "/roots/alpha/a-worktree",
                    "/roots/alpha/not-a-repository-at-all",
                },
            }));

        Assert.Equal(
            new[] { "/roots/alpha/a-clone", "/roots/alpha/a-worktree", "/roots/alpha/not-a-repository-at-all" },
            Assert.Single(listings).ChildPaths);
    }

    // ---------- THE FAILURE CASES ----------

    /// <summary>
    /// FAILURE CASE, AND THE ONE THAT MATTERS MOST. A root the Director could not read - an unmounted
    /// drive, a disconnected share, a folder that has been deleted - is left out entirely. Reporting it
    /// with no children would tell the Gateway to forget every repository under it.
    /// </summary>
    [Fact]
    public void Build_ARootThatCouldNotBeListed_IsNotReportedAtAll()
    {
        var listings = DirectorRootFolders.Build(
            new[] { "/mnt/unplugged-drive", "/roots/alpha" },
            Lister(new()
            {
                ["/mnt/unplugged-drive"] = null, // could not look
                ["/roots/alpha"] = new[] { "/roots/alpha/one" },
            }));

        Assert.Equal("/roots/alpha", Assert.Single(listings).Path);
    }

    /// <summary>
    /// And the contrast that makes the test above mean something: a root the Director COULD read, which
    /// is genuinely empty, is reported with no children. "There is nothing there" is a real answer and
    /// the Gateway is entitled to act on it.
    /// </summary>
    [Fact]
    public void Build_ARootThatIsReadableAndEmpty_IsReportedWithNoChildren()
    {
        var listings = DirectorRootFolders.Build(
            new[] { "/roots/alpha" },
            Lister(new() { ["/roots/alpha"] = Array.Empty<string>() }));

        var listing = Assert.Single(listings);
        Assert.Equal("/roots/alpha", listing.Path);
        Assert.Empty(listing.ChildPaths);
    }

    /// <summary>The same folder registered twice is one root, not two identical ones.</summary>
    [Fact]
    public void Build_ARootRegisteredTwice_IsReportedOnce()
    {
        var calls = 0;
        var listings = DirectorRootFolders.Build(
            new[] { "/roots/alpha", "/roots/alpha" },
            _ => { calls++; return new[] { "/roots/alpha/one" }; });

        Assert.Single(listings);
        Assert.Equal(1, calls);
    }

    /// <summary>A blank entry is not a folder and is never asked about.</summary>
    [Fact]
    public void Build_ABlankRoot_IsDroppedWithoutBeingListed()
    {
        var asked = new List<string>();
        var listings = DirectorRootFolders.Build(
            new[] { "  ", "", "/roots/alpha" },
            root => { asked.Add(root); return Array.Empty<string>(); });

        Assert.Equal("/roots/alpha", Assert.Single(listings).Path);
        Assert.Equal(new[] { "/roots/alpha" }, asked);
    }

    /// <summary>No roots at all is an empty listing, which forgets nothing anywhere.</summary>
    [Fact]
    public void Build_NoRootsAtAll_ReportsNothing()
    {
        Assert.Empty(DirectorRootFolders.Build(null, _ => Array.Empty<string>()));
        Assert.Empty(DirectorRootFolders.Build(Array.Empty<string>(), _ => Array.Empty<string>()));
    }

    /// <summary>A blank child path cannot be compared against anything and is dropped.</summary>
    [Fact]
    public void Build_ABlankChildPath_IsDropped()
    {
        var listings = DirectorRootFolders.Build(
            new[] { "/roots/alpha" },
            Lister(new() { ["/roots/alpha"] = new[] { "  ", "/roots/alpha/one" } }));

        Assert.Equal(new[] { "/roots/alpha/one" }, Assert.Single(listings).ChildPaths);
    }

    // ---------- THE REAL LISTER, AGAINST A REAL DISK ----------

    /// <summary>
    /// The injected lister above is only as good as the real one. This drives
    /// <see cref="DirectorRootFolders.ListChildFolders"/> against a real folder holding a real git
    /// worktree - a folder whose <c>.git</c> is a FILE - and proves it reports it, because that is the
    /// exact kind of folder the root-folder scan cannot see.
    /// </summary>
    [Fact]
    public void ListChildFolders_ARealFolder_ReportsEveryChildFolderIncludingAWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"RootFolders_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a-clone", ".git"));
            Directory.CreateDirectory(Path.Combine(root, "a-worktree"));
            File.WriteAllText(Path.Combine(root, "a-worktree", ".git"), "gitdir: /elsewhere");
            Directory.CreateDirectory(Path.Combine(root, "plain-folder"));
            File.WriteAllText(Path.Combine(root, "a-loose-file.txt"), "not a folder");

            var children = DirectorRootFolders.ListChildFolders(root);

            Assert.NotNull(children);
            Assert.Equal(
                new[] { "a-clone", "a-worktree", "plain-folder" },
                children!.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// FAILURE CASE, on the real lister: a folder that is not there answers NULL and not an empty list.
    /// An empty list would be a licence to delete; null is "I could not look".
    /// </summary>
    [Fact]
    public void ListChildFolders_AFolderThatIsNotThere_AnswersNullRatherThanAnEmptyList()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"RootFoldersMissing_{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(missing));

        Assert.Null(DirectorRootFolders.ListChildFolders(missing));
        Assert.Null(DirectorRootFolders.ListChildFolders("   "));
    }

    /// <summary>And a real folder that is genuinely empty answers an empty list, not null.</summary>
    [Fact]
    public void ListChildFolders_ARealEmptyFolder_AnswersAnEmptyListRatherThanNull()
    {
        var root = Path.Combine(Path.GetTempPath(), $"RootFoldersEmpty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var children = DirectorRootFolders.ListChildFolders(root);
            Assert.NotNull(children);
            Assert.Empty(children!);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* best-effort temp cleanup */ }
        }
    }

    // ---------- HOW IT RIDES ON THE PUSH ----------

    /// <summary>
    /// The listing is a push-level fact riding on one row, because the hub method's signature cannot gain
    /// a parameter without breaking every Director in the field. It is on the FIRST row and on no other,
    /// so a machine with hundreds of repositories does not send hundreds of copies of it.
    /// </summary>
    [Fact]
    public void Union_TheListing_RidesOnTheFirstRowAndOnNoOther()
    {
        var listings = new List<RootFolderListingDto>
        {
            new() { Path = "/roots/alpha", ChildPaths = { "/roots/alpha/one" } },
        };

        var pushed = DirectorRepositorySnapshot.Union(
            new[]
            {
                new Core.Git.RepositoryStatus { Path = "/roots/alpha/one", Name = "one", Success = true },
                new Core.Git.RepositoryStatus { Path = "/roots/alpha/two", Name = "two", Success = true },
            },
            registered: null,
            scanHasCompleted: true,
            rootFolders: listings,
            directorId: "director-one",
            machineName: "SOREN_NORTH");

        Assert.Equal(2, pushed.Count);
        Assert.Equal("/roots/alpha", Assert.Single(pushed[0].RootFolders!).Path);
        Assert.Null(pushed[1].RootFolders);
    }

    /// <summary>
    /// A push with no rows carries no listing, and that costs nothing: a push with no rows is the one the
    /// Gateway already refuses to reconcile from, so there is nothing for a listing to authorise.
    /// </summary>
    [Fact]
    public void Union_NoRowToCarryIt_PushesNoListing()
    {
        var pushed = DirectorRepositorySnapshot.Union(
            scanned: null,
            registered: null,
            scanHasCompleted: true,
            rootFolders: new List<RootFolderListingDto> { new() { Path = "/roots/alpha" } },
            directorId: "director-one",
            machineName: "SOREN_NORTH");

        Assert.Empty(pushed);
    }

    /// <summary>
    /// A Director with nothing to say about its root folders says nothing - the rows go up exactly as
    /// they did before this change, and the Gateway forgets nothing from that push.
    /// </summary>
    [Fact]
    public void Union_NoListing_LeavesEveryRowCarryingNone()
    {
        var pushed = DirectorRepositorySnapshot.Union(
            new[] { new Core.Git.RepositoryStatus { Path = "/roots/alpha/one", Name = "one", Success = true } },
            registered: null,
            scanHasCompleted: true,
            rootFolders: null,
            directorId: "director-one",
            machineName: "SOREN_NORTH");

        Assert.Null(Assert.Single(pushed).RootFolders);
    }
}
