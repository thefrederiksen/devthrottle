using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>Which lock a refused start found already held.</summary>
public enum GuardHeldBy
{
    /// <summary>Nothing: the guard was acquired.</summary>
    None,

    /// <summary>Another process of the same Director program file is running.</summary>
    SameProgram,

    /// <summary>Another Director - any program file - is running on the same data folder.</summary>
    SameHome,
}

/// <summary>
/// Refuses to let two Directors run at once on the same program file OR on the same data folder.
///
/// Identity collision is silent and lossy: both processes claim the same DirectorId
/// (per <see cref="DirectorIdStore"/>), fight over <c>instances/&lt;id&gt;.json</c>,
/// overwrite each other's <c>ports/&lt;id&gt;.port</c>, and concurrently mutate the
/// shared per-Director state tree. The guard converts that into an explicit "already
/// running" exit at startup.
///
/// TWO LOCKS, BECAUSE ONE WAS NOT ENOUGH (issue devthrottle_internal#2489). The first lock is keyed by the
/// program file's slot (same scheme as DirectorIdStore), so cc-director1.exe and cc-director2.exe each
/// have their own. That alone let two DIFFERENT program files run on ONE data folder: on devlinux,
/// 2026-10-17, a worktree build ran on the live default instance home beside the installed Director and
/// cleared its sessions.json, because every Director clears that file at start-up. So the second lock is
/// keyed by the instance home itself. Program files still coexist - each in a data folder of its own,
/// which is what <c>--instance</c> and a cloned instance give a test Director.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _programMutex;
    private readonly Mutex _homeMutex;

    private SingleInstanceGuard(Mutex programMutex, Mutex homeMutex)
    {
        _programMutex = programMutex;
        _homeMutex = homeMutex;
    }

    /// <summary>The lock for this process's program file and instance.</summary>
    public static string MutexNameForCurrentProcess()
        => $@"Local\cc-director-instance-{DirectorIdStore.SlotFor(DirectorIdStore.CurrentProcessSlotKey())}";

    /// <summary>The lock for this process's data folder.</summary>
    public static string HomeMutexNameForCurrentProcess() => HomeMutexNameFor(InstanceContext.InstanceHome);

    /// <summary>
    /// The lock name for a data folder. The folder is made absolute and loses any trailing separator, so two
    /// spellings of one folder share one lock; the slot hash also ignores case and slash style, which on a
    /// case-sensitive file system can only make two folders share a lock, never split one.
    ///
    /// GLOBAL ON MACOS AND LINUX, AND THAT IS LOAD-BEARING. There a <c>Local\</c> name is visible only inside one
    /// login session: a Director started from the desktop and one started over SSH, or with setsid, never see
    /// each other's <c>Local\</c> lock. Proved on devlinux, 2026-10-17: two Directors in two sessions both
    /// "acquired" the same <c>Local\</c> name on one folder. A <c>Global\</c> name is seen by every session on the
    /// machine. Windows keeps <c>Local\</c>, which is per logon there and covers every Director a user starts.
    /// </summary>
    public static string HomeMutexNameFor(string instanceHome) => HomeMutexNameFor(instanceHome, OperatingSystem.IsWindows());

    /// <summary><see cref="HomeMutexNameFor(string)"/> for a given platform. Pure, for tests.</summary>
    public static string HomeMutexNameFor(string instanceHome, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceHome);
        var normalized = Path.GetFullPath(instanceHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scope = isWindows ? "Local" : "Global";
        return $@"{scope}\cc-director-home-{DirectorIdentitySlot.SlotFor(normalized)}";
    }

    /// <summary>
    /// Attempt to claim both locks: the program file's first, then the data folder's.
    /// Returns a guard if both were claimed; null if either is held, with <paramref name="heldBy"/> saying which.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(out GuardHeldBy heldBy)
    {
        var programName = MutexNameForCurrentProcess();
        var program = TryClaim(programName);
        if (program is null)
        {
            FileLog.Write($"[SingleInstanceGuard] Another instance holds {programName}");
            heldBy = GuardHeldBy.SameProgram;
            return null;
        }

        var homeName = HomeMutexNameForCurrentProcess();
        var home = TryClaim(homeName);
        if (home is null)
        {
            Release(program);
            FileLog.Write($"[SingleInstanceGuard] Another Director holds {homeName}, the data folder {InstanceContext.InstanceHome}");
            heldBy = GuardHeldBy.SameHome;
            return null;
        }

        FileLog.Write($"[SingleInstanceGuard] Acquired {programName} and {homeName} ({InstanceContext.InstanceHome})");
        heldBy = GuardHeldBy.None;
        return new SingleInstanceGuard(program, home);
    }

    /// <summary>Claim a named lock without waiting; null when another process holds it.</summary>
    private static Mutex? TryClaim(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out _);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Prior holder crashed without releasing. The kernel hands us the mutex anyway.
            // not-an-error: evidence of an earlier crash, which that crash's own report covers; claiming the mutex is the recovery
            FileLog.Write($"[SingleInstanceGuard] AbandonedMutex on {name} -- prior holder crashed, claiming");
            acquired = true;
        }

        if (acquired)
            return mutex;
        mutex.Dispose();
        return null;
    }

    private static void Release(Mutex mutex)
    {
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }

    public void Dispose()
    {
        Release(_homeMutex);
        Release(_programMutex);
    }
}
