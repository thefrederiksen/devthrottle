using CcDirector.Core.Scheduler;
using Xunit;

namespace CcDirector.Core.Tests.Scheduler;

public sealed class ReloadRunnersTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fakeDbPath;
    private readonly string _statePath;

    public ReloadRunnersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ReloadRunnersTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fakeDbPath = Path.Combine(_tempDir, "no-db.sqlite");
        _statePath = Path.Combine(_tempDir, "state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static RunnerRegistration Make(string name) => new()
    {
        Name = name,
        QueueFilter = "1=1",
        Command = "dotnet",
        Args = new[] { "--version" },
        Schedule = Cron.EveryMinutes(10),
    };

    [Fact]
    public void Reload_PreservesStateForKeptRunners()
    {
        var s = new CommQueueScheduler(_fakeDbPath, _statePath);
        s.RegisterRunner(Make("a"));
        s.RegisterRunner(Make("b"));

        // Fire 'a' so it gets a real LastFiredAt. Generous wait: the fire runs a real
        // `dotnet --version` subprocess, which can take several seconds on a loaded machine.
        s.RunNow("a");
        Assert.True(WaitFor(() => !s.GetSnapshot().Single(r => r.Name == "a").IsFiring, TimeSpan.FromSeconds(15)));
        var beforeReload = s.GetSnapshot().Single(r => r.Name == "a").LastFiredAtUtc;
        Assert.NotEqual(DateTime.MinValue, beforeReload);

        // Reload with the same set of runners.
        s.ReloadRunners(new[] { Make("a"), Make("b") });

        var afterReload = s.GetSnapshot().Single(r => r.Name == "a").LastFiredAtUtc;
        Assert.Equal(beforeReload, afterReload);
    }

    [Fact]
    public void Reload_NewRunnerStartsWithFreshState()
    {
        var s = new CommQueueScheduler(_fakeDbPath, _statePath);
        s.RegisterRunner(Make("existing"));
        s.RunNow("existing");
        Assert.True(WaitFor(() => !s.GetSnapshot()[0].IsFiring, TimeSpan.FromSeconds(15)));

        s.ReloadRunners(new[] { Make("existing"), Make("brand-new") });

        var snap = s.GetSnapshot();
        Assert.Equal(2, snap.Count);
        var fresh = snap.Single(r => r.Name == "brand-new");
        Assert.Equal(DateTime.MinValue, fresh.LastFiredAtUtc);
        Assert.False(fresh.IsFiring);
    }

    [Fact]
    public void Reload_RemovedRunnerDropsFromSnapshot()
    {
        var s = new CommQueueScheduler(_fakeDbPath, _statePath);
        s.RegisterRunner(Make("a"));
        s.RegisterRunner(Make("doomed"));

        s.ReloadRunners(new[] { Make("a") });

        var snap = s.GetSnapshot();
        Assert.Single(snap);
        Assert.Equal("a", snap[0].Name);
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
