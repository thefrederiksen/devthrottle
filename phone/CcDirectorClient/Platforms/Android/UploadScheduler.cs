using Android.Content;
using AndroidX.Work;
using Java.Util.Concurrent;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Schedules the background upload queue via Android WorkManager so recordings
/// upload even when the app is fully swiped-closed, and resume after a reboot.
/// WorkManager persists the queue and applies backoff on failure.
/// </summary>
public static class UploadScheduler
{
    private const string OneTimeName = "cc-upload-now";
    private const string PeriodicName = "cc-upload-periodic";

    // Deliberately NOT NetworkType.Connected. The Gateway is reached over
    // Tailscale, which Android can classify as a "constrained"/"local" network
    // (or VPN-only with no public internet) that WorkManager does NOT count as
    // Connected - so a Connected constraint can block the worker from ever
    // starting even though the Gateway is reachable. We require no network type
    // and let the in-app pass govern reachability instead: it skips only when
    // there is no radio at all (NetworkAccess == None) and falls each failed
    // attempt back to Retry. This is the same Tailscale-aware logic the
    // foreground path already uses (issue #687).
    private static Constraints NetworkConstraints()
        => new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.NotRequired!)
            .Build();

    /// <summary>Kick an upload pass as soon as the network allows (called on stop).</summary>
    public static void EnqueueNow(Context ctx)
    {
        var req = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(UploadWorker)))
            .SetConstraints(NetworkConstraints())
            .SetBackoffCriteria(BackoffPolicy.Linear!, 30000, TimeUnit.Milliseconds!)
            .Build();
        // AppendOrReplace (not Append): with plain Append, once a prior upload
        // run ends FAILED/CANCELLED, every later request is chained behind it as
        // a blocked prerequisite and never runs. Replace keeps the queue live.
        WorkManager.GetInstance(ctx).EnqueueUniqueWork(OneTimeName, ExistingWorkPolicy.AppendOrReplace!, req);
    }

    /// <summary>A 15-minute safety-net pass so stragglers always drain eventually.</summary>
    public static void EnsurePeriodic(Context ctx)
    {
        var req = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(UploadWorker)), 15, TimeUnit.Minutes!)
            .SetConstraints(NetworkConstraints())
            .Build();
        WorkManager.GetInstance(ctx).EnqueueUniquePeriodicWork(PeriodicName, ExistingPeriodicWorkPolicy.Keep!, req);
    }
}
