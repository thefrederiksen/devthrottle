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
    /// The lock name for a data folder. The folder is made absolute, symbolic links in it are resolved, and any
    /// trailing separator is dropped, so two paths to one folder share one lock; the slot hash also ignores case
    /// and slash style, which on a case-sensitive file system can only make two folders share a lock, never split one.
    ///
    /// GLOBAL ON EVERY PLATFORM, AND THAT IS LOAD-BEARING. A <c>Local\</c> name is visible only inside one
    /// session. On macOS and Linux that is a login session: a Director started from the desktop and one started
    /// over SSH, or with setsid, never see each other's <c>Local\</c> lock - proved on devlinux, 2026-10-17, where
    /// two Directors in two sessions both "acquired" the same <c>Local\</c> name on one folder. On Windows it is a
    /// desktop session: a Remote Desktop sign-in, or a scheduled task in session 0, would miss the console
    /// Director's lock the same way. A <c>Global\</c> name is seen by every session, and an ordinary user may
    /// create a global mutex on all three platforms.
    /// </summary>
    public static string HomeMutexNameFor(string instanceHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceHome);
        var normalized = WithLinksResolved(Path.GetFullPath(instanceHome))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $@"Global\cc-director-home-{DirectorIdentitySlot.SlotFor(normalized)}";
    }

    /// <summary>
    /// <paramref name="fullPath"/> with every symbolic link in it resolved, one segment at a time, so a link
    /// anywhere in the path - not only the last part - leads to the same lock as the real folder (on macOS
    /// <c>/tmp</c> is a link to <c>/private/tmp</c>). Segments that do not exist yet are kept as written.
    /// </summary>
    internal static string WithLinksResolved(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var resolved = root;
        var segments = fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = Path.Combine(resolved, segment);
            var target = Directory.Exists(next) ? new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true) : null;
            resolved = target is null ? next : Path.GetFullPath(target.FullName);
        }
        return resolved;
    }

    /// <summary>
    /// Attempt to claim both locks: the program file's first, then the data folder's.
    /// Returns a guard if both were claimed; null if either is held, with <paramref name="heldBy"/> saying which.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(out GuardHeldBy heldBy)
        => TryAcquire(MutexNameForCurrentProcess(), HomeMutexNameForCurrentProcess(), out heldBy);

    /// <summary>
    /// <see cref="TryAcquire(out GuardHeldBy)"/> for given lock names, so the ordering and the release can be
    /// tested with names of the test's own. If claiming the data folder's lock THROWS - an unreadable shared
    /// lock file, say - the program lock already taken is released before the exception leaves, so a refused
    /// start never holds a lock it does not use.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string programName, string homeName, out GuardHeldBy heldBy)
    {
        var program = TryClaim(programName);
        if (program is null)
        {
            FileLog.Write($"[SingleInstanceGuard] Another instance holds {programName}");
            heldBy = GuardHeldBy.SameProgram;
            return null;
        }

        Mutex? home;
        try
        {
            home = TryClaim(homeName);
        }
        catch
        {
            Release(program);
            throw;
        }
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
