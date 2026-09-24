using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// Ensures a typed prompt actually SUBMITS - that the Enter after the text was not swallowed
/// (issue #212, pull request #1513).
///
/// THE TRAP
/// --------
/// The Enter that submits a prompt is unreliable. claude's path-autocomplete popup can eat it
/// (selecting the completion instead of submitting); during claude's startup window the input loop
/// buffers typed TEXT but drops Enter keypresses entirely; and a composer repainting at the moment
/// the byte lands can swallow it. All of them leave the full prompt parked in the composer while
/// the session looks idle - observed live on the 2026-06-06 restore E2E, and again on 2026-07-14
/// from a phone dictation that sat in the composer while the session reported Working.
///
/// Typing the text is verified separately (<see cref="TerminalSubmit"/>'s echo check proves the text
/// ARRIVED in the composer). This verifies the other half: that it LEFT.
///
/// THE SIGNAL: PER-WINDOW OUTPUT DELTAS
/// ------------------------------------
/// Screen parsing failed live (the TUI paints incrementally; the parked composer is not reliably
/// visible in a stream diff). A single cumulative growth check also failed live (the typed text's own
/// echo + popup render crossed the threshold). What discriminates reliably is the TUI's output rhythm
/// per beat:
///   - DEAD window (almost nothing): parked - the live incidents froze the byte count for minutes.
///     Nudge with Enter. The nudge is safe precisely because we only send it while the prompt is
///     still unsubmitted; a composer holding the operator's own hand-typed text alongside ours is
///     text they wanted submitted together anyway.
///   - SETTLING window (echo/popup repaints, small): in flux - wait, do not spam.
///   - STREAMING (large cumulative growth): submitted - the agent echoes the prompt, animates the
///     spinner and streams its reply at kilobytes per second. Done.
///
/// WHY IT THROWS
/// -------------
/// Exhausting every beat without streaming means the prompt is parked. Returning quietly there is
/// what let a lost Enter masquerade as a successful send. It throws so the caller never marks the
/// session Working for a turn that never started, and the operator is told their words did not go.
/// </summary>
public static class SubmitVerifier
{
    private static readonly byte[] EnterByte = { 0x0D };

    /// <summary>Default beat length between checks; tests pass a faster one explicitly.</summary>
    internal static readonly TimeSpan DefaultAttemptDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Cumulative growth (since the submitting Enter) that proves the prompt went through.
    /// Above any echo + popup + status repaint combination observed live (those stayed in
    /// the hundreds of bytes); a streaming agent crosses this within a beat or two.
    /// </summary>
    internal const int SubmittedGrowthBytes = 2048;

    /// <summary>A beat with less output than this is a DEAD window: nothing is happening.</summary>
    internal const int QuietWindowBytes = 64;

    /// <summary>Upper bound on beats - covers ~10s of agent startup at the default delay.</summary>
    internal const int MaxAttempts = 8;

