using Android.Media;
using CcDirectorClient.Voice;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Android audio cues for the voice recording flow. Uses <see cref="ToneGenerator"/>
/// (no file assets, no extra permissions beyond MODIFY_AUDIO_SETTINGS which is normal
/// for tone generation). All three methods are fire-and-forget: ToneGenerator.StartTone
/// is synchronous but brief, so each call is dispatched onto a background thread via
/// Task.Run to keep the UI thread free.
/// </summary>
public sealed class AndroidAudioCue : IAudioCue
{
    // ToneGenerator volume scale is 0..100; the Android binding exposes it as Android.Media.Volume
    // (an enum over int). 80 is clearly audible without being jarring.
    private const global::Android.Media.Volume ToneVolume = (global::Android.Media.Volume)80;

    // Durations chosen for minimum perceptible confirmation while driving:
    // start=150ms (distinctive short beep), stop=100ms (higher pitch, punchy), error=300ms (unmissable).
    private const int StartMs = 150;
    private const int StopMs  = 100;
    private const int ErrorMs = 300;

    public void PlayStart()
    {
        // Beep (mid pitch) - recording started.
        _ = Task.Run(() => PlayTone(Tone.PropBeep, StartMs));
    }

    public void PlayStop()
    {
        // Higher pitch beep - submitted successfully.
        _ = Task.Run(() => PlayTone(Tone.PropBeep2, StopMs));
    }

    public void PlayError()
    {
        // Negative acknowledgement tone - turn failed.
        _ = Task.Run(() => PlayTone(Tone.PropNack, ErrorMs));
    }

    private static void PlayTone(Tone tone, int durationMs)
    {
        ToneGenerator? gen = null;
        try
        {
            gen = new ToneGenerator(global::Android.Media.Stream.Notification, ToneVolume);
            gen.StartTone(tone, durationMs);
            // Block the background thread for the duration so the generator is not
            // disposed before the tone finishes. ToneGenerator.StartTone is async
            // internally; the documented approach is to wait the duration or call
            // StopTone, then release.
            System.Threading.Thread.Sleep(durationMs + 50); // +50ms safety margin
            gen.StopTone();
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[AndroidAudioCue] PlayTone({tone}) FAILED: {ex.Message}");
        }
        finally
        {
            try { gen?.Release(); } catch { }
            try { gen?.Dispose(); } catch { }
        }
    }
}
