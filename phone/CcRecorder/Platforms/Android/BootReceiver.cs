using Android.App;
using Android.Content;

namespace CcRecorder.Platforms.Android;

/// <summary>
/// Re-arms the background upload workers after a device reboot. WorkManager
/// persists scheduled work across a normal reboot, but a force-stop (the user
/// or the OS stopping the app) cancels all scheduled work until the app is next
/// launched - and the only launch-time arming lives in the UI. This receiver
/// closes that gap: on <c>BOOT_COMPLETED</c> it re-schedules the periodic safety
/// net and kicks a one-time pass, so pending recordings drain in the background
/// after a reboot without the user having to open the app (issue #687).
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        if (intent?.Action != Intent.ActionBootCompleted) return;

        // Re-arm the periodic safety net and kick an immediate pass. Both are
        // idempotent and govern reachability themselves, so this is safe to run
        // on every boot.
        UploadScheduler.EnsurePeriodic(context);
        UploadScheduler.EnqueueNow(context);
    }
}
