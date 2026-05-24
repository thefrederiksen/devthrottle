using Android.Media;
using Android.OS;
using Android.Speech.Tts;
using CcDirectorClient.Voice;
using Java.Util;
using AndroidApp = Android.App.Application;
using AndroidTts = Android.Speech.Tts.TextToSpeech;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Native Android text-to-speech (<see cref="TextToSpeech"/>). The agent reply is
/// spoken on-device so it works backgrounded and on weak signal, with no /tts
/// network fetch. While speaking, the engine requests transient audio focus with
/// "may duck" so any music dips under the voice instead of stopping, then the
/// focus is abandoned so the music returns to full volume (Phase 3).
/// </summary>
public sealed class AndroidTextToSpeech : Java.Lang.Object, IReplySpeaker, AndroidTts.IOnInitListener
{
    private readonly object _gate = new();
    private AndroidTts? _tts;
    private TaskCompletionSource<bool>? _initTcs;
    private TaskCompletionSource<bool>? _speakTcs;
    private AudioManager? _audioManager;
    private AudioFocusRequestClass? _focusRequest;

    public async Task<bool> InitAsync()
    {
        lock (_gate)
        {
            if (_tts is not null && _initTcs is not null && _initTcs.Task.IsCompletedSuccessfully)
                return true;
            if (_initTcs is not null) { /* init already in flight */ }
            else
            {
                ClientLog.Write("[AndroidTextToSpeech] InitAsync: creating engine");
                _initTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tts = new AndroidTts(AndroidApp.Context, this);
                _tts.SetOnUtteranceProgressListener(new ProgressListener(this));
            }
        }
        var ok = await _initTcs!.Task;
        ClientLog.Write($"[AndroidTextToSpeech] InitAsync: ready={ok}");
        return ok;
    }

    public void OnInit(OperationResult status)
    {
        var ok = status == OperationResult.Success;
        if (ok && _tts is not null)
            _tts.SetLanguage(Java.Util.Locale.English);
        _initTcs?.TrySetResult(ok);
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!await InitAsync())
            throw new InvalidOperationException("text-to-speech engine failed to initialize");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _speakTcs = tcs; }

        RequestAudioFocus();
        ClientLog.Write($"[AndroidTextToSpeech] SpeakAsync: chars={text.Length}");

        var id = Guid.NewGuid().ToString("N");
        var result = _tts!.Speak(text, QueueMode.Flush, null, id);
        if (result != OperationResult.Success)
        {
            AbandonAudioFocus();
            throw new InvalidOperationException("text-to-speech Speak call failed");
        }

        using (ct.Register(() => { try { _tts?.Stop(); } catch { } tcs.TrySetResult(false); }))
        {
            await tcs.Task;
        }
        AbandonAudioFocus();
    }

    public void Stop()
    {
        try { _tts?.Stop(); } catch { }
        _speakTcs?.TrySetResult(false);
        AbandonAudioFocus();
    }

    private void OnSpeakDone() => _speakTcs?.TrySetResult(true);

    // ===== audio focus (Phase 3: duck music under the voice) ================

    private void RequestAudioFocus()
    {
        try
        {
            _audioManager ??= (AudioManager?)AndroidApp.Context.GetSystemService(global::Android.Content.Context.AudioService);
            if (_audioManager is null) return;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var attrs = new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Assistant)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build();
                _focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)!
                    .SetAudioAttributes(attrs!)!
                    .Build();
                _audioManager.RequestAudioFocus(_focusRequest!);
            }
            else
            {
#pragma warning disable CA1422 // legacy overload only on pre-26 devices
                _audioManager.RequestAudioFocus(null, global::Android.Media.Stream.Music, AudioFocus.GainTransientMayDuck);
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[AndroidTextToSpeech] RequestAudioFocus failed: {ex.Message}");
        }
    }

    private void AbandonAudioFocus()
    {
        try
        {
            if (_audioManager is null) return;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                if (_focusRequest is not null) _audioManager.AbandonAudioFocusRequest(_focusRequest);
            }
            else
            {
#pragma warning disable CA1422
                _audioManager.AbandonAudioFocus(null);
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[AndroidTextToSpeech] AbandonAudioFocus failed: {ex.Message}");
        }
    }

    /// <summary>Bridges native utterance callbacks back to the awaiting Speak task.</summary>
    private sealed class ProgressListener : UtteranceProgressListener
    {
        private readonly AndroidTextToSpeech _owner;
        public ProgressListener(AndroidTextToSpeech owner) => _owner = owner;

        public override void OnStart(string? utteranceId) { }
        public override void OnDone(string? utteranceId) => _owner.OnSpeakDone();

        // Pre-21 signature; required override. The string overload below carries detail.
        public override void OnError(string? utteranceId) => _owner.OnSpeakDone();
        public override void OnError(string? utteranceId, TextToSpeechError errorCode)
        {
            ClientLog.Write($"[AndroidTextToSpeech] utterance error: {errorCode}");
            _owner.OnSpeakDone();
        }
    }
}
