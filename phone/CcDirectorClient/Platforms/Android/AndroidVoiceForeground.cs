using Android.Content;
using CcDirectorClient.Voice;
using AndroidApp = Android.App.Application;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Android implementation of <see cref="IVoiceForeground"/>: starts and stops the
/// <see cref="VoiceForegroundService"/> so the voice round-trip and TTS survive
/// backgrounding and screen-off.
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
    }
}