    /// <summary>
    /// Press the submitting Enter and prove it landed: watch the TUI's output rhythm, nudge on dead
    /// windows, wait on settling ones, return once streaming proves the submit.
    ///
    /// This presses the Enter ITSELF rather than taking one already sent, because the growth baseline
    /// has to be captured BEFORE the Enter. Reading it afterwards folds any bytes the agent streamed
    /// in the meantime into the baseline, so a submit that worked measures as zero growth and reads as
    /// parked - the watchdog would then nudge a live session and finally throw on a turn that was
    /// running fine. Owning both ends makes that ordering unrepresentable.
    /// </summary>
    /// <param name="buffer">The session's terminal buffer (null = no signal available; see below).</param>
    /// <param name="write">Writes raw bytes to the TUI's stdin.</param>
    /// <param name="label">What was submitted, for logging: an @-reference, or a truncated prompt.</param>
    /// <param name="attemptDelay">Beat length override; tests pass a fast one. Defaults to <see cref="DefaultAttemptDelay"/>.</param>
    /// <param name="beatDelay">
    /// How to wait out one beat. Defaults to <c>Task.Delay</c>. Tests inject this to drive the beat
    /// deterministically (e.g. write the per-beat output synchronously and return), so a test never
    /// races a real-time painter against the beat clock.
    /// </param>
    /// <exception cref="PromptNotSubmittedException">
    /// The agent never started the turn: the prompt is parked in the composer.
    /// </exception>
    /// <param name="throwWhenParked">False when the caller proves delivery from the agent's own conversation records
    /// (issue #3290). Output volume is then only a reason to nudge, never a verdict: it called a working Codex, reading
    /// a file quietly, "parked", and it called Claude Code's "nothing left to send" message a submitted turn.</param>
    public static async Task PressEnterAndVerifyAsync(
        CircularTerminalBuffer? buffer, Action<byte[]> write, string label,
        TimeSpan? attemptDelay = null, Func<TimeSpan, Task>? beatDelay = null, bool throwWhenParked = true)
    {
        var beat = attemptDelay ?? DefaultAttemptDelay;
        var wait = beatDelay ?? (b => Task.Delay(b));
        if (buffer is null)
        {
            // No buffer = no evidence either way, so there is nothing to verify, nudge or throw on.
            // The @-reference-only ancestor of this class sent one blind nudge here, reasoning that a
            // stray Enter on an empty composer is a no-op. That reasoning does not survive the move to
            // EVERY submit: the operator hand-types into the composer and sends more from their phone
            // to run both together, so an unconditional second Enter could submit whatever they were
            // halfway through typing. Guessing is worse than admitting we cannot see.
            write(EnterByte);
            FileLog.Write($"[SubmitVerifier] no terminal buffer - cannot verify '{label}' submitted");
            return;
        }

        var baseline = buffer.TotalBytesWritten;
        write(EnterByte);

        // NO BLIND NUDGES WHEN THE RECORDS DECIDE (issue #3290). Measured on 24 September 2026: Claude Code, taking in
        // an 8,238-character paste on a loaded machine, printed nothing for ten seconds after the Enter; this loop read
        // that as a swallowed Enter and pressed Enter eight more times. The paste was lost and the eight Enters sat in
        // the composer as blank lines at the head of the NEXT prompt. When the caller proves delivery from the agent's
        // own records it also presses Enter again - once, and only when the screen shows the text still waiting.
        if (!throwWhenParked)
        {
            FileLog.Write($"[SubmitVerifier] '{label}': Enter pressed once; the agent's own records decide whether it was delivered");
            return;
        }

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
                    FileLog.Write($"[SubmitVerifier] '{label}' submitted after " +
                                  $"{nudges} nudge(s): TUI streamed {total - baseline} bytes");
                return;
            }

            if (windowDelta < QuietWindowBytes)
            {
                nudges++;
                FileLog.Write($"[SubmitVerifier] dead window ({windowDelta} bytes in " +
                              $"{beat.TotalMilliseconds:0}ms) after '{label}' - nudging with Enter ({attempt}/{MaxAttempts})");
                write(EnterByte);
            }
            // else: settling (echo/popup repaints) - wait another beat without spamming.
        }

        if (!throwWhenParked)
        {
            FileLog.Write($"[SubmitVerifier] '{label}' produced under {SubmittedGrowthBytes} bytes in {MaxAttempts} beats " +
                          $"({nudges} nudge(s) sent); the agent's own records decide whether it was delivered");
            return;
        }

        throw new PromptNotSubmittedException(
            $"[SubmitVerifier] '{label}' never started a turn within {MaxAttempts} beats " +
            $"({nudges} nudge(s) sent): the agent produced under {SubmittedGrowthBytes} bytes, so the " +
            "prompt is parked in the composer unsubmitted. The TUI swallowed the Enter (an autocomplete " +
            "popup, a startup window that drops Enter, or a composer repaint).");
    }
}
