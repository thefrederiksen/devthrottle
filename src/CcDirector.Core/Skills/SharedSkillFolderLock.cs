using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// ONE critical section per shared skill folder, held across the whole of a reconciliation
/// (devthrottle_internal#2311, review finding SK-F1).
///
/// WHY. Every Director on the computer reconciles the same per-user <c>~/.agents/skills</c> and
/// <c>~/.claude/skills</c>. Deciding who owns a folder and then deleting or replacing it are two steps; without
/// a lock around both, one Director can read its own stamp, another can replace and re-stamp the folder in
/// between, and the first then deletes a folder its source did not install - F7 again, by timing instead of by
/// rule. Sequential tests cannot see it. So the read of ownership and the change it licenses happen under one
/// lock that every Director takes.
///
/// THE STORE TOO (review finding SK-F7). The same kind of lock, on a Director's own materialized store, is shared
/// by the store refresh, which deletes and rebuilds it, and placement, which reads each skill's recorded source and
/// copies its bytes - so the two can never interleave. The order is fixed: placement takes the shared folders'
/// locks first and the store's second; the refresh takes only the store's.
///
/// HOW. A named operating-system mutex per folder, its name derived from the folder's full path, so every
/// process of every Director on the machine meets on the same object, and two different folders never wait on
/// each other. On Windows it lives in the <c>Global\</c> namespace, because a Director started by the Task
/// Scheduler runs in a different logon session from one started by hand, and a session-local name would give
/// them two different locks. Several folders are always taken in one fixed order, so two Directors can never
/// each hold one and wait for the other.
///
/// A WAIT IS BOUNDED. A reconciliation takes well under a second; one that cannot get the lock in the wait
/// given does NOTHING to the folders and the caller records why. It never proceeds unlocked. A lock left by a
/// process that died holding it is taken over and said so in the log: the reconciliation that follows rebuilds
/// every folder it owns from the store, which is the repair.
/// </summary>
public sealed class SharedSkillFolderLock : IDisposable
{
    private readonly List<Mutex> _held;

    private SharedSkillFolderLock(List<Mutex> held) => _held = held;

    /// <summary>
    /// Take the lock for every folder in <paramref name="folders"/>, waiting at most <paramref name="wait"/> in
    /// all. Null when it could not be had in time - nothing is held then.
    /// </summary>
    public static SharedSkillFolderLock? TryAcquire(IEnumerable<string> folders, TimeSpan wait)
    {
        var names = folders.Select(NameFor).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var deadline = DateTime.UtcNow + wait;
        var held = new List<Mutex>();
        foreach (var name in names)
        {
            var mutex = new Mutex(initiallyOwned: false, name);
            var remaining = deadline - DateTime.UtcNow;
            bool got;
            try
            {
                got = mutex.WaitOne(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // not-an-error: evidence of an earlier crash, which that crash's own report covers; taking the lock over is the recovery
                FileLog.Write($"[SharedSkillFolderLock] {name} was left held by a process that ended mid-reconcile; " +
                              "taken over - this reconciliation rebuilds what it owns");
                got = true;
            }
            if (!got)
            {
                mutex.Dispose();
                Release(held);
                FileLog.Write($"[SharedSkillFolderLock] TryAcquire: {name} not free within {wait.TotalSeconds:0.#}s");
                return null;
            }
            held.Add(mutex);
        }
        return new SharedSkillFolderLock(held);
    }

    /// <summary>
    /// The lock's name for one folder: the same for every spelling of the same path, different for every other
    /// folder. A hash, because a mutex name cannot contain a path's separators.
    /// </summary>
    public static string NameFor(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            full = full.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
        return (OperatingSystem.IsWindows() ? @"Global\" : "") + "devthrottle-skill-folder-" + hash;
    }

    public void Dispose() => Release(_held);

    private static void Release(List<Mutex> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
        {
            held[i].ReleaseMutex();
            held[i].Dispose();
        }
        held.Clear();
    }
}
