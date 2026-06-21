using System.Diagnostics;
using Android.Media;
using Android.OS;
using CcDirectorClient.Voice;
using AndroidApp = Android.App.Application;

namespace CcDirectorClient.Platforms.Android;

/// <summary>
/// Plays the spoken reply on-device using Android's <see cref="MediaPlayer"/>.
/// The audio is MP3 produced by the Director's OpenAI TTS voice (POST /tts), so
/// the phone speaks with the same natural voice as the web voice page instead of
/// the robotic system text-to-speech engine. While playing, the engine requests
/// transient audio focus with "may duck" so any music dips under the voice
/// instead of stopping, then the focus is abandoned so the music returns to full
/// volume (Phase 3 ducking, preserved from the old native speaker).
///
/// OBSERVABILITY (issue #394): every playback logs its START (bytes + estimated
/// duration), every audio-focus change while it plays, and its TERMINAL outcome
/// with elapsed wall time and the played-versus-estimated duration - so a truncated
/// playback (Interrupted, stopped well short of the estimate) is distinguishable in
/// the log from a clean finish (Completed) and from a decoder failure (Error, with
/// MediaPlayer what/extra codes). The Android <c>MediaPlayer.Completion</c> event
/// fires identically whether playback finished or was cut off, so this class records
/// which terminal path it actually took rather than inferring it from the event.
/// </summary>
public sealed class AndroidReplySpeaker : Java.Lang.Object, IReplySpeaker, AudioManager.IOnAudioFocusChangeListener
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private AudioManager? _audioManager;
    private AudioFocusRequestClass? _focusRequest;

    // Broadcasts "is a clip playing" so a screen can show its Stop-talking control only while
    // there is something to stop (issue #146). Change-only, so it does not flicker the button.
    private readonly PlaybackSignal _signal = new();

    public bool IsPlaying => _signal.IsPlaying;

    public event Action<bool>? PlayingChanged
    {
        add => _signal.Changed += value;
        remove => _signal.Changed -= value;
    }

    public async Task<PlaybackOutcome> PlayAsync(byte[] audio, CancellationToken ct = default)
    {
        if (audio is null || audio.Length == 0) return PlaybackOutcome.None;

        // Stop anything currently playing before starting the next clip.
        StopInternal();

        // MediaPlayer plays from a file path most reliably; spill the bytes to a
        // unique cache file and delete it once playback ends.
        var path = System.IO.Path.Combine(
            Microsoft.Maui.Storage.FileSystem.CacheDirectory,
            $"ccd-tts-{Guid.NewGuid():N}.mp3");
        await System.IO.File.WriteAllBytesAsync(path, audio, ct);

        // Estimate how long this clip should run (issue #394). The terminal line compares
        // this with the wall-clock time actually played, which is what makes a cutout visible.
        var estimated = Mp3Duration.Estimate(audio);

        // The terminal result starts Completed and is overwritten by the FIRST terminal
        // signal: Error (decoder failure) or Interrupted (Stop/cancel). MediaPlayer.Completion
        // resolves the await without changing the result, so a natural finish stays Completed.
        var result = PlaybackResult.Completed;
        int? errorWhat = null, errorExtra = null;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new MediaPlayer();
        lock (_gate) { _player = player; }

        player.Completion += (_, _) => tcs.TrySetResult(true);
        player.Error += (_, e) =>
        {
            // Record the decoder's codes so the terminal line carries them, then end the await.
            errorWhat = (int)e.What;
            errorExtra = e.Extra;
            result = PlaybackResult.Error;
            e.Handled = true; // we handle it; suppress the follow-up Completion event
            tcs.TrySetResult(false);
        };

        // AudioUsageKind.Assistant is API 26+. On older devices the MediaPlayer
        // uses its default (music) stream, which still plays fine and still ducks
        // via the legacy audio-focus path below.
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            player.SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Assistant)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!);
        }

        RequestAudioFocus();
        ClientLog.Write(PlaybackLog.Start(audio.Length, estimated));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            player.SetDataSource(path);
            player.Prepare();
            // The cancellation token firing is an interruption (cut off mid-playback), distinct
            // from a clean Completion. Record it before resolving the await so the terminal line
            // reads Interrupted, not Completed.
            using (ct.Register(() =>
            {
                result = PlaybackResult.Interrupted;
                try { player.Stop(); } catch { }
                tcs.TrySetResult(false);
            }))
            {
                player.Start();
                _signal.Begin();   // playing now: any visible screen shows its Stop-talking control
                await tcs.Task;
            }
        }
        finally
        {
            stopwatch.Stop();
            lock (_gate) { if (_player == player) _player = null; }
            try { player.Release(); } catch { }
            try { player.Dispose(); } catch { }
            AbandonAudioFocus();
            _signal.End();         // playback ended (finished, stopped, or cancelled)
            try { System.IO.File.Delete(path); } catch { }
        }

        // result is Completed only if MediaPlayer.Completion resolved the await; the Error handler
        // and the cancellation registration each overwrite it to Error / Interrupted before
        // resolving, so the terminal line reflects the path actually taken (issue #394).
        var outcome = new PlaybackOutcome(result, audio.Length, estimated, stopwatch.Elapsed);
        ClientLog.Write(PlaybackLog.Terminal(outcome, errorWhat, errorExtra));
        return outcome;
    }

    public void Stop()
    {
        StopInternal();
        AbandonAudioFocus();
        _signal.End();             // make the Stop-talking control disappear even if no PlayAsync is awaiting
    }

    private void StopInternal()
    {
        MediaPlayer? p;
        lock (_gate) { p = _player; _player = null; }
        if (p is null) return;
        try { p.Stop(); } catch { }
        try { p.Release(); } catch { }
        try { p.Dispose(); } catch { }
    }

    // ===== audio focus (Phase 3: duck music under the voice) ================

    /// <summary>
    /// Audio-focus changes observed while a reply plays (issue #394): a transient
    /// loss/duck means the system pulled focus (e.g. a call, an alarm) and the voice
    /// may have been ducked or paused under it - one more reason a turn can sound cut
    /// off. Logged, not acted on: the existing duck/abandon behaviour is unchanged.
    /// </summary>
    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        var change = focusChange switch
        {
            AudioFocus.Gain => "gain",
            AudioFocus.Loss => "loss",
            AudioFocus.LossTransient => "loss-transient",
            AudioFocus.LossTransientCanDuck => "loss-transient-can-duck",
            _ => focusChange.ToString(),
        };
        ClientLog.Write(PlaybackLog.FocusChange(change));
    }

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
                    // Register for focus-change callbacks so gain/loss/duck is observable in the
                    // log during playback (issue #394). A Handler on the main looper delivers them.
                    .SetOnAudioFocusChangeListener(this, new Handler(Looper.MainLooper!))!
                    .Build();
                _audioManager.RequestAudioFocus(_focusRequest!);
            }
            else
            {
#pragma warning disable CA1422 // legacy overload only on pre-26 devices
                _audioManager.RequestAudioFocus(this, global::Android.Media.Stream.Music, AudioFocus.GainTransientMayDuck);
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[AndroidReplySpeaker] RequestAudioFocus failed: {ex.Message}");
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
                _audioManager.AbandonAudioFocus(this);
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[AndroidReplySpeaker] AbandonAudioFocus failed: {ex.Message}");
        }
    }
}
