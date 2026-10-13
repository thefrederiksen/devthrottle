using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A WORKTREE IS NOT A REPOSITORY (the one-repository-list mission, "a worktree is not a repository").
///
/// The defect these exist to stop coming back: every agent session on this fleet runs in a git
/// worktree, and every worktree that ever hosted one became its own row in the repository list.
/// Measured against the live Gateway on 20 September 2026, one Windows machine's list served 559
/// repositories, 110 of which were live worktrees of four repositories.
///
/// <para><b>The layout is git's, not this test's, wherever it can be.</b> The flow cases below run
/// REAL <c>git worktree add</c> against a real repository on a real disk, because a rule about git's
/// own record of itself proved against a layout this test invented would prove only that the two
/// agree with each other. The shapes git will not make on demand - a submodule, a bare repository's
/// worktree, a <c>.git</c> file left pointing at nothing - are written by hand, and each one says so.
/// </para>
/// </summary>
public sealed class LinkedWorktreeTests : IDisposable
{
    private readonly string _root;

    public LinkedWorktreeTests()
    {
        _root = TestTempRoot.For("LinkedWorktreeTests_");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Loud: a copy that cannot be removed is a defect in the test, not a folder to leave behind.
        TestTempRoot.DeleteTree(_root);
    }

    // ---------------------------------------------------------------- the flow, with real git

    // ------------------------------------------------- the shapes git will not make on demand

    [Fact]
    public void AGitFilePointingAtNothing_IsLeftAlone()
    {
        // Written by hand: the file git leaves behind when a repository is moved or destroyed.
        var worktree = WriteGitFile("dangling", "gitdir: " + Path.Combine(_root, "gone", ".git", "worktrees", "x"));

        Assert.True(LinkedWorktree.IsOne(worktree));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(worktree));
    }

    [Fact]
    public void ABareRepositorysWorktree_IsLeftAlone_BecauseThereIsNoWorkingTreeToCreditIt()
    {
        // A bare repository's git directory IS the repository, so it is not called .git and there is
        // no working tree anybody could have started a session in. Written by hand because git will
        // not put a bare repository where this test wants one without more ceremony than the rule
        // being proved is worth.
        var bare = Path.Combine(_root, "devthrottle.git");
        Directory.CreateDirectory(Path.Combine(bare, "worktrees", "x"));
        var worktree = WriteGitFile("from-bare", "gitdir: " + Path.Combine(bare, "worktrees", "x"));

        Assert.Null(LinkedWorktree.ParentRepositoryOf(worktree));
    }

    [Fact]
    public void AGitFileThatSaysSomethingElseEntirely_IsLeftAlone()
    {
        Assert.Null(LinkedWorktree.ParentRepositoryOf(WriteGitFile("empty", "")));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(WriteGitFile("blank-target", "gitdir:   ")));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(WriteGitFile("prose", "this is not a git file")));
    }

    [Fact]
    public void AFolderThatDoesNotExist_IsLeftAlone()
    {
        var missing = Path.Combine(_root, "never-existed");

        Assert.False(LinkedWorktree.IsOne(missing));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(missing));
    }

    [Fact]
    public void NoPathAtAll_IsLeftAlone()
    {
        Assert.False(LinkedWorktree.IsOne(null));
        Assert.False(LinkedWorktree.IsOne("   "));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(null));
        Assert.Null(LinkedWorktree.ParentRepositoryOf("   "));
    }

    // ----------------------------------------------------------------- the two steps, on their own

    /// <summary>
    /// Both separators, because a Windows Director's paths are read on a Windows Director - but the
    /// rule is written to the PATH'S OWN shape rather than the host's, which is the defect family this
    /// mission has now met six times. These cases run on whatever machine the suite is on.
    /// </summary>
    [Theory]
    [InlineData("/repos/devthrottle/.git/worktrees/p5", "/repos/devthrottle/.git")]
    [InlineData(@"D:\ReposFred\devthrottle\.git\worktrees\p5", @"D:\ReposFred\devthrottle\.git")]
    [InlineData("/repos/devthrottle/.git/worktrees/p5/", "/repos/devthrottle/.git")]
    [InlineData("/repos/worktrees/.git/worktrees/p5", "/repos/worktrees/.git")]
    public void RepositoryGitDirectoryOf_AWorktreeEntry_IsTheRepositorysOwnGitDirectory(string entry, string expected)
        => Assert.Equal(expected, LinkedWorktree.RepositoryGitDirectoryOf(entry));

    [Theory]
    [InlineData("/repos/devthrottle/.git/modules/vendored")]   // a submodule
    [InlineData("/repos/devthrottle/.git")]                    // the repository's own git directory
    [InlineData("worktrees/p5")]                               // no repository above it
    [InlineData("")]
    [InlineData(null)]
    public void RepositoryGitDirectoryOf_AnythingElse_IsNull(string? entry)
        => Assert.Null(LinkedWorktree.RepositoryGitDirectoryOf(entry));

    [Theory]
    [InlineData("/repos/devthrottle/.git", "/repos/devthrottle")]
    [InlineData(@"D:\ReposFred\devthrottle\.git", @"D:\ReposFred\devthrottle")]
    [InlineData("/repos/devthrottle/.git/", "/repos/devthrottle")]
    public void PrimaryWorkingTreeOf_AGitDirectory_IsTheFolderHoldingIt(string gitDirectory, string expected)
        => Assert.Equal(expected, LinkedWorktree.PrimaryWorkingTreeOf(gitDirectory));

    [Theory]
    [InlineData("/repos/devthrottle.git")]   // bare - the git directory IS the repository
    [InlineData("/repos/devthrottle")]
    [InlineData(".git")]                     // nothing above it to be the working tree
    [InlineData("")]
    [InlineData(null)]
    public void PrimaryWorkingTreeOf_AnythingWithoutAWorkingTree_IsNull(string? gitDirectory)
        => Assert.Null(LinkedWorktree.PrimaryWorkingTreeOf(gitDirectory));

    // -------------------------------------------------------------------------------- helpers

    private string WriteGitFile(string folderName, string contents)
    {
        var path = Path.Combine(_root, folderName);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".git"), contents);
        return path;
    }
}
