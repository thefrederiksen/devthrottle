using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Refuses to let two processes of the same Director exe path run at once.
///
/// Identity collision is silent and lossy: both processes claim the same DirectorId
/// (per <see cref="DirectorIdStore"/>), fight over <c>instances/&lt;id&gt;.json</c>,
/// overwrite each other's <c>ports/&lt;id&gt;.port</c>, and concurrently mutate the
/// shared per-Director state tree. The guard converts that into an explicit "already
/// running" exit at startup.
///
/// The mutex name is keyed by the exe path slot (same scheme as DirectorIdStore), so
/// cc-director1.exe and cc-director2.exe each have their own guard
/// and can coexist.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownedByUs;

    private SingleInstanceGuard(Mutex mutex, bool ownedByUs)
    {
        _mutex = mutex;
        _ownedByUs = ownedByUs;
    }

    public static string MutexNameForCurrentProcess()
        => $@"Local\cc-director-instance-{DirectorIdStore.SlotFor(DirectorIdStore.CurrentProcessSlotKey())}";

    /// <summary>
    /// Attempt to claim the single-instance mutex for this exe path.
    /// Returns a guard if successful; null if another process already holds it.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var name = MutexNameForCurrentProcess();
        var mutex = new Mutex(initiallyOwned: false, name, out _);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Prior holder crashed without releasing. The kernel hands us the mutex anyway.
            FileLog.Write($"[SingleInstanceGuard] AbandonedMutex on {name} -- prior holder crashed, claiming");
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            FileLog.Write($"[SingleInstanceGuard] Another instance holds {name}");
            return null;
        }

        FileLog.Write($"[SingleInstanceGuard] Acquired {name}");
        return new SingleInstanceGuard(mutex, ownedByUs: true);
    }

    public void Dispose()
    {
        if (_ownedByUs)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
