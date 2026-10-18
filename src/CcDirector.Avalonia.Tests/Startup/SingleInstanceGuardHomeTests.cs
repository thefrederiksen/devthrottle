using CcDirector.ControlApi;
using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Avalonia.Tests.Startup;

/// <summary>
/// The data-folder lock of <see cref="SingleInstanceGuard"/> (issue devthrottle_internal#2489). The defect:
/// the only lock was keyed on the program file, so a second program file - a worktree build - ran on the
/// live default instance home and cleared its sessions.json.
///
/// The ordering tests use real named locks with names of their own. A named lock belongs to the thread that
/// took it, so the "other Director" is a second thread holding it; nothing here waits on a clock.
/// </summary>
public sealed class SingleInstanceGuardHomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guard-home-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _defaultHome;

    public SingleInstanceGuardHomeTests()
    {
        _defaultHome = InstanceContext.InstanceHomeOf(_root, "default");
        Directory.CreateDirectory(_defaultHome);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string UniqueName(string kind) => $@"Global\cc-director-test-{kind}-{Guid.NewGuid():N}";

    /// <summary>Run <paramref name="act"/> on a thread of its own and wait for it - one more Director.</summary>
    private static T OnOtherThread<T>(Func<T> act)
    {
        T result = default!;
        var thread = new Thread(() => result = act());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void TryAcquire_HomeHeldByAnotherProgram_IsRefusedAsSameHome()
    {
        // Arrange: Director A, with its own program lock, holds the folder lock.
        var home = UniqueName("home");
        using var holderReady = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var a = SingleInstanceGuard.TryAcquire(UniqueName("program-a"), home, out _);
            holderReady.Set();
            release.Wait();
        });
        holder.Start();
        holderReady.Wait();

        // Act: Director B - a DIFFERENT program file - tries the same folder.
        var programB = UniqueName("program-b");
        var (guardB, heldBy) = OnOtherThread(() =>
        {
            var g = SingleInstanceGuard.TryAcquire(programB, home, out var h);
            return (g, h);
        });

        // Assert
        Assert.Null(guardB);
        Assert.Equal(GuardHeldBy.SameHome, heldBy);

        // And B let go of its program lock on the way out: another claim of it succeeds.
        var programFree = OnOtherThread(() =>
        {
            using var again = SingleInstanceGuard.TryAcquire(programB, UniqueName("home-other"), out var h);
            return again is not null && h == GuardHeldBy.None;
        });
        Assert.True(programFree);

        release.Set();
        holder.Join();
    }

    [Fact]
    public void TryAcquire_ProgramHeld_IsRefusedAsSameProgram_BeforeTheHomeIsTried()
    {
        var program = UniqueName("program");
        var home = UniqueName("home");
        using var first = SingleInstanceGuard.TryAcquire(program, UniqueName("home-first"), out _);

        var (second, heldBy) = OnOtherThread(() =>
        {
            var g = SingleInstanceGuard.TryAcquire(program, home, out var h);
            return (g, h);
        });

        Assert.Null(second);
        Assert.Equal(GuardHeldBy.SameProgram, heldBy);
        // The home lock was never taken, so it is free.
        var homeFree = OnOtherThread(() =>
        {
            using var g = SingleInstanceGuard.TryAcquire(UniqueName("program-x"), home, out _);
            return g is not null;
        });
        Assert.True(homeFree);
    }

    [Fact]
    public void Dispose_ReleasesBothLocks()
    {
        var program = UniqueName("program");
        var home = UniqueName("home");
        OnOtherThread(() =>
        {
            using var g = SingleInstanceGuard.TryAcquire(program, home, out _);
            return 0;
        });

        var again = OnOtherThread(() =>
        {
            using var g = SingleInstanceGuard.TryAcquire(program, home, out var h);
            return g is not null && h == GuardHeldBy.None;
        });

        Assert.True(again);
    }

    [Fact]
    public void HomeMutexNameFor_IsGlobalSoEverySessionSeesIt()
    {
        // A Local name is per session: a desktop start and an SSH or Remote Desktop start would each get it.
        Assert.StartsWith(@"Global\cc-director-home-", SingleInstanceGuard.HomeMutexNameFor(_defaultHome));
    }

    [Fact]
    public void HomeMutexNameFor_DifferentInstances_HaveDifferentLocks()
    {
        var linuxtest = InstanceContext.InstanceHomeOf(_root, "linuxtest");

        Assert.NotEqual(SingleInstanceGuard.HomeMutexNameFor(_defaultHome), SingleInstanceGuard.HomeMutexNameFor(linuxtest));
    }

    [Fact]
    public void HomeMutexNameFor_TrailingSeparatorAndRoundaboutSpelling_AreTheSameFolder()
    {
        var withSeparator = _defaultHome + Path.DirectorySeparatorChar;
        var roundabout = Path.Combine(_defaultHome, "..", "default");

        Assert.Equal(SingleInstanceGuard.HomeMutexNameFor(_defaultHome), SingleInstanceGuard.HomeMutexNameFor(withSeparator));
        Assert.Equal(SingleInstanceGuard.HomeMutexNameFor(_defaultHome), SingleInstanceGuard.HomeMutexNameFor(roundabout));
    }

    [Fact]
    public void HomeMutexNameFor_ThroughASymbolicLink_IsTheSameFolder()
    {
        if (OperatingSystem.IsWindows()) return; // creating a link there needs a privilege the test cannot assume

        var link = Path.Combine(_root, "link-to-instances");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "instances"));

        Assert.Equal(
            SingleInstanceGuard.HomeMutexNameFor(_defaultHome),
            SingleInstanceGuard.HomeMutexNameFor(Path.Combine(link, "default")));
    }

    [Fact]
    public void HomeMutexNameFor_IsDistinctFromTheProgramLock()
    {
        Assert.DoesNotContain("cc-director-instance-", SingleInstanceGuard.HomeMutexNameFor(_defaultHome));
    }

    [Fact]
    public void HomeMutexNameFor_EmptyHome_Throws()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.HomeMutexNameFor(" "));
    }
}
