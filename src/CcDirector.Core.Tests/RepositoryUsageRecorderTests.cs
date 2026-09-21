using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The one-repository-list mission, phase 1: which repository was used last is recorded for EVERY
/// session this Director creates, not only the ones started by the desktop dialog's own button.
///
/// The defect these tests exist to stop coming back: the last-used time had exactly one writer in the
/// whole product - the desktop New Session dialog - so a session started from the Cockpit, the phone,
/// a schedule or an agent moved nothing, and the repository list was ordered by a signal that had
/// never seen most of the work done on the machine.
/// </summary>
public sealed class RepositoryUsageRecorderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RepositoryRegistry _registry;
    private readonly SessionManager _sessions;

    public RepositoryUsageRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RepoUsageRecorderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _registry = new RepositoryRegistry(Path.Combine(_tempDir, "repositories.json"));
        _registry.Load();
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>A registered repository that is NOT on disk, so announcing a session in it does not
    /// reach the worktree reservation store - the repository catalogue is what is under test here.</summary>
    private string RegisterRepository(string folderName)
    {
        var path = Path.Combine(_tempDir, "registered", folderName);
        Assert.True(_registry.TryAdd(path));
        return path;
    }

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

    private DateTime? LastUsedOf(string path) =>
        _registry.Repositories.Single(r => r.Path == path.TrimEnd('\\', '/')).LastUsed;

    [Fact]
    public void SessionCreated_ByAnyRoute_MarksItsRepositoryUsed()
    {
        // The phase 1 row itself: nothing here is the desktop dialog. RaiseSessionCreated is the one
        // place every creation route funnels through - the desktop window, the Cockpit's and the
        // phone's create verb, a schedule, an agent spawning a worker - so a session announced here
        // stands for every one of them.
        var repo = RegisterRepository("cockpit-started");
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);
        var before = DateTime.UtcNow;

        using var session = NewSession(repo);
        _sessions.RaiseSessionCreated(session);

        var lastUsed = LastUsedOf(repo);
        Assert.NotNull(lastUsed);
        Assert.InRange(lastUsed!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void SessionCreated_MakesThatRepositoryTheMostRecentlyUsedOne()
    {
        // The order, which is what the list is for: the repository of the newest session sits on top,
        // whatever the order the repositories were registered in.
        var older = RegisterRepository("older");
        var newest = RegisterRepository("newest");
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var first = NewSession(older);
        _sessions.RaiseSessionCreated(first);
        using var second = NewSession(newest);
        _sessions.RaiseSessionCreated(second);

        var ordered = _registry.Repositories
            .OrderByDescending(r => r.LastUsed ?? DateTime.MinValue)
            .Select(r => r.Path)
            .ToList();
        Assert.Equal(newest.TrimEnd('\\', '/'), ordered[0]);
    }

    [Fact]
    public void SessionCreated_WithoutTheRecorder_MarksNothing()
    {
        // The failure this phase removes, kept as a test so the fix cannot quietly be undone: with no
        // recorder subscribed, a session created by any route other than the desktop dialog left the
        // last-used time untouched and the repository never moved.
        var repo = RegisterRepository("unrecorded");

        using var session = NewSession(repo);
        _sessions.RaiseSessionCreated(session);

        Assert.Null(LastUsedOf(repo));
    }

    [Fact]
    public void SessionCreated_InAPooledWorktree_MarksTheRepositoryTheSlotCameFrom()
    {
        // A pooled session RUNS in a throwaway slot, and its repository path reports that slot. The
        // repository a person picks out of the list is the one the slot came out of, and it is the one
        // that must move.
        var repo = RegisterRepository("pooled");
        var slot = Path.Combine(_tempDir, "pool", "wt01");
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var session = NewSession(repo, new PooledWorktree(repo, "wt01", slot, "lease-1"));
        Assert.Equal(slot, session.RepoPath);
        _sessions.RaiseSessionCreated(session);

        Assert.NotNull(LastUsedOf(repo));
        Assert.DoesNotContain(_registry.Repositories, r => r.Path == slot);
    }

    [Fact]
    public void Record_RepositoryIsNotRegistered_RecordsNothingAndDoesNotThrow()
    {
        // The failure case: a session in a folder this machine has never registered. There is nothing
        // to mark, and a picker catalogue must never be the reason a session start fails.
        var registered = RegisterRepository("registered");
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        using var session = NewSession(Path.Combine(_tempDir, "never-registered"));
        _sessions.RaiseSessionCreated(session);

        Assert.Null(LastUsedOf(registered));
    }

    [Fact]
    public void Record_SessionHasNoRepositoryAtAll_RecordsNothing()
    {
        var registered = RegisterRepository("registered");
        using var recorder = new RepositoryUsageRecorder(_sessions, _registry);

        recorder.Record(NewSession("   "));

        Assert.Null(LastUsedOf(registered));
    }

    [Fact]
    public void Dispose_StopsRecording()
    {
        var repo = RegisterRepository("after-dispose");
        var recorder = new RepositoryUsageRecorder(_sessions, _registry);
        recorder.Dispose();

        using var session = NewSession(repo);
        _sessions.RaiseSessionCreated(session);

        Assert.Null(LastUsedOf(repo));
    }
}

