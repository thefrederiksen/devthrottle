using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SessionGitStatusMonitor"/> - the Director-side poll that publishes each live
/// session's uncommitted file count onto <see cref="Session.UncommittedCount"/>, which is what puts the
/// number on the wire for the Cockpit roster and the phone.
///
/// The property under test in most of these is the one that is easy to get wrong and impossible to see once
/// it ships: A FAILED PROBE MUST NOT LOOK LIKE A CLEAN TREE (issue 516). A count of 0 and "we could not
/// tell" render identically to a reader that collapses them, so the monitor publishes only counts a probe
/// actually produced, and leaves the last known value alone otherwise.
/// </summary>
public sealed class SessionGitStatusMonitorTests
{
    [Fact]
    public async Task RefreshOnce_PublishesTheCountOntoTheSession()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        Assert.Null(session.UncommittedCount);   // nothing probed yet is UNKNOWN, not zero

        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: true, Count: 7));
        var published = await monitor.RefreshOnceAsync();

        Assert.Equal(1, published);
        Assert.Equal(7, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_CleanTreePublishesZero_WhichIsADifferentAnswerFromUnknown()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: true, Count: 0));
        await monitor.RefreshOnceAsync();

        // A SUCCESSFUL probe that found nothing is a verified-clean tree, and 0 is the honest report of it.
        // The distinction this test protects is against null (unknown), asserted in the tests below.
        Assert.Equal(0, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_FailedProbeLeavesTheLastKnownCount_NeverAFalseZero()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var succeed = true;
        var monitor = Monitor(manager, probe: _ => succeed
            ? new GitCountResult(Success: true, Count: 12)
            : new GitCountResult(Success: false, Count: 0));

        await monitor.RefreshOnceAsync();
        Assert.Equal(12, session.UncommittedCount);

        // git falls over on the next cycle. The count is now UNKNOWN - and unknown must not overwrite a real
        // measurement with a zero, which every reader downstream would render as "this tree is clean".
        succeed = false;
        var published = await monitor.RefreshOnceAsync();

        Assert.Equal(0, published);
        Assert.Equal(12, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_ProbeThatNeverSucceededLeavesTheCountNull()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: false, Count: 0));
        await monitor.RefreshOnceAsync();

        // Null, not 0. A client renders no badge for null; a 0 here would claim a verified-clean tree we
        // have never actually observed.
        Assert.Null(session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_MissingRepositoryFolderIsNotProbedAndKeepsItsLastCount()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);
        session.UncommittedCount = 3;

        var probed = false;
        var monitor = Monitor(manager,
            probe: _ => { probed = true; return new GitCountResult(Success: true, Count: 99); },
            directoryExists: _ => false);

        await monitor.RefreshOnceAsync();

        // A session whose folder was deleted or moved is unreadable, not clean.
        Assert.False(probed);
        Assert.Equal(3, session.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_OneThrowingProbeDoesNotStopTheOtherSessions()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var bad = NewSession(@"C:\test\bad");
        using var good = NewSession(@"C:\test\good");
        manager.AdoptSession(bad);
        manager.AdoptSession(good);

        var monitor = Monitor(manager, probe: path => path.EndsWith("bad", StringComparison.Ordinal)
            ? throw new InvalidOperationException("git exploded")
            : new GitCountResult(Success: true, Count: 4));

        var published = await monitor.RefreshOnceAsync();

        Assert.Equal(1, published);
        Assert.Null(bad.UncommittedCount);
        Assert.Equal(4, good.UncommittedCount);
    }

    [Fact]
    public async Task RefreshOnce_RaisesTheChangeEventOnlyWhenTheNumberActuallyMoves()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var session = NewSession();
        manager.AdoptSession(session);

        var raised = 0;
        session.OnUncommittedCountChanged += _ => raised++;

        var count = 5;
        var monitor = Monitor(manager, probe: _ => new GitCountResult(Success: true, Count: count));

        await monitor.RefreshOnceAsync();
        await monitor.RefreshOnceAsync();   // same number - an idle session must push nothing
        Assert.Equal(1, raised);

        count = 6;
        await monitor.RefreshOnceAsync();
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task RefreshOnce_ProbesEachRepositoryOnce_NotEachSession()
    {
        using var manager = new SessionManager(new AgentOptions());
        var sessions = new List<Session>();
        // Ten sessions, two trees - the shape a Director actually holds (issue #1111, item 2).
        for (var i = 0; i < 7; i++) sessions.Add(NewSession(@"C:\test\alpha"));
        for (var i = 0; i < 3; i++) sessions.Add(NewSession(@"C:\test\beta"));
        foreach (var s in sessions) manager.AdoptSession(s);

        try
        {
            var asked = new List<string>();
            var monitor = Monitor(manager, probe: path =>
            {
                lock (asked) asked.Add(path);
                return new GitCountResult(Success: true, Count: 4);
            });

            var published = await monitor.RefreshOnceAsync();

            // Two questions, two probes - not ten. Each probe is a `git status` against a tree that live
            // agents are writing to, so the duplicates were not merely wasted, they were contended.
            Assert.Equal(2, asked.Count);

            // And every session still gets its number: deduplicating the PROBE must not deduplicate the ANSWER.
            Assert.Equal(10, published);
            Assert.All(sessions, s => Assert.Equal(4, s.UncommittedCount));
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    [Fact]
    public async Task RefreshOnce_TreatsTheSameDirectorySpelledTwoWaysAsOneRepository()
    {
        using var manager = new SessionManager(new AgentOptions());
        // The exact defect from item 3 of the issue: RepoPath is stored however it arrived, so one tree
        // shows up under several spellings. Grouping on the raw string would silently keep the duplication -
        // this is the test that fails if the grouping key stops being canonical.
        // FOUR SPELLINGS OF ONE DIRECTORY, chosen so they are genuinely one directory on the platform
        // under test. The Windows set leans on the separator and on case; neither is available off
        // Windows - a backslash is an ordinary character in a Unix file name, and a case variant is a
        // DIFFERENT directory on a case-sensitive filesystem, so asserting that it groups would be
        // asserting something untrue on Linux. Redundant and trailing separators are unambiguous on
        // both.
        var (a, b, c, d) = OperatingSystem.IsWindows()
            ? ("C:/test/alpha", @"C:\test\alpha", @"C:\test\alpha\", @"C:\Test\Alpha")
            : ("/test/alpha", "/test/alpha/", "/test/./alpha", "/test//alpha");

        using var forward = NewSession(a);
        using var backward = NewSession(b);
        using var trailing = NewSession(c);
        using var cased = NewSession(d);
        manager.AdoptSession(forward);
        manager.AdoptSession(backward);
        manager.AdoptSession(trailing);
        manager.AdoptSession(cased);

        var asked = new List<string>();
        var monitor = Monitor(manager, probe: path =>
        {
            lock (asked) asked.Add(path);
            return new GitCountResult(Success: true, Count: 9);
        });

        var published = await monitor.RefreshOnceAsync();

        Assert.Single(asked);
        Assert.Equal(4, published);
        Assert.Equal(9, forward.UncommittedCount);
        Assert.Equal(9, backward.UncommittedCount);
        Assert.Equal(9, trailing.UncommittedCount);
        Assert.Equal(9, cased.UncommittedCount);

        // git is handed a real path, never the lowercased comparison key - that key exists to compare with,
        // not to run a process against.
        Assert.DoesNotContain(asked, p => p == RepoPathKey.For(p) && p != p.ToLowerInvariant());
    }

    [Fact]
    public async Task RefreshOnce_OneUnreadableRepositoryStillLeavesItsOwnSessionsAlone()
    {
        using var manager = new SessionManager(new AgentOptions());
        using var goodA = NewSession(@"C:\test\good");
        using var goodB = NewSession(@"C:\test\good");
        using var bad = NewSession(@"C:\test\bad");
        manager.AdoptSession(goodA);
        manager.AdoptSession(goodB);
        manager.AdoptSession(bad);
        bad.UncommittedCount = 2;

        var monitor = Monitor(manager, probe: path => path.EndsWith("bad", StringComparison.Ordinal)
            ? throw new InvalidOperationException("git exploded")
            : new GitCountResult(Success: true, Count: 6));

        var published = await monitor.RefreshOnceAsync();

        // Grouping must not let one failing tree take its neighbours down with it, nor overwrite the last
        // known count for the sessions that share the failing one.
        Assert.Equal(2, published);
        Assert.Equal(6, goodA.UncommittedCount);
        Assert.Equal(6, goodB.UncommittedCount);
        Assert.Equal(2, bad.UncommittedCount);
    }

    private static SessionGitStatusMonitor Monitor(
        SessionManager manager,
        Func<string, GitCountResult> probe,
        Func<string, bool>? directoryExists = null)
        => new(manager,
            interval: TimeSpan.FromHours(1),          // the loop never runs; the tests drive RefreshOnceAsync
            probe: (path, _) => Task.FromResult(probe(path)),
            directoryExists: directoryExists ?? (_ => true));

    private static Session NewSession(string repoPath = @"C:\test\repo")
        => new(Guid.NewGuid(), repoPath, repoPath, null, new NullBackend(), SessionBackendType.ConPty);

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
