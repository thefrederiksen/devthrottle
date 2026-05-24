using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Minimal foreground service whose only job is to keep the app process alive
/// (and the persistent notification visible) while recording, so Android does
/// not reclaim it when the screen locks or the app is backgrounded. The actual
/// capture + segment rotation runs in <see cref="AndroidAudioRecorder"/> in the
/// same process.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone)]
public sealed class RecorderForegroundService : Service
{
    public const string ChannelId = "cc_recorder_capture";
    private const int NotificationId = 4801;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("CC Director Client")
            .SetContentText("Recording in progress")
            .SetSmallIcon(global::Android.Resource.Drawable.PresenceAudioOnline)
            .SetOngoing(true)
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification,
                global::Android.Content.PM.ForegroundService.TypeMicrophone);
        else
            StartForeground(NotificationId, notification);

        // Sticky: if the process is killed, Android tries to restart the service.
        return StartCommandResult.Sticky;
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr is null) return;
        if (mgr.GetNotificationChannel(ChannelId) is not null) return;
        var channel = new NotificationChannel(ChannelId, "Recording", NotificationImportance.Low)
        {
            Description = "Active audio recording",
        };
        mgr.CreateNotificationChannel(channel);
    }
}
