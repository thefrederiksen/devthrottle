using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Audio;
using CcDirector.Core.Utilities;
using NAudio.Wave;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Plays short user-interface sound cues on the desktop by SYNTHESIZING the
/// waveform in code - there are no bundled audio files to ship or resolve. The
/// only cue today is the dictation "ready" signal: a brief water-drop "bloop"
/// played the instant the microphone is confirmed capturing real audio, so the
/// user hears exactly when to start speaking (the same courtesy the Windows
/// dictation panel gives with its ready beep).
///
/// The tone is generated as 16-bit mono PCM and played through NAudio's default
/// output device, mirroring <see cref="DesktopTtsPlayer"/>'s playback pattern.
/// Playback is fire-and-forget and best-effort by design: a cue is a courtesy,
/// so a missing or busy output device must never disrupt the dictation turn - a
/// failure is logged and swallowed, exactly as a missing text-to-speech voice is.
/// </summary>
public sealed class DesktopAudioCue
{
    private const int SampleRate = 44_100;

    /// <summary>
    /// How long playback is given to report its end before the end is abandoned and never reported. The
    /// dictation recorder waits no longer than this for a cue whose end has not arrived when the user
    /// stops (issue #2925): past it, no end can ever be reported.
    /// </summary>
    public static readonly TimeSpan PlaybackEndAllowance = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Play the dictation "ready" water-drop bloop once. Returns immediately and
    /// plays on a background thread. Never throws: a cue failure is logged and
    /// swallowed so it can never take down the caller's dictation turn.
    /// </summary>
    /// <param name="onPlaybackFinished">Called once, on the playback thread, when the output device
    /// reports that the cue has FINISHED playing (NAudio's <c>PlaybackStopped</c> with no error). This is the
    /// only evidence the whole cue sounded, so it is the dictation recorder's gate for blanking the cue
    /// (issue #2925, ruling L1), and it releases a Send that landed while the cue was still playing. It never
    /// positions anything - <c>PlaybackStopped</c> follows NAudio's padded output buffer and can be ~100 ms
    /// after the audible end. Not called when playback fails or never starts.</param>
    /// <param name="onPlaybackFailed">Called at most once, with the reason, when the cue did NOT play to
    /// completion: synthesis or initialisation failed, playback stopped with an error (possibly part-way
    /// through the cue), or no end was reported within <see cref="PlaybackEndAllowance"/>. Exactly one of the
    /// two callbacks is called for every call to this method.</param>
    public void PlayReady(Action? onPlaybackFinished = null, Action<string>? onPlaybackFailed = null)
    {
        byte[] pcm;
        try
        {
            pcm = BuildWaterDropBloop();
        }
        catch (Exception ex)
        {
            // Synthesis is pure arithmetic and should never fail; if it somehow does,
            // the cue is skipped rather than propagating into the dictation flow.
            FileLog.Write($"[DesktopAudioCue] bloop synthesis failed: {ex.Message}");
            onPlaybackFailed?.Invoke($"synthesis failed: {ex.Message}");
            return;
        }

        _ = Task.Run(() =>
        {
            // 0 = waiting, 1 = end reported, 2 = failure reported. Only the first transition counts, so exactly one
            // outcome is reported, and an end that arrives after the wait gave up (or during disposal) is not.
            int state = 0;
            try
            {
                using var ms = new MemoryStream(pcm);
                var source = new RawSourceWaveStream(ms, new WaveFormat(SampleRate, 16, 1));
                // Not disposed: the handler may still run after a timed-out wait, and a slim event
                // with no wait handle holds nothing that needs releasing.
                var stopped = new ManualResetEventSlim(false);
                using var output = new WaveOutEvent();
                output.PlaybackStopped += (_, args) =>
                {
                    if (args.Exception is not null)
                    {
                        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                        {
                            FileLog.Write($"[DesktopAudioCue] bloop playback stopped with an error, end NOT reported: {args.Exception.Message}");
                            onPlaybackFailed?.Invoke($"playback stopped with an error: {args.Exception.Message}");
                        }
                    }
                    else if (Interlocked.CompareExchange(ref state, 1, 0) == 0)
                    {
                        FileLog.Write("[DesktopAudioCue] bloop playback finished");
                        onPlaybackFinished?.Invoke();
                    }
                    stopped.Set();
                };
                output.Init(source);
                output.Play();
                // PlaybackStopped is raised on the playback thread once the device has drained the
                // buffer. Wait for it before disposing the device - disposing first would stop the
                // cue early and the event would describe a cut-off, not the real end.
                if (!stopped.Wait(PlaybackEndAllowance) && Interlocked.CompareExchange(ref state, 2, 0) == 0)
                {
                    FileLog.Write($"[DesktopAudioCue] bloop playback did not report its end within {PlaybackEndAllowance.TotalSeconds:F0} s, end NOT reported");
                    onPlaybackFailed?.Invoke($"no end reported within {PlaybackEndAllowance.TotalSeconds:F0} s");
                }
            }
            catch (Exception ex)
            {
                // Init or Play threw, or disposal did. Only the first outcome counts, so a failure after a
                // reported end changes nothing.
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                {
                    FileLog.Write($"[DesktopAudioCue] bloop playback failed, end NOT reported: {ex.Message}");
                    onPlaybackFailed?.Invoke($"playback failed: {ex.Message}");
                }
                else
                {
                    FileLog.Write($"[DesktopAudioCue] bloop playback failed after its outcome was settled: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// The water-drop "bloop" as 16-bit little-endian mono PCM at <see cref="SampleRate"/>. The waveform comes
    /// from <see cref="ReadyCue.Synthesize"/>, the same generator the dictation recorder uses to FIND the cue in
    /// captured audio (issue #2925), so what is searched for is exactly what was played.
    /// </summary>
    private static byte[] BuildWaterDropBloop() => ReadyCue.SynthesizePcm16(SampleRate);
}
