using System.Diagnostics;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A WORKTREE IS NOT A REPOSITORY. USING A WORKTREE OF <c>devthrottle</c> IS USING <c>devthrottle</c>
/// (the one-repository-list mission, "a worktree is not a repository") - proved at the Director, where
/// the question is answered, with real repositories and real <c>git worktree add</c> on a real disk.
///
/// <para>The defect: every agent session on this fleet runs in a git worktree, so every worktree that
/// ever hosted one became its own row. One Windows machine's list served 559 repositories, thirteen of
/// whose top twenty rows were worktrees. The owner, reading it: "here you are showing the work trees.
/// We should only be showing the repos."</para>
///
/// <para>These drive <see cref="SessionManager.RaiseSessionCreated"/>, which is the one place every
/// creation route funnels through - the desktop window, the Cockpit's and the phone's create verb, a
/// schedule, an agent spawning a worker, and a restore after a Director restart. A session announced
/// here stands for every one of them.</para>
/// </summary>
public sealed class AWorktreeIsNotARepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly RepositoryRegistry _registry;
    private readonly SessionManager _sessions;

    public AWorktreeIsNotARepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"AWorktreeIsNotARepository_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _registry = new RepositoryRegistry(Path.Combine(_root, "repositories.json"));
        _registry.Load();
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------- the stamp on the session

    [Fact]
    public void ASessionStartedInAWorktree_CarriesTheRepositoryItIsAWorktreeOf()
    {
        // THE ROW THIS WORK EXISTS FOR. RepoPath still says where the session IS - nothing moves the
        // session - and the repository it is a use OF rides beside it.
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");

        using var session = NewSession(worktree);
        _sessions.RaiseSessionCreated(session);

        Assert.Equal(worktree, session.RepoPath);
        AssertSameFolder(repository, session.PrimaryRepoPath);
    }

    [Fact]
    public void ASessionStartedInARepositoryProper_CarriesNothing()
    {
        // The contrast. A clone is already the answer, so there is nothing to carry and the catalogues
        // record it exactly as they did before this existed.
        var repository = MakeRepository("devthrottle");

        using var session = NewSession(repository);
        _sessions.RaiseSessionCreated(session);

        Assert.Null(session.PrimaryRepoPath);
    }

    [Fact]
    public void ASessionStartedInAWorktreeWhoseRepositoryIsGone_CarriesNothing()
    {
        // FAILURE CASE. There is nothing to credit, so the folder keeps its own row rather than being
        // folded onto a guess. A destructive change acts only on what it can positively prove.
        var repository = MakeRepository("doomed");
        var worktree = AddWorktree(repository, "orphan", "orphan");
        Directory.Delete(repository, recursive: true);

        using var session = NewSession(worktree);
        _sessions.RaiseSessionCreated(session);

        Assert.Null(session.PrimaryRepoPath);
    }

    [Fact]
    public void ASessionStartedInAFolderThatIsNotARepositoryAtAll_CarriesNothing()
    {
        // FAILURE CASE, and a real row: one machine's catalogue holds its own ROOT FOLDER as a
        // repository, because a session was once started there. It is not a worktree, nothing can be
        // resolved for it, and it is left exactly as it is.
        var plain = Path.Combine(_root, "ReposFred");
        Directory.CreateDirectory(plain);

        using var session = NewSession(plain);
        _sessions.RaiseSessionCreated(session);

        Assert.Null(session.PrimaryRepoPath);
    }

    [Fact]
    public void WhenResolvingThrows_TheSessionIsStillCreatedAndCarriesNothing()
    {
        // FAILURE CASE. A repository picker's ordering must never be the reason a session fails to
        // start - the same rule the usage recorder is written to.
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");
        _sessions.PrimaryRepositoryResolver = _ => throw new IOException("the disk went away");

        using var session = NewSession(worktree);
        var announced = false;
        _sessions.OnSessionCreated += _ => announced = true;
        _sessions.RaiseSessionCreated(session);

        Assert.Null(session.PrimaryRepoPath);
        Assert.True(announced);
    }

    [Fact]
    public void ARestoredSessionIsResolvedAgainstTheDiskAsItIsNow_NotAsItWas()
    {
        // A session announced a second time - which is what a restore after a Director restart does -
        // is stamped from the disk again. The disk is what answers, and it may have moved while this
        // Director was not running.
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");

        using var session = NewSession(worktree);
        _sessions.RaiseSessionCreated(session);
        AssertSameFolder(repository, session.PrimaryRepoPath);

        Directory.Delete(repository, recursive: true);
        _sessions.RaiseSessionCreated(session);

        Assert.Null(session.PrimaryRepoPath);
    }

    // -------------------------------------------------------- what the Director's own list records

    [Fact]
    public void ASessionInAWorktree_MovesTheREPOSITORYUpTheList_NotTheWorktree()
    {
        // The end of the road on this side: the Director's own registry, which is what the desktop
        // New Session dialog orders by.
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");
        // Registered under the folder's real spelling, because that is what git recorded inside the
        // worktree and the registry matches paths lexically - see
        // ARepositoryRegisteredThroughASymbolicLink_IsNotMatched below, which is that limit stated as
        // a test rather than left for someone to find.
        Assert.True(_registry.TryAdd(RealPath(repository)));
        // THE SECOND DOOR, shut: adding the worktree by hand adds nothing, because it resolves to the
        // repository that is already in the list.
        Assert.False(_registry.TryAdd(worktree));
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var session = NewSession(worktree);
        _sessions.RaiseSessionCreated(session);

        Assert.NotNull(LastUsedOf(RealPath(repository)));
        Assert.DoesNotContain(_registry.Repositories, r => r.Path == worktree.TrimEnd('\\', '/'));
    }

    [Fact]
    public void AWorktreeAddedByHand_IsRegisteredAsTheRepositoryItIsAWorktreeOf()
    {
        // THE SECOND SOURCE of worktree rows, on its own. RepositoryRegistry.TryAdd never checked what
        // a folder was, and since the Director's push became the scan UNION this list, a worktree added
        // through Browse or the repo-add verb became a row in the one repository list that the
        // root-folder scan could never have put there.
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");

        Assert.True(_registry.TryAdd(worktree));

        var registered = Assert.Single(_registry.Repositories);
        Assert.Equal(RealPath(repository), RealPath(registered.Path));
    }

    [Fact]
    public void AFolderThatIsNotAWorktree_IsRegisteredExactlyAsGiven()
    {
        // The contrast: a clone, and a folder that is not a repository at all, are both registered as
        // themselves. Nothing here refuses a folder - this rule renames what is added, it never blocks.
        var repository = MakeRepository("devthrottle");
        var plain = Path.Combine(_root, "notes");
        Directory.CreateDirectory(plain);

        Assert.True(_registry.TryAdd(repository));
        Assert.True(_registry.TryAdd(plain));

        Assert.Contains(_registry.Repositories, r => r.Path == repository.TrimEnd('\\', '/'));
        Assert.Contains(_registry.Repositories, r => r.Path == plain.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// A KNOWN LIMIT, stated rather than hidden. git records the REAL path of the repository inside a
    /// worktree's <c>.git</c> file - symbolic links and junctions resolved - while
    /// <see cref="RepositoryRegistry"/> compares the spelling a person browsed to, lexically. So a
    /// repository registered through a link is not matched by the resolved answer, and its last-used
    /// time does not move; the repository still appears in every list, ordered by whatever it had
    /// before, and nothing is lost or duplicated on this side.
    ///
    /// It does not bite on the machines this fleet runs: the registered roots are
    /// <c>D:\ReposFred</c>, <c>D:\ReposMindzie</c> and <c>/Users/soren/ReposFred</c>, none of which
    /// is reached through a link. It is here so that the day it does bite, this test names it.
    /// </summary>
    [Fact]
    public void ARepositoryRegisteredThroughASymbolicLink_IsNotMatched()
    {
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");
        var link = Path.Combine(_root, "link-to-devthrottle");
        Directory.CreateSymbolicLink(link, repository);
        Assert.True(_registry.TryAdd(link));
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var session = NewSession(worktree);
        _sessions.RaiseSessionCreated(session);

        AssertSameFolder(repository, session.PrimaryRepoPath);
        Assert.Null(LastUsedOf(link));
    }

    [Fact]
    public void ASessionInAWorktreeWhoseRepositoryIsGone_StillMovesTheWorktree()
    {
        // FAILURE CASE, and the property that makes this safe to ship: when nothing can be resolved,
        // the product behaves exactly as it did before. An unresolvable worktree still gets its use
        // recorded, against itself, rather than being lost.
        var repository = MakeRepository("doomed");
        var worktree = AddWorktree(repository, "orphan", "orphan");
        Directory.Delete(repository, recursive: true);
        // Registered AFTER the repository is gone, so nothing can be resolved and the worktree is
        // registered as itself - which is the product's behaviour before any of this existed.
        Assert.True(_registry.TryAdd(worktree));
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var session = NewSession(worktree);
        _sessions.RaiseSessionCreated(session);

        Assert.NotNull(LastUsedOf(worktree));
    }

    [Fact]
    public void APooledSession_StillCreditsTheRepositoryTheSlotCameFrom()
    {
        // A POOLED WORKTREE MUST KEEP WORKING EXACTLY AS IT DOES NOW. Its slot is a real git worktree,
        // so both answers are now available; the pool's own record is what is used, because it is a
        // fact the pool wrote down and it is right even when the slot has already been handed back.
        var repository = MakeRepository("devthrottle");
        var slot = AddWorktree(repository, Path.Combine("pool", "wt01"), "slot-01");
        Assert.True(_registry.TryAdd(repository));
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var session = NewSession(slot, new PooledWorktree(repository, "wt01", slot, "lease-1"));
        _sessions.RaiseSessionCreated(session);

        Assert.NotNull(LastUsedOf(repository));
        Assert.Equal(slot, session.RepoPath);
    }

    // -------------------------------------------------------------------------------- helpers

    private DateTime? LastUsedOf(string path) =>
        _registry.Repositories.Single(r => r.Path == path.TrimEnd('\\', '/')).LastUsed;

    private static Session NewSession(string repoPath, PooledWorktree? pooled = null)
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: pooled?.Path ?? repoPath,
            workingDirectory: pooled?.Path ?? repoPath,
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        session.PooledWorktree = pooled;
        return session;
    }

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

    private static void AssertSameFolder(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(RealPath(expected), RealPath(actual!));
    }

    /// <summary>Symbolic links resolved ANYWHERE in the path - see the note in
    /// <see cref="LinkedWorktreeTests"/>: on macOS the temporary folder's ancestor is a link, and
    /// <c>ResolveLinkTarget</c> answers about the last component alone.</summary>
    private static string RealPath(string path)
    {
        var resolved = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                       ?? Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(resolved);
        return string.IsNullOrEmpty(parent) || parent == resolved
            ? resolved
            : Path.Combine(RealPath(parent), Path.GetFileName(resolved));
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
