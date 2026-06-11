using Android.Content;
using Android.Views;
using CcDirectorClient.Voice;
using AndroidApp = Android.App.Application;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Android implementation of <see cref="IVoiceForeground"/>: starts and stops the
/// <see cref="VoiceForegroundService"/> so the voice round-trip and TTS survive
/// backgrounding and screen-off.
///
/// Also manages the driving-mode window flags so the screen stays on and the app
/// shows over the lock screen while voice is active (same approach as Waze / Maps):
///   - FLAG_KEEP_SCREEN_ON  — already set by TalkPage via DeviceDisplay.KeepScreenOn;
///     we don't duplicate it here.
///   - SetShowWhenLocked(true) + SetTurnScreenOn(true) (API 27+) — shows the app on
///     top of the lock screen and wakes the display when a turn arrives, so a glance
///     at the phone in a car mount is enough. Cleared in Stop() so the lock screen
///     resumes normal behaviour after voice mode ends.
///   - FLAG_SHOW_WHEN_LOCKED + FLAG_TURN_SCREEN_ON (API 24-26) — same intent via the
///     older deprecated window-flag path for devices that don't have the API 27 methods.
///
/// Both paths require no additional permissions.
/// </summary>
public sealed class AndroidVoiceForeground : IVoiceForeground
{
    private readonly object _gate = new();

    public bool IsActive { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (IsActive) return;
            var ctx = AndroidApp.Context;
            var intent = new Intent(ctx, typeof(VoiceForegroundService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                ctx.StartForegroundService(intent);
            else
                ctx.StartService(intent);
            IsActive = true;
            ClientLog.Write("[AndroidVoiceForeground] started");
        }
        SetDrivingFlags(true);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsActive) return;
            var ctx = AndroidApp.Context;
            ctx.StopService(new Intent(ctx, typeof(VoiceForegroundService)));
            IsActive = false;
            ClientLog.Write("[AndroidVoiceForeground] stopped");
        }
        SetDrivingFlags(false);
    }

    // ===== lock-screen bypass =============================================

    private static void SetDrivingFlags(bool enable)
    {
        // Window flag changes must happen on the UI thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                if (activity is null) return;

                if (OperatingSystem.IsAndroidVersionAtLeast(27))
                {
                    // API 27+: clean, non-deprecated path.
                    activity.SetShowWhenLocked(enable);
                    activity.SetTurnScreenOn(enable);
                }
                else
                {
                    // API 24-26: older window-flag path (deprecated at 27 but still works).
                    const WindowManagerFlags flags =
                        WindowManagerFlags.ShowWhenLocked |
                        WindowManagerFlags.TurnScreenOn;
                    if (enable)
                        activity.Window?.AddFlags(flags);
                    else
                        activity.Window?.ClearFlags(flags);
                }

                ClientLog.Write($"[AndroidVoiceForeground] driving flags {(enable ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                ClientLog.Write($"[AndroidVoiceForeground] SetDrivingFlags({enable}) FAILED: {ex.Message}");
            }
        });
    }
}
