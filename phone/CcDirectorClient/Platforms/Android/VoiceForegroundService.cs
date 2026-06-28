using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Foreground service that keeps the voice round-trip and native TTS alive while
/// the app is backgrounded or the screen is off. Without it, Android suspends the
/// app a few seconds after it loses focus, which is exactly the "problem fetching"
/// failure the web voice page hits: the agent finishes while another app is in
/// front and the reply never plays. Declared as both microphone (push-to-talk
/// capture) and media playback (speaking the reply) so both are permitted in the
/// background.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone
        | global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
public sealed class VoiceForegroundService : Service
{
    public const string ChannelId = "cc_director_client_voice";
    private const int NotificationId = 4901;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("DevThrottle Client")
            .SetContentText("Voice mode active")
            .SetSmallIcon(global::Android.Resource.Drawable.PresenceAudioOnline)
            .SetOngoing(true)
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification,
                global::Android.Content.PM.ForegroundService.TypeMicrophone
                    | global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
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
        var channel = new NotificationChannel(ChannelId, "Voice", NotificationImportance.Low)
        {
            Description = "Active voice conversation",
        };
        mgr.CreateNotificationChannel(channel);
    }
}
