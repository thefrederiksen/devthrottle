using CcDirector.Core.Scheduler;
using Xunit;

namespace CcDirector.Core.Tests.Scheduler;

/// <summary>
/// Round-trips for the scheduler-state.json persistence layer. Each test uses
/// a unique temp dir so they can run in parallel without colliding on the
/// state file. The DB path is set to a non-existent file so the queue read
/// short-circuits (tests don't need a real comm-queue DB).
/// </summary>
public sealed class SchedulerStatePersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;
    private readonly string _fakeDbPath;

    public SchedulerStatePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SchedStateTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "scheduler-state.json");
        _fakeDbPath = Path.Combine(_tempDir, "no-such-db.sqlite");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private CommQueueScheduler NewScheduler() => new(_fakeDbPath, _statePath);

    // Use `dotnet --version` for the test subprocess: it is guaranteed to be
    // on PATH (we are running under dotnet test), exits within ~500ms with
    // code 0, and works on Windows/Linux/macOS. `echo` is a shell builtin on
    // Windows, not an executable, so Process.Start("echo") fails or hangs.
    private static RunnerRegistration MakeRunner(string name) => new()
    {
        Name = name,
        QueueFilter = "1=1",
        Command = "dotnet",
        Args = new[] { "--version" },
        Schedule = Cron.EveryMinutes(10),
    };

    [Fact]
    public void Load_WithNoFile_DoesNothing()
    {
        var s = NewScheduler();
        s.RegisterRunner(MakeRunner("a"));

        s.LoadPersistedState(); // file doesn't exist -- should not throw or modify state

        var snap = s.GetSnapshot();
        Assert.Equal(DateTime.MinValue, snap[0].LastFiredAtUtc);
    }

    [Fact]
    public void Load_AfterRunNow_RestoresLastFiredAt()
    {
        // First scheduler: fire the runner, which writes state to disk.
        var first = NewScheduler();
        first.RegisterRunner(MakeRunner("a"));
        var result = first.RunNow("a");
        Assert.True(result.Started);

        // Wait for the background fire to complete and write state to disk.
        Assert.True(WaitFor(() =>
        {
            var snap = first.GetSnapshot();
            return !snap[0].IsFiring && snap[0].LastFiredAtUtc != DateTime.MinValue;
        }, TimeSpan.FromSeconds(10)), "First scheduler should finish fire and persist state");

        // MarkFireCompleted clears IsFiring inside the gate lock but persists to disk
        // AFTER releasing it (deliberately - no disk I/O under the lock), so the
        // in-memory predicate above can become true before the file lands. Wait for
        // the file separately instead of asserting its existence instantly.
        Assert.True(WaitFor(() => File.Exists(_statePath), TimeSpan.FromSeconds(10)),
            "scheduler-state.json should be persisted shortly after the fire completes");
        var persistedAt = File.GetLastWriteTimeUtc(_statePath);

        // Second scheduler: load state and verify LastFiredAt was restored.
        var second = NewScheduler();
        second.RegisterRunner(MakeRunner("a"));
        second.LoadPersistedState();

        var snap2 = second.GetSnapshot();
        Assert.NotEqual(DateTime.MinValue, snap2[0].LastFiredAtUtc);
        // Sanity: the restored time should be within a few seconds of when the file was written.
        Assert.True(Math.Abs((snap2[0].LastFiredAtUtc - persistedAt).TotalSeconds) < 5);
    }

    [Fact]
    public void Load_DoesNotOverwriteNonMinValueInMemoryState()
    {
        // Pre-populate the state file with a "fired at 2026-01-01" record.
        File.WriteAllText(_statePath, """
            {
              "runners": [
                { "name": "a", "lastFiredAtUtc": "2026-01-01T08:00:00Z" }
              ]
            }
            """);

        var s = NewScheduler();
        s.RegisterRunner(MakeRunner("a"));

        // Fire once to set in-memory state to "now".
        s.RunNow("a");
        Assert.True(WaitFor(() => !s.GetSnapshot()[0].IsFiring, TimeSpan.FromSeconds(15)));
        var inMemoryAfterFire = s.GetSnapshot()[0].LastFiredAtUtc;

        // Loading should NOT overwrite our newer in-memory state with the older disk value.
        s.LoadPersistedState();

        var after = s.GetSnapshot()[0].LastFiredAtUtc;
        Assert.Equal(inMemoryAfterFire, after);
    }

    [Fact]
    public void Load_MalformedJson_DoesNotThrow()
    {
        File.WriteAllText(_statePath, "{ not valid json");
        var s = NewScheduler();
        s.RegisterRunner(MakeRunner("a"));

        var ex = Record.Exception(() => s.LoadPersistedState());
        Assert.Null(ex);
        Assert.Equal(DateTime.MinValue, s.GetSnapshot()[0].LastFiredAtUtc);
    }

    [Fact]
    public void Load_IgnoresEntriesForUnregisteredRunners()
    {
        File.WriteAllText(_statePath, """
            {
              "runners": [
                { "name": "ghost", "lastFiredAtUtc": "2026-01-01T08:00:00Z" }
              ]
            }
            """);
        var s = NewScheduler();
        s.RegisterRunner(MakeRunner("real"));

        s.LoadPersistedState();

        Assert.Equal(DateTime.MinValue, s.GetSnapshot()[0].LastFiredAtUtc);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
