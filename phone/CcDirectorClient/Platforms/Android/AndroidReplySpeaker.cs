using Android.Media;
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
/// </summary>
public sealed class AndroidReplySpeaker : Java.Lang.Object, IReplySpeaker
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private AudioManager? _audioManager;
    private AudioFocusRequestClass? _focusRequest;

    public async Task PlayAsync(byte[] audio, CancellationToken ct = default)
    {
        if (audio is null || audio.Length == 0) return;

        // Stop anything currently playing before starting the next clip.
        StopInternal();

        // MediaPlayer plays from a file path most reliably; spill the bytes to a
        // unique cache file and delete it once playback ends.
        var path = System.IO.Path.Combine(
            Microsoft.Maui.Storage.FileSystem.CacheDirectory,
            $"ccd-tts-{Guid.NewGuid():N}.mp3");
        await System.IO.File.WriteAllBytesAsync(path, audio, ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new MediaPlayer();
        lock (_gate) { _player = player; }

        player.Completion += (_, _) => tcs.TrySetResult(true);
        player.Error += (_, e) =>
        {
            ClientLog.Write($"[AndroidReplySpeaker] MediaPlayer error: what={e.What}, extra={e.Extra}");
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
        ClientLog.Write($"[AndroidReplySpeaker] PlayAsync: bytes={audio.Length}");

        try
        {
            player.SetDataSource(path);
            player.Prepare();
            using (ct.Register(() => { try { player.Stop(); } catch { } tcs.TrySetResult(false); }))
            {
                player.Start();
                await tcs.Task;
            }
        }
        finally
        {
            lock (_gate) { if (_player == player) _player = null; }
            try { player.Release(); } catch { }
            try { player.Dispose(); } catch { }
            AbandonAudioFocus();
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    public void Stop()
    {
        StopInternal();
        AbandonAudioFocus();
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
                _audioManager.AbandonAudioFocus(null);
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[AndroidReplySpeaker] AbandonAudioFocus failed: {ex.Message}");
        }
    }
}
