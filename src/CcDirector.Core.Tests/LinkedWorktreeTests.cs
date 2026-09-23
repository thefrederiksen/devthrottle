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
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- the flow, with real git

    [Fact]
    public void ARealWorktree_ResolvesToTheRepositoryItWasMadeFrom()
    {
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");

        // The owner's own example: D:\ReposFred\devthrottle-p5-run-a is a worktree of
        // D:\ReposFred\devthrottle and sits beside it, sharing not one character of its name.
        Assert.True(LinkedWorktree.IsOne(worktree));
        AssertSameFolder(repository, LinkedWorktree.ParentRepositoryOf(worktree));
    }

    [Fact]
    public void ARealWorktreeInAFolderCalledWorktrees_ResolvesToTheRepositoryToo()
    {
        // The other half of the owner's example: D:\ReposMindzie\worktrees\idle-p5-a. The answer must
        // not depend on where the worktree was put, and the folder being called "worktrees" must not
        // be what makes this work - the previous test has no such folder anywhere in it.
        var repository = MakeRepository("mindzieWeb");
        Directory.CreateDirectory(Path.Combine(_root, "worktrees"));
        var worktree = AddWorktree(repository, Path.Combine("worktrees", "idle-p5-a"), "idle-p5-a");

        AssertSameFolder(repository, LinkedWorktree.ParentRepositoryOf(worktree));
    }

    [Fact]
    public void ARepositoryProper_IsNotAWorktreeAndIsLeftAlone()
    {
        // The contrast that makes the rest mean anything. A clone's .git is a DIRECTORY, so it is
        // never even asked, and the catalogue goes on recording it as itself.
        var repository = MakeRepository("devthrottle");

        Assert.False(LinkedWorktree.IsOne(repository));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(repository));
    }

    [Fact]
    public void APlainFolderInsideARepository_IsLeftAlone()
    {
        // THE TRAP, and the reason the guard is ".git is a FILE" and not "git can answer". Asked from
        // a sub-folder, git walks UP and cheerfully names the repository above it - so a session
        // started in repo/src would be credited to a repository the person never picked.
        var repository = MakeRepository("devthrottle");
        var inside = Path.Combine(repository, "src");
        Directory.CreateDirectory(inside);

        Assert.False(LinkedWorktree.IsOne(inside));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(inside));
    }

    [Fact]
    public void AWorktreeWhoseRepositoryWasDeleted_IsLeftAlone()
    {
        // A real worktree, made by real git, whose repository is then removed from the disk. There is
        // nothing to collapse it onto, so it keeps its own row exactly as the product did before.
        var repository = MakeRepository("doomed");
        var worktree = AddWorktree(repository, "orphan", "orphan");
        Directory.Delete(repository, recursive: true);

        Assert.True(LinkedWorktree.IsOne(worktree));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(worktree));
    }

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
    public void ASubmodule_IsLeftAlone_BecauseASubmoduleIsARepository()
    {
        // Written by hand, in git's documented layout: a submodule's .git file points into
        // $GIT_DIR/modules/<name>, not $GIT_DIR/worktrees/<id>. It must keep its own place in the
        // list - a submodule is a repository in its own right and nobody asked for it to be folded
        // into the project that holds it. The repository above it is REAL here, so the only thing
        // refusing this is the shape of the path git wrote.
        var repository = MakeRepository("superproject");
        var submodule = WriteGitFile("vendored", "gitdir: " + Path.Combine(repository, ".git", "modules", "vendored"));

        Assert.True(LinkedWorktree.IsOne(submodule));
        Assert.Null(LinkedWorktree.ParentRepositoryOf(submodule));
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
    public void AGitFileWithARelativeTarget_ResolvesAgainstTheWorktreesOwnFolder()
    {
        // git writes a relative target when a worktree is created with relative paths, so that a
        // repository and its worktrees can be moved together. Resolving it against the current
        // working directory instead of the worktree would answer a different folder every time.
        var repository = MakeRepository("devthrottle");
        var worktree = WriteGitFile("relative", "gitdir: ../devthrottle/.git/worktrees/relative");

        AssertSameFolder(repository, LinkedWorktree.ParentRepositoryOf(worktree));
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

    private string MakeRepository(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        RunGit(path, "-c", "init.defaultBranch=main", "init");
        RunGit(path, "-c", "user.email=test@example.com", "-c", "user.name=test", "commit", "--allow-empty", "-m", "first");
        return path;
    }

    private string AddWorktree(string repository, string relativePath, string branch)
    {
        var path = Path.Combine(_root, relativePath);
        RunGit(repository, "worktree", "add", path, "-b", branch);
        return path;
    }

    private string WriteGitFile(string folderName, string contents)
    {
        var path = Path.Combine(_root, folderName);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".git"), contents);
        return path;
    }

    /// <summary>
    /// The two paths name the same folder. Compared by resolving both rather than as strings, because
    /// on macOS the temporary folder is reached through a symbolic link (<c>/var</c> -&gt;
    /// <c>/private/var</c>) and git answers with the resolved spelling while a file read answers with
    /// the one it was handed. Which spelling comes back is not what these tests are about.
    /// </summary>
    private static void AssertSameFolder(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(RealPath(expected), RealPath(actual!));
    }

    /// <summary>
    /// The real path of a folder, with symbolic links resolved ANYWHERE in it - not only at the leaf.
    /// <c>ResolveLinkTarget</c> answers about the last component alone, so on macOS, where the
    /// temporary folder lives under <c>/var</c> and <c>/var</c> is a link to <c>/private/var</c>, it
    /// leaves the ancestor unresolved. git answers with the fully resolved spelling and a file read
    /// answers with the spelling it was handed, and which of the two comes back is not what these
    /// tests are about.
    /// </summary>
    private static string RealPath(string path)
    {
        // One implementation, in TestTempRoot, and it short-circuits Windows before it touches
        // ResolveLinkTarget. The hand-rolled recursion that used to live here walked UP the path
        // calling ResolveLinkTarget on every ancestor, including the volume root - and Windows
        // throws DirectoryNotFoundException when asked to resolve "C:\", so every test through
        // here failed on the build machine while passing on macOS.
        return TestTempRoot.Canonical(path);
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
    }
}
