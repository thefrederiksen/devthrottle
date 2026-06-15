using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// Ensures an <c>@.temp/...</c> file-reference prompt actually SUBMITS (issue #212).
///
/// THE TRAP
/// --------
/// Large/multi-line prompts are sent as an @-temp-file reference typed into claude's
/// composer plus an Enter. That Enter is unreliable: claude's path-autocomplete popup can
/// eat it (selecting the completion instead of submitting), and during claude's startup
/// window the input loop buffers typed TEXT but drops Enter keypresses entirely. Both leave
/// the full prompt parked in the composer while the session looks idle - observed live on
/// the 2026-06-06 restore E2E across multiple attempts. Handover seeds, PrePrompts and
/// restore continuations all ride this path.
///
/// THE SIGNAL: PER-WINDOW OUTPUT DELTAS
/// ------------------------------------
/// Screen parsing failed live (the TUI paints incrementally; the parked composer is not
/// reliably visible in a stream diff). A single cumulative growth check also failed live
/// (the typed reference's own echo + popup render crossed the threshold). What discriminates
/// reliably is the TUI's output rhythm per beat:
///   - DEAD window (almost nothing): parked - the live incidents froze the byte count for
///     minutes. Nudge with Enter; a stray Enter on an empty composer is a no-op.
///   - SETTLING window (echo/popup repaints, small): in flux - wait, do not spam.
///   - STREAMING (large cumulative growth): submitted - claude echoes the prompt, animates
///     the spinner and streams its reply at kilobytes per second. Done.
/// </summary>
public static class AtReferenceSubmitVerifier
{
    private static readonly byte[] EnterByte = { 0x0D };

    /// <summary>Default beat length between checks; tests pass a faster one explicitly.</summary>
    internal static readonly TimeSpan DefaultAttemptDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Cumulative growth (since the submitting Enter) that proves the prompt went through.
    /// Above any echo + popup + status repaint combination observed live (those stayed in
    /// the hundreds of bytes); a streaming claude crosses this within a beat or two.
    /// </summary>
    internal const int SubmittedGrowthBytes = 2048;

    /// <summary>A beat with less output than this is a DEAD window: nothing is happening.</summary>
    internal const int QuietWindowBytes = 64;

    /// <summary>Upper bound on beats - covers ~10s of claude startup at the default delay.</summary>
    internal const int MaxAttempts = 8;

    /// <summary>
    /// Watch the TUI's output rhythm after the submitting Enter: nudge on dead windows,
    /// wait on settling ones, return once streaming proves the submit.
    /// </summary>
    /// <param name="buffer">The session's terminal buffer (null = no signal available; single best-effort nudge).</param>
    /// <param name="write">Writes raw bytes to the TUI's stdin.</param>
    /// <param name="atReference">The typed reference (logging only), e.g. <c>@.temp/input_x.txt</c>.</param>
    /// <param name="attemptDelay">Beat length override; tests pass a fast one. Defaults to <see cref="DefaultAttemptDelay"/>.</param>
    /// <param name="beatDelay">
    /// How to wait out one beat. Defaults to <c>Task.Delay</c>. Tests inject this to drive the beat
    /// deterministically (e.g. write the per-beat output synchronously and return), so a test never
    /// races a real-time painter against the beat clock.
    /// </param>
    public static async Task EnsureSubmittedAsync(
        CircularTerminalBuffer? buffer, Action<byte[]> write, string atReference,
        TimeSpan? attemptDelay = null, Func<TimeSpan, Task>? beatDelay = null)
    {
        var beat = attemptDelay ?? DefaultAttemptDelay;
        var wait = beatDelay ?? (b => Task.Delay(b));
        if (buffer is null)
        {
            // No buffer = no evidence either way. One best-effort nudge (no-op when the
            // first Enter landed) is all that can be done.
            await wait(beat);
            FileLog.Write($"[AtReferenceSubmitVerifier] no buffer to verify '{atReference}' - sending one blind nudge");
            write(EnterByte);
            return;
        }

        var baseline = buffer.TotalBytesWritten;
        var lastSeen = baseline;
        var nudges = 0;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await wait(beat);
            var total = buffer.TotalBytesWritten;
            var windowDelta = total - lastSeen;
            lastSeen = total;

            if (total - baseline >= SubmittedGrowthBytes)
            {
                if (nudges > 0)
                    FileLog.Write($"[AtReferenceSubmitVerifier] '{atReference}' submitted after " +
                                  $"{nudges} nudge(s): TUI streamed {total - baseline} bytes");
                return;
            }

            if (windowDelta < QuietWindowBytes)
            {
                nudges++;
                FileLog.Write($"[AtReferenceSubmitVerifier] dead window ({windowDelta} bytes in " +
                              $"{beat.TotalMilliseconds:0}ms) after '{atReference}' - nudging with Enter ({attempt}/{MaxAttempts})");
                write(EnterByte);
            }
            // else: settling (echo/popup repaints) - wait another beat without spamming.
        }

        FileLog.Write($"[AtReferenceSubmitVerifier] WARNING: no streaming after '{atReference}' " +
                      $"within {MaxAttempts} beats ({nudges} nudges sent) - the prompt may be parked unsubmitted");
    }
}
