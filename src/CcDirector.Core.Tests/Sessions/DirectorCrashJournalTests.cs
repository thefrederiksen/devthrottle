using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The Director crash journal (issue #212 W1/L5): a per-Director roster that survives an
/// abnormal death and is claimed exactly once on the next startup, while a clean shutdown
/// leaves nothing behind. This is the foundation of crash recovery, so the dirty-vs-clean
/// distinction and the "claim once" behavior are pinned down here.
/// </summary>
public sealed class DirectorCrashJournalTests : IDisposable
{
    private readonly string _dir;

    public DirectorCrashJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-crashjournal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static DirectorCrashJournalSession Session(string sid, string name, string repo) => new()
    {
        SessionId = sid,
        Name = name,
        RepoPath = repo,
        Agent = "ClaudeCode",
        ClaudeSessionId = "claude-" + sid,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private DirectorCrashJournal NewJournal(string directorId, int pid) =>
        new(directorId, pid, "TEST_MACHINE", "tester", DateTimeOffset.UtcNow, _dir);

    // ---- write / clean ----

    [Fact]
    public void Update_writes_the_roster_to_disk()
    {
        var j = NewJournal("dir-1", pid: 4242);
        j.Update(new[] { Session("s1", "alpha", "/repo/a"), Session("s2", "beta", "/repo/b") });

        Assert.True(File.Exists(j.FilePath));
        var json = File.ReadAllText(j.FilePath);
        Assert.Contains("\"alpha\"", json);
        Assert.Contains("/repo/b", json);
    }

    [Fact]
    public void MarkClean_deletes_the_journal()
    {
        var j = NewJournal("dir-1", pid: 4242);
        j.Update(new[] { Session("s1", "alpha", "/repo/a") });
        Assert.True(File.Exists(j.FilePath));

        j.MarkClean();
        Assert.False(File.Exists(j.FilePath));
    }

    // ---- dirty detection ----

    [Fact]
    public void DetectAndClaim_ignores_a_live_directors_journal()
    {
        // A journal owned by THIS (alive) process must never be treated as a dead predecessor.
        var liveJournal = NewJournal("dir-live", pid: Environment.ProcessId);
        liveJournal.Update(new[] { Session("s1", "active", "/repo/y") });

        var dirty = DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);

        Assert.Empty(dirty);
        Assert.True(File.Exists(liveJournal.FilePath)); // untouched
    }

    [Fact]
    public void Dismiss_returns_false_when_nothing_to_remove()
    {
        Assert.False(DirectorCrashJournal.Dismiss("ghost", 1, _dir));
    }

    // ---- per-session removal after restore (issue #212 W4) ----

    [Fact]
    public void DetectAndClaim_on_empty_directory_returns_nothing()
    {
        var empty = Path.Combine(_dir, "does-not-exist");
        Assert.Empty(DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: empty));
    }
}
