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
    public void DetectAndClaim_finds_a_dead_directors_roster_and_claims_it_once()
    {
        // A journal left behind by a dead PID (pid 1 has surely exited; use a clearly-dead one).
        var deadJournal = NewJournal("dir-dead", pid: GetDeadPid());
        deadJournal.Update(new[] { Session("s1", "interrupted work", "/repo/x") });

        var dirty = DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);

        var d = Assert.Single(dirty);
        Assert.Equal("dir-dead", d.Data.DirectorId);
        Assert.Single(d.Data.Sessions);
        Assert.Equal("interrupted work", d.Data.Sessions[0].Name);

        // The live journal was renamed to .dirty.json (claimed); the original is gone.
        Assert.False(File.Exists(deadJournal.FilePath));
        Assert.True(File.Exists(d.DirtyFilePath));
        Assert.EndsWith(".dirty.json", d.DirtyFilePath);

        // A second scan does not re-report the already-claimed journal.
        var again = DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);
        Assert.Empty(again);
    }

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
    public void DetectAndClaim_deletes_a_dead_director_with_empty_roster()
    {
        var emptyJournal = NewJournal("dir-empty", pid: GetDeadPid());
        emptyJournal.Update(Array.Empty<DirectorCrashJournalSession>());
        Assert.True(File.Exists(emptyJournal.FilePath));

        var dirty = DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);

        Assert.Empty(dirty);                               // nothing to recover
        Assert.False(File.Exists(emptyJournal.FilePath));  // cleaned up
    }

    [Fact]
    public void ListPendingRecoveries_returns_claimed_dirty_journals()
    {
        var deadJournal = NewJournal("dir-dead", pid: GetDeadPid());
        deadJournal.Update(new[] { Session("s1", "interrupted", "/repo/x") });
        DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);

        var pending = DirectorCrashJournal.ListPendingRecoveries(_dir);

        var d = Assert.Single(pending);
        Assert.Equal("dir-dead", d.DirectorId);
        Assert.Equal("interrupted", d.Sessions[0].Name);
    }

    [Fact]
    public void Dismiss_removes_a_claimed_dirty_journal()
    {
        var deadPid = GetDeadPid();
        var deadJournal = NewJournal("dir-dead", pid: deadPid);
        deadJournal.Update(new[] { Session("s1", "interrupted", "/repo/x") });
        var dirty = Assert.Single(DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir));
        Assert.True(File.Exists(dirty.DirtyFilePath));

        var removed = DirectorCrashJournal.Dismiss("dir-dead", deadPid, _dir);

        Assert.True(removed);
        Assert.False(File.Exists(dirty.DirtyFilePath));
        Assert.Empty(DirectorCrashJournal.ListPendingRecoveries(_dir));
    }

    [Fact]
    public void Dismiss_returns_false_when_nothing_to_remove()
    {
        Assert.False(DirectorCrashJournal.Dismiss("ghost", 1, _dir));
    }

    // ---- per-session removal after restore (issue #212 W4) ----

    [Fact]
    public void RemoveSession_drops_one_session_and_keeps_the_rest()
    {
        var deadPid = GetDeadPid();
        var deadJournal = NewJournal("dir-dead", pid: deadPid);
        deadJournal.Update(new[]
        {
            Session("s1", "restored one", "/repo/x"),
            Session("s2", "still waiting", "/repo/y"),
        });
        DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);

        var removed = DirectorCrashJournal.RemoveSession("dir-dead", deadPid, "s1", _dir);

        Assert.True(removed);
        var pending = Assert.Single(DirectorCrashJournal.ListPendingRecoveries(_dir));
        var left = Assert.Single(pending.Sessions);
        Assert.Equal("s2", left.SessionId);
        Assert.Equal("still waiting", left.Name);
    }

    [Fact]
    public void RemoveSession_deletes_the_journal_when_it_was_the_last_session()
    {
        var deadPid = GetDeadPid();
        var deadJournal = NewJournal("dir-dead", pid: deadPid);
        deadJournal.Update(new[] { Session("s1", "only one", "/repo/x") });
        var dirty = Assert.Single(DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir));

        var removed = DirectorCrashJournal.RemoveSession("dir-dead", deadPid, "s1", _dir);

        Assert.True(removed);
        Assert.False(File.Exists(dirty.DirtyFilePath));
        Assert.Empty(DirectorCrashJournal.ListPendingRecoveries(_dir));
    }

    [Fact]
    public void RemoveSession_returns_false_for_unknown_session_or_journal()
    {
        var deadPid = GetDeadPid();
        var deadJournal = NewJournal("dir-dead", pid: deadPid);
        deadJournal.Update(new[] { Session("s1", "here", "/repo/x") });
        DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: _dir);

        // Unknown session in a real journal; double-restore must not fail the second caller.
        Assert.False(DirectorCrashJournal.RemoveSession("dir-dead", deadPid, "ghost-session", _dir));
        // Unknown journal entirely.
        Assert.False(DirectorCrashJournal.RemoveSession("ghost-dir", 1, "s1", _dir));
        // The real session is untouched by the failed attempts.
        var pending = Assert.Single(DirectorCrashJournal.ListPendingRecoveries(_dir));
        Assert.Single(pending.Sessions);
    }

    [Fact]
    public void DetectAndClaim_on_empty_directory_returns_nothing()
    {
        var empty = Path.Combine(_dir, "does-not-exist");
        Assert.Empty(DirectorCrashJournal.DetectAndClaim(currentPid: Environment.ProcessId, directory: empty));
    }

    /// <summary>
    /// A PID that is reliably dead: spin up a trivial process, let it exit, return its id.
    /// Far more robust than guessing an unused number.
    /// </summary>
    private static int GetDeadPid()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit" : "-c exit",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        return p.Id;
    }
}
