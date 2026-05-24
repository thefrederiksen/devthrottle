using Android.Content;
using AndroidX.Work;
using Java.Util.Concurrent;

namespace CcRecorder.Platforms.Android;

/// <summary>
/// Schedules the background upload queue via Android WorkManager so recordings
/// upload even when the app is fully swiped-closed, and resume after a reboot.
/// Work is constrained to network availability; WorkManager persists the queue
/// and applies backoff on failure.
/// </summary>
public static class UploadScheduler
{
    private const string OneTimeName = "cc-upload-now";
    private const string PeriodicName = "cc-upload-periodic";

    private static Constraints NetworkConstraints()
        => new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)
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
