using Android.Media;
using CcDirectorClient.Voice;
using ATts = Android.Speech.Tts;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Android audio cues for the voice recording flow.
///
/// Beep cues (Start/Stop/Error): ToneGenerator on a background thread - no permissions,
/// no file assets, instant playback.
///
/// Status announcements (Sent/Reply): Android's on-device TextToSpeech - local, offline,
/// no network call, no OpenAI timeout risk. Initialized lazily on first use.
///
/// Thinking sound: soft repeating beep every 3 seconds while the agent is working,
/// cancelled by StopThinking(). Gives the user a "still alive" pulse without being
/// intrusive.
/// </summary>
public sealed class AndroidAudioCue : IAudioCue, IDisposable
{
    private const global::Android.Media.Volume ToneVolume  = (global::Android.Media.Volume)80;
    private const global::Android.Media.Volume ThinkVolume = (global::Android.Media.Volume)40;

    private const int StartMs    = 150;
    private const int StopMs     = 100;
    private const int ErrorMs    = 300;
    private const int ThinkMs    = 80;
    private const int ThinkGapMs = 3000;

    private ATts.TextToSpeech? _tts;
    private readonly object _ttsLock = new();
    private bool _ttsReady;

    private CancellationTokenSource? _thinkingCts;

    // ===== recording beeps =================================================

    public void PlayStart() => _ = Task.Run(() => PlayTone(Tone.PropBeep,  StartMs, ToneVolume));
    public void PlayStop()  => _ = Task.Run(() => PlayTone(Tone.PropBeep2, StopMs,  ToneVolume));
    public void PlayError() => _ = Task.Run(() => PlayTone(Tone.PropNack,  ErrorMs, ToneVolume));

    // ===== status announcements (local TTS) ================================

    public void PlaySent()  => SpeakPhrase("Sent");
    public void PlayReply() => SpeakPhrase("Agent replied");

    // ===== thinking sound ==================================================

    public void StartThinking()
    {
        _thinkingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thinkingCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                PlayTone(Tone.PropBeep, ThinkMs, ThinkVolume);
                try { await Task.Delay(ThinkGapMs, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void StopThinking()
    {
        _thinkingCts?.Cancel();
        _thinkingCts = null;
    }

    // ===== internals =======================================================

    private void SpeakPhrase(string text)
    {
        _ = Task.Run(() =>
        {
            try
            {
                EnsureTts();
                if (_ttsReady)
                    _tts?.Speak(text, ATts.QueueMode.Flush, null, null);
            }
            catch (Exception ex)
            {
                ClientLog.Write($"[AndroidAudioCue] SpeakPhrase({text}) FAILED: {ex.Message}");
            }
        });
    }

    private void EnsureTts()
    {
        lock (_ttsLock)
        {
            if (_tts != null) return;
            var ctx = global::Android.App.Application.Context;
            _tts = new ATts.TextToSpeech(ctx, new TtsInitListener(ready => _ttsReady = ready));
        }
    }

    private static void PlayTone(Tone tone, int durationMs, global::Android.Media.Volume volume)
    {
        ToneGenerator? gen = null;
        try
        {
            gen = new ToneGenerator(global::Android.Media.Stream.Notification, volume);
            gen.StartTone(tone, durationMs);
            System.Threading.Thread.Sleep(durationMs + 50);
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

    public void Dispose()
    {
        StopThinking();
        try { _tts?.Shutdown(); } catch { }
        _tts?.Dispose();
    }

    private sealed class TtsInitListener : Java.Lang.Object, ATts.TextToSpeech.IOnInitListener
    {
        private readonly Action<bool> _onReady;
        public TtsInitListener(Action<bool> onReady) => _onReady = onReady;
        public void OnInit(ATts.OperationResult status) => _onReady(status == ATts.OperationResult.Success);
    }
}
