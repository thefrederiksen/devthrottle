using CcDirector.Core.Instances;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The launcher starts the installed Director again when it crashes, and never when it ended on purpose.
///
/// Every test drives <see cref="DirectorCrashWatch"/> with a scripted reading of the machine, a counted start and
/// an injected clock, so nothing here starts a real Director, waits on a real timer or depends on what else this
/// machine is running. The crash journal is a real file in a throwaway instance home, because the file being
/// there or not is the fact the watch decides on.
/// </summary>
public sealed class DirectorCrashWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-crash-watch-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _swapLockName = @"Local\cc-director-binary-swap-test-" + Guid.NewGuid().ToString("N");
    private DirectorLookup _machine = NotRunning();
    private DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private int _starts;
    private Func<bool> _start;
    private readonly DirectorStopLedger _ledger = new();
    private readonly HashSet<int> _gonePids = new();

    public DirectorCrashWatchTests()
    {
        _home = Path.Combine(_root, "instances", "default");
        Directory.CreateDirectory(Path.Combine(_home, "config", "director", "crash-journal"));
        _start = () => { _starts++; return true; };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DirectorCrashWatch NewWatch() => new(() => _machine, () => _start(), _ledger, () => _now)
    {
        IsProcessGone = d => _gonePids.Contains(d.Pid),
        SwapLockName = _swapLockName,
    };

    private SupervisedDirector Director(string id, int pid, bool installed = true) =>
        new(id, pid, _home, "2.23.0", _now, installed ? "cc-director.exe" : "cc-director5.exe") { IsInstalledImage = installed };

    private static DirectorLookup NotRunning() => new(DirectorResolution.NotRunning, null, Array.Empty<string>());
    private static DirectorLookup Running(SupervisedDirector d) => new(DirectorResolution.Running, d, new[] { d.DirectorId });

    /// <summary>The Director is running: its journal is on disk and the machine reads it as running.</summary>
    private void IsRunning(SupervisedDirector d)
    {
        File.WriteAllText(DirectorCrashWatch.CrashJournalPath(d), "{\"DirectorId\":\"" + d.DirectorId + "\",\"Sessions\":[]}");
        _machine = Running(d);
    }

    /// <summary>It crashed: the process is gone, the registration no longer resolves, and the journal stays.</summary>
    private void Crashes(SupervisedDirector d)
    {
        _gonePids.Add(d.Pid);
        _machine = NotRunning();
    }

    /// <summary>It ended on purpose: its clean shutdown deleted the journal, then the process left.</summary>
    private void Quits(SupervisedDirector d)
    {
        File.Delete(DirectorCrashWatch.CrashJournalPath(d));
        Crashes(d);
    }

    [Fact]
    public async Task CheckOnceAsync_DirectorCrashed_StartsItAgain()
    {
        // A Director that dies with its crash journal still on disk did not end on purpose, so one is started.
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        Assert.Equal(DirectorCrashWatchOutcome.Watching, await watch.CheckOnceAsync());

        Crashes(a);
        Assert.Equal(DirectorCrashWatchOutcome.Restarted, await watch.CheckOnceAsync());
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_DirectorQuitOnPurpose_IsNotStartedAgain()
    {
        // A quit deletes the crash journal, so the watch leaves the Director closed - now and on every later look.
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        await watch.CheckOnceAsync();

        Quits(a);
        Assert.Equal(DirectorCrashWatchOutcome.EndedOnPurpose, await watch.CheckOnceAsync());
        Assert.Equal(DirectorCrashWatchOutcome.NothingToWatch, await watch.CheckOnceAsync());
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_LauncherStoppedItWithAKill_IsNotStartedAgain()
    {
        // The real supervisor's stop records the Director before stopping it, so a stop that ends in a kill - which
        // leaves the journal behind exactly like a crash - is not mistaken for one. Built on the real supervisor and
        // the watch's public constructor: had the watch tried to start the uninstalled Director, it would throw.
        var a = Director("director-a", int.MaxValue);
        var supervisor = new DirectorSupervisor(new InstallLayout(_root), new DirectorInstanceLocator(_home))
        {
            StopLedger = _ledger,
        };
        supervisor.ReadTheMachine = () => _machine;
        var watch = new DirectorCrashWatch(supervisor) { IsProcessGone = d => _gonePids.Contains(d.Pid), SwapLockName = _swapLockName };

        IsRunning(a);
        await watch.CheckOnceAsync();
        await supervisor.StopAsync();   // pid int.MaxValue does not exist, so the stop sees it gone at once
        Crashes(a);                      // the journal is still there, as after a kill

        Assert.Equal(DirectorCrashWatchOutcome.StoppedByLauncher, await watch.CheckOnceAsync());
        Assert.Equal(DirectorCrashWatchOutcome.NothingToWatch, await watch.CheckOnceAsync());
    }

    [Fact]
    public async Task CheckOnceAsync_RegistrationGoneButProcessStillExiting_DecidesNothingUntilItHasLeft()
    {
        // A clean shutdown can remove the registration before the process has left; the watch waits for the
        // process to be gone before it reads the journal, so a shutdown in progress is never read as a crash.
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        await watch.CheckOnceAsync();

        _machine = NotRunning();   // registration gone, process still here, journal not yet deleted
        Assert.Equal(DirectorCrashWatchOutcome.StillExiting, await watch.CheckOnceAsync());

        Quits(a);
        Assert.Equal(DirectorCrashWatchOutcome.EndedOnPurpose, await watch.CheckOnceAsync());
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_CrashLoop_GivesUpAfterTheLimitUntilADirectorRunsAgain()
    {
        // Five restarts in ten minutes, then nothing more is started; a Director seen running again (a person
        // started one) turns crash restarts back on.
        var watch = NewWatch();
        for (var i = 0; i < DirectorCrashWatch.MaxRestarts; i++)
        {
            var d = Director($"director-{i}", 200 + i);
            IsRunning(d);
            await watch.CheckOnceAsync();
            Crashes(d);
            Assert.Equal(DirectorCrashWatchOutcome.Restarted, await watch.CheckOnceAsync());
            _now = _now.AddSeconds(30);
        }

        var last = Director("director-last", 300);
        IsRunning(last);
        await watch.CheckOnceAsync();
        Crashes(last);
        Assert.Equal(DirectorCrashWatchOutcome.GaveUp, await watch.CheckOnceAsync());
        Assert.Equal(DirectorCrashWatchOutcome.NothingToWatch, await watch.CheckOnceAsync());
        Assert.Equal(DirectorCrashWatch.MaxRestarts, _starts);

        var byHand = Director("director-by-hand", 400);
        IsRunning(byHand);
        Assert.Equal(DirectorCrashWatchOutcome.Watching, await watch.CheckOnceAsync());
        Crashes(byHand);
        Assert.Equal(DirectorCrashWatchOutcome.Restarted, await watch.CheckOnceAsync());
    }

    [Fact]
    public async Task CheckOnceAsync_RestartsOlderThanTheWindow_DoNotCountTowardTheLimit()
    {
        // Crashes spread out over hours are each restarted: only restarts inside the ten-minute window count.
        var watch = NewWatch();
        for (var i = 0; i < DirectorCrashWatch.MaxRestarts + 3; i++)
        {
            var d = Director($"director-{i}", 500 + i);
            IsRunning(d);
            await watch.CheckOnceAsync();
            Crashes(d);
            Assert.Equal(DirectorCrashWatchOutcome.Restarted, await watch.CheckOnceAsync());
            _now = _now + DirectorCrashWatch.RestartWindow + TimeSpan.FromSeconds(1);
        }
        Assert.Equal(DirectorCrashWatch.MaxRestarts + 3, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_SwapLockHeld_WaitsAndStartsOnTheNextLook()
    {
        // An update swapping the Director holds the swap lock; the restart is held off, not dropped, and runs on
        // the next look once the lock is free.
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        await watch.CheckOnceAsync();
        Crashes(a);

        // A mutex belongs to the thread that took it, and an await may resume on another, so the lock is held on
        // a thread of its own for exactly as long as the test needs it.
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var held = new Mutex(initiallyOwned: true, _swapLockName);
            acquired.Set();
            release.Wait();
            held.ReleaseMutex();
        });
        holder.Start();
        acquired.Wait();

        Assert.Equal(DirectorCrashWatchOutcome.RestartHeldOff, await watch.CheckOnceAsync());
        Assert.Equal(0, _starts);
        release.Set();
        holder.Join();

        Assert.Equal(DirectorCrashWatchOutcome.Restarted, await watch.CheckOnceAsync());
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_AfterAHandover_WatchesTheDirectorThatTookOver()
    {
        // An update or Smart Restart replaces the process. The watch follows the instance to the new Director, so a
        // crash after the handover is still restarted - the thing a launch agent or scheduled task cannot do.
        var watch = NewWatch();
        var before = Director("director-before", 101);
        IsRunning(before);
        await watch.CheckOnceAsync();

        File.Delete(DirectorCrashWatch.CrashJournalPath(before));
        var after = Director("director-after", 102);
        IsRunning(after);
        Assert.Equal(DirectorCrashWatchOutcome.Watching, await watch.CheckOnceAsync());
        Assert.Equal("director-after", watch.Watched?.DirectorId);

        Crashes(after);
        Assert.Equal(DirectorCrashWatchOutcome.Restarted, await watch.CheckOnceAsync());
    }

    [Fact]
    public async Task CheckOnceAsync_DevelopmentBuildHoldsTheInstance_IsNotWatched()
    {
        // A Director that is not the installed image is not the launcher's to bring back, so its crash starts nothing.
        var watch = NewWatch();
        var dev = Director("director-dev", 101, installed: false);
        IsRunning(dev);
        Assert.Equal(DirectorCrashWatchOutcome.NothingToWatch, await watch.CheckOnceAsync());

        Crashes(dev);
        Assert.Equal(DirectorCrashWatchOutcome.NothingToWatch, await watch.CheckOnceAsync());
        Assert.Equal(0, _starts);
    }

    [Theory]
    [InlineData(DirectorResolution.Ambiguous)]
    [InlineData(DirectorResolution.Unknown)]
    public async Task CheckOnceAsync_MachineCannotBeReadToOneAnswer_StartsNothing(DirectorResolution outcome)
    {
        // Several claimants, or one that will not read, is a machine nobody understands; nothing is started on it.
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        await watch.CheckOnceAsync();

        _gonePids.Add(a.Pid);
        _machine = new DirectorLookup(outcome, null, Array.Empty<string>());
        Assert.Equal(DirectorCrashWatchOutcome.Undecided, await watch.CheckOnceAsync());
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_StartThrows_IsTriedAgainAndCountsTowardTheLimit()
    {
        // A start that fails is not lost - it is tried on the next look - and each try counts, so a Director that
        // cannot be started at all ends in a give-up instead of a try every fifteen seconds for ever.
        _start = () => { _starts++; throw new FileNotFoundException("Installed Director not found"); };
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        await watch.CheckOnceAsync();
        Crashes(a);

        for (var i = 0; i < DirectorCrashWatch.MaxRestarts; i++)
            await Assert.ThrowsAsync<FileNotFoundException>(() => watch.CheckOnceAsync());

        Assert.Equal(DirectorCrashWatchOutcome.GaveUp, await watch.CheckOnceAsync());
        Assert.Equal(DirectorCrashWatch.MaxRestarts, _starts);
    }

    [Fact]
    public async Task CheckOnceAsync_ADirectorAlreadyRunningAgainWhenTheStartRuns_IsReportedAsSuch()
    {
        // The supervisor's start skips when something holds the instance; that is reported, not called a restart.
        _start = () => { _starts++; return false; };
        var watch = NewWatch();
        var a = Director("director-a", 101);
        IsRunning(a);
        await watch.CheckOnceAsync();
        Crashes(a);

        Assert.Equal(DirectorCrashWatchOutcome.AlreadyRunningAgain, await watch.CheckOnceAsync());
        Assert.Equal(DirectorCrashWatchOutcome.NothingToWatch, await watch.CheckOnceAsync());
    }

    [Fact]
    public async Task StopAsync_RecordsTheDirectorInTheLedgerBeforeStoppingIt()
    {
        // The supervisor writes the stop down whatever way the stop ends, and the record answers once only, so it
        // cannot excuse a later crash of a process that reused the id.
        var a = Director("director-a", int.MaxValue);
        var supervisor = new DirectorSupervisor(new InstallLayout(_root), new DirectorInstanceLocator(_home)) { StopLedger = _ledger };
        supervisor.ReadTheMachine = () => Running(a);

        await supervisor.StopAsync();

        Assert.True(_ledger.Consume("director-a", int.MaxValue));
        Assert.False(_ledger.Consume("director-a", int.MaxValue));
    }
}