/// <summary>
/// The one rule both catalogues resolve the repository with - the Director's own registry and the
/// Gateway's known-repository store.
/// </summary>
public sealed class RepositoryUsageTests
{
    [Fact]
    public void StartedIn_NoPooledWorktree_IsTheSessionsOwnRepository()
        => Assert.Equal("/repos/devthrottle", RepositoryUsage.StartedIn("/repos/devthrottle", null, null));

    [Fact]
    public void StartedIn_PooledWorktree_IsTheRepositoryTheSlotCameFrom()
        => Assert.Equal("/repos/devthrottle",
            RepositoryUsage.StartedIn("/pool/devthrottle/wt01", "/repos/devthrottle", null));

    [Fact]
    public void StartedIn_BlankPooledRepository_FallsBackToTheSessionsOwnRepository()
        => Assert.Equal("/repos/devthrottle", RepositoryUsage.StartedIn("/repos/devthrottle", "   ", null));

    [Fact]
    public void StartedIn_NeitherIsKnown_IsNullSoNothingIsRecorded()
    {
        Assert.Null(RepositoryUsage.StartedIn(null, null, null));
        Assert.Null(RepositoryUsage.StartedIn("  ", null, null));
    }

    [Fact]
    public void StartedIn_TrimsSurroundingWhitespace()
        => Assert.Equal("/repos/devthrottle", RepositoryUsage.StartedIn("  /repos/devthrottle  ", null, null));

    // A WORKTREE IS NOT A REPOSITORY (the one-repository-list mission). The rule itself, at this level,
    // is only the precedence; what decides whether a folder IS a worktree is LinkedWorktree, and it has
    // its own tests against real folders on a real disk.

    [Fact]
    public void StartedIn_AWorktree_IsTheRepositoryItIsAWorktreeOf()
        => Assert.Equal("/repos/devthrottle",
            RepositoryUsage.StartedIn("/repos/devthrottle-p5-run-a", null, "/repos/devthrottle"));

    [Fact]
    public void StartedIn_AWorktreeWhoseRepositoryCouldNotBeResolved_IsTheFolderItself()
        => Assert.Equal("/repos/devthrottle-p5-run-a",
            RepositoryUsage.StartedIn("/repos/devthrottle-p5-run-a", null, null));

    [Fact]
    public void StartedIn_ABlankResolvedRepository_IsTheFolderItself()
        => Assert.Equal("/repos/devthrottle-p5-run-a",
            RepositoryUsage.StartedIn("/repos/devthrottle-p5-run-a", null, "   "));

    [Fact]
    public void StartedIn_TrimsTheResolvedRepositoryToo()
        => Assert.Equal("/repos/devthrottle",
            RepositoryUsage.StartedIn("/repos/devthrottle-p5-run-a", null, "  /repos/devthrottle  "));

    /// <summary>
    /// The pooled slot keeps its precedence. A pooled slot IS a git worktree, so both answers are
    /// available and they normally agree - but the pool's own record is a fact it wrote down, needing no
    /// disk, and it is right even when the slot has already been handed back. This is a WRONG RULE
    /// nothing removed: it fails if a later change ever reorders the two.
    /// </summary>
    [Fact]
    public void StartedIn_APooledSlotThatIsAlsoAWorktree_StillAnswersWithThePoolsOwnRepository()
        => Assert.Equal("/repos/devthrottle",
            RepositoryUsage.StartedIn("/pool/devthrottle/wt01", "/repos/devthrottle", "/somewhere/else"));
}
