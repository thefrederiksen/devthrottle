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
[Collection("ProcessWideState")]
public sealed class AWorktreeIsNotARepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SessionManager _sessions;

    public AWorktreeIsNotARepositoryTests()
    {
        _root = TestTempRoot.For("AWorktreeIsNotARepository_");
        Directory.CreateDirectory(_root);
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        // Loud: a copy that cannot be removed is a defect in the test, not a folder to leave behind.
        TestTempRoot.DeleteTree(_root);
    }

    // ------------------------------------------------------------------- the stamp on the session

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

    // -------------------------------------------------------------------------------- helpers

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
}
