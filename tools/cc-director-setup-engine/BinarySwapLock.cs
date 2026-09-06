using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>
/// THE MACHINE-WIDE LOCK THAT KEEPS TWO BINARY SWAPS APART.
///
/// This install has two update owners, and each one stops the other's process. The launcher owns the
/// Director's update (<c>DirectorUpdateOwner</c>): it stops the Director, swaps its binary, starts it
/// and waits for the new version to answer. The Director owns the launcher's update
/// (<see cref="LauncherUpdateOwner"/>): it stops the launcher, swaps ITS binary, starts it and waits
/// for the new build to be witnessed. Each is correct alone, and they were uncoordinated.
///
/// Overlapped, they destroy each other's evidence and can leave the machine on a half-installed pair.
/// The Director begins swapping the launcher; the launcher, in the same seconds, decides the Director
/// is idle and stops it - killing the process that has already renamed the launcher binary aside, with
/// nothing left running to put it back or to start what was installed. The mirror is no better: the
/// Director stops the launcher mid-Director-swap, and the swap's own witness dies with it, so a
/// perfectly good build is rolled back and pinned for a reason that has nothing to do with the build.
///
/// The same lock also keeps two DIRECTORS sharing one install from swapping the one launcher at once.
///
/// It is a machine-wide named mutex, taken WITHOUT WAITING - the same shape and the same reasoning as
/// <see cref="ToolReconciler.HeavyRepairMutexName"/>. Never blocking is the point: both owners run on
/// periodic loops, so a pass that cannot have the lock has nothing to gain by queueing behind one. It
/// says why it did nothing and looks again on its next cycle, by which time the other swap has either
/// finished or failed and said so.
///
/// WHAT THIS DOES NOT COVER, AND CANNOT. A launcher old enough to predate this code takes no lock, so
/// the exclusion only holds once both sides are running a build that has it. That is inherent to
/// shipping a lock into a pair of processes that update each other - and it is the ordinary case for
/// the very machines this exists for, whose launcher is the stale one. The Director's side is still
/// worth taking on its own: it makes two Directors safe immediately, and it makes the pair safe from
/// the first launcher that carries it.
/// </summary>
public static class BinarySwapLock
{
    /// <summary>
    /// The one name both owners take. Global so it spans logon sessions on a machine where a Director
    /// and a launcher may not share one; the same convention as the tool reconcile's mutex.
    /// </summary>
    public const string Name = @"Global\cc-director-binary-swap";

    /// <summary>
    /// Run <paramref name="work"/> while holding the lock, or return <paramref name="whenBusy"/>'s
    /// answer without running it at all when another swap holds it.
    ///
    /// A WINDOWS NAMED MUTEX HAS THREAD AFFINITY: only the thread that took it may release it. The
    /// guarded work is asynchronous and an await resumes on whatever thread the runtime picks, so
    /// taking the mutex, awaiting, and releasing inline released it from a different thread and threw
    /// "Object synchronization method was called from an unsynchronized block of code" - which the
    /// caller's catch then reported as a FAILED update of a swap that had actually succeeded
    /// (issue #1045, in the tool reconcile). So the whole critical section runs start to finish on one
    /// dedicated thread, exactly as <see cref="ToolReconciler"/> does it.
    /// </summary>
    /// <param name="work">The swap. Runs only when the lock was taken.</param>
    /// <param name="whenBusy">The answer to give when another swap is already running.</param>
    /// <param name="who">A short label for the log, so it says which owner was held off.</param>
    /// <param name="name">
    /// The mutex to take. Defaults to <see cref="Name"/>, which is the whole point - both owners take
    /// the SAME one or the lock excludes nothing.
    ///
    /// It is a parameter only so that a test can hold a lock of its own and prove the held-off path
    /// without stopping every other process on the machine that is legitimately swapping a binary -
    /// and, more practically, without two test projects running side by side in one gate contending
    /// for a real machine-wide lock and failing each other for a reason that is not in either branch.
    /// Production callers never pass it; that both of them do not is asserted by
    /// <c>BothUpdateOwnersTakeTheSameLockTests</c>, which is the fact a test seam here could otherwise
    /// quietly cost.
    /// </param>
    public static Task<T> RunExclusivelyAsync<T>(
        Func<Task<T>> work, Func<string, T> whenBusy, string who, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(whenBusy);
        var mutexName = string.IsNullOrWhiteSpace(name) ? Name : name;

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Background, so a wedged swap can never keep the process alive; created per call, so it holds
        // no other lock and cannot deadlock against anything.
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            var held = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, mutexName, out _);
                try { held = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { held = true; }   // a prior holder died; we own it now

                if (!held)
                {
                    var message = "another binary swap is already running on this machine "
                                  + "(a Director update or a launcher update); nothing was touched.";
                    FileLog.Write($"[BinarySwapLock] {who} held off: {message}");
                    completion.SetResult(whenBusy(message));
                    return;
                }

                // Blocking is correct on this thread: it exists only to own the mutex.
                completion.SetResult(work().GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                if (held)
                {
                    try { mutex!.ReleaseMutex(); }
                    catch (Exception ex) { FileLog.Write($"[BinarySwapLock] releasing FAILED: {ex.Message}"); }
                }
                mutex?.Dispose();
            }
        })
        { IsBackground = true, Name = $"binary-swap-lock-{who}" };

        thread.Start();
        return completion.Task;
    }
}
