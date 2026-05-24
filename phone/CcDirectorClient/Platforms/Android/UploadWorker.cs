using Android.Content;
using AndroidX.Work;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// WorkManager worker that drains the upload queue in the background, even when
/// the app is closed. Reuses the same upload logic as the in-app path
/// (<see cref="AndroidAudioRecorder.ProcessUploadQueueAsync"/>), which reads
/// recordings from disk and credentials from preferences, so it does not need
/// the live UI. Returns Retry while anything is still pending so WorkManager
/// reschedules with backoff.
/// </summary>
public class UploadWorker : Worker
{
    public UploadWorker(Context context, WorkerParameters workerParams)
        : base(context, workerParams)
    {
    }

    public override Result DoWork()
    {
        try
        {
            // The recorder's queue logic is stateless w.r.t. live recording;
            // a fresh instance operates purely on disk + preferences.
            var recorder = new AndroidAudioRecorder();
            recorder.ProcessUploadQueueAsync().GetAwaiter().GetResult();

            var anyPending = recorder.ListRecordings()
                .Any(r => r.State is "Queued" or "Retry" or "Uploading");
            return anyPending ? Result.InvokeRetry()! : Result.InvokeSuccess()!;
        }
        catch
        {
            return Result.InvokeRetry()!;
        }
    }
}
