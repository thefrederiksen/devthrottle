using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
using CcDirector.Core.Machine;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;


namespace CcDirector.Core.Drivers;

/// <summary>
/// Shared terminal-submit primitives for interactive CLI drivers. The echo-verified submit was
/// proven first on ClaudeDriver and then on CodexDriver: many TUIs repaint a cycling placeholder
/// in their composer, so a blind "type text, wait, one Enter" loses the Enter when driven
/// programmatically (the Enter lands mid-repaint and is swallowed, parking the prompt unsubmitted).
/// The fix is to type the text, wait until the composer echoes it back in the terminal byte stream,
/// then press Enter as a separate keystroke. This class is the single home for that logic so every
/// driver (Codex, Pi, ...) uses one tested implementation.
///
/// A submit has TWO halves, and both are verified here:
///   1. The text ARRIVED in the composer - the echo check (<see cref="EchoVerifiedInlineSubmitAsync"/>).
///   2. The text LEFT the composer - the Enter actually submitted (<see cref="SubmitVerifier"/>).
/// Half 2 used to be checked only on the @-temp-file route, so the COMMON route (a short single-line
/// prompt - every phone dictation) pressed Enter and returned without ever looking back. A swallowed
/// Enter therefore reported success: the prompt sat parked in the composer, the session was marked
/// Working, and the next send typed itself onto the end of the orphan and ran the two mashed together
/// (pull request #1513). Every Enter now goes through <see cref="PressEnterAndVerifyAsync"/>.
/// </summary>
/// <summary>What <see cref="TerminalSubmit.DoorbellSubmitAsync"/> came to.</summary>
public enum DoorbellSubmitOutcome
{
    /// <summary>The last look said no; nothing was written.</summary>
    NotTyped,

    /// <summary>The line was typed, Enter was pressed once, and the screen showed the turn.</summary>
    Verified,

    /// <summary>The line was typed, but its submit was not proven - Enter may or may not have been pressed.
    /// The caller reads the composer to decide what happened.</summary>
    NotVerified,
}

public static class TerminalSubmit
{
    private static readonly byte[] EscapeByte = [0x1B];
    private static readonly byte[] BracketedPasteStart = Encoding.UTF8.GetBytes("\x1b[200~");
    private static readonly byte[] BracketedPasteEnd = Encoding.UTF8.GetBytes("\x1b[201~");

    /// <summary>
    /// THE one place an Enter is pressed to submit a prompt. Settle, press, then watch the TUI until
    /// it proves the turn started - nudging a parked composer and throwing if it never does.
    ///
    /// With no terminal buffer there is no evidence either way, so this presses Enter and returns
    /// unverified. It deliberately does NOT send the blind nudge the @-reference-only verifier used
    /// to: that nudge was safe when a composer could only ever hold OUR text, but the operator also
    /// hand-types into the composer and sends more from their phone to run both together. An
    /// unconditional second Enter would submit whatever they were halfway through typing. When the
    /// buffer exists we only ever nudge a composer we can SEE is parked, which cannot do that.
    /// </summary>
    private static async Task PressEnterAndVerifyAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan settle,
        TimeSpan? submitVerifyBeat,
        bool throwWhenParked = true)
    {
        await Task.Delay(settle);
        await SubmitVerifier.PressEnterAndVerifyAsync(
            backend.Buffer, backend.Write, LabelFor(text, driverTag), submitVerifyBeat, throwWhenParked: throwWhenParked,
            nudgeOnlyWhen: TextWaitsInComposer.Value);
    }

    /// <summary>Short, log-safe description of what was submitted.</summary>
    private static string LabelFor(string text, string driverTag)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        const int maxChars = 60;
        var shown = oneLine.Length <= maxChars ? oneLine : oneLine[..maxChars] + "...";
        return $"{driverTag}: {shown}";
    }

    /// <summary>
    /// The single ConPTY submit protocol: trim caller submit newlines, use bracketed paste for
    /// large or multi-line blocks when the target TUI requested it, fall back to an @-temp-file
    /// reference when needed, and otherwise echo-verify before pressing Enter.
    /// <paramref name="screenSnapshot"/>, when provided, returns the CURRENT rendered screen rows
    /// and is consulted as a second opinion whenever the byte-stream echo check misses (see
    /// <see cref="ObserveComposerAsync"/>).
    /// <paramref name="submitVerifyBeat"/> overrides the post-Enter watchdog's beat length; tests pass
    /// a fast one so the suite does not wait out real-time beats.
    /// <paramref name="sessionId"/> attributes composer-echo misses to a session in
    /// <see cref="PromptDeliveryFailures"/> so they can be counted and shown on that session's row
    /// (issue internal#811). Default (empty) on the driver and backend routes, which have no session
    /// to name; the Director's own send path passes it.
    /// </summary>
    /// <param name="clearKeys">The keystrokes MEASURED to empty this agent's composer (<see cref="ComposerClearKeys"/>);
    /// null keeps the old single Escape for callers that do not know the agent.</param>
    /// <param name="composerHoldsNothing">When the caller can read the composer: true once it holds nothing. A composer
    /// that still holds text after it was cleared is never typed into.</param>
    /// <param name="recordsProofFollows">The caller proves delivery from the agent's own conversation records, so the
    /// output-volume verifier only nudges and logs; it does not decide.</param>
    /// <param name="composerText">Where the caller can read the agent's composer: the text it holds right now, or null
    /// when it holds nothing or cannot be read. Read whatever the agent is doing - a working agent still draws its
    /// composer - and used to see a paste taken in (see <see cref="WaitForPasteTakenInAsync"/>).</param>
    /// <returns>The exact line typed into the composer: the text itself, or the file instruction or @-reference that
    /// carries it. That is what the agent's records will hold.</returns>
    public static async Task<string> SharedSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        bool bracketedPasteEnabled = false,
        bool requireEcho = true,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null,
        TimeSpan? submitVerifyBeat = null,
        Guid sessionId = default,
        Func<int, byte[]?>? clearKeysFor = null,
        Func<bool>? composerHoldsNothing = null,
        bool recordsProofFollows = false,
        bool clearRetainedUnconditionally = false,
        Func<bool>? nudgeOnlyWhen = null,
        Func<string?>? composerText = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var throwWhenParked = !recordsProofFollows;
        var nudgeBefore = TextWaitsInComposer.Value;
        TextWaitsInComposer.Value = nudgeOnlyWhen;
        try
        {
            return await SharedSubmitCoreAsync(backend, text, driverTag, bracketedPasteEnabled, requireEcho, echoTimeout,
                pollInterval, enterSettleDelay, screenSnapshot, submitVerifyBeat, sessionId, clearKeysFor,
                composerHoldsNothing, throwWhenParked, clearRetainedUnconditionally, composerText);
        }
        finally
        {
            TextWaitsInComposer.Value = nudgeBefore;
        }
    }

    /// <summary>
    /// Whether the screen shows the send's text waiting in the composer - the rule for every Enter the send presses
    /// again (issue #3290, review finding 5) and the sign that a paste has been taken in. Carried alongside the send
    /// rather than through each route's parameters; <see cref="SharedSubmitAsync"/> sets it and puts the previous one
    /// back.
    /// </summary>
    private static readonly AsyncLocal<Func<bool>?> TextWaitsInComposer = new();

    private static async Task<string> SharedSubmitCoreAsync(
        ISessionBackend backend, string text, string driverTag, bool bracketedPasteEnabled, bool requireEcho,
        TimeSpan? echoTimeout, TimeSpan? pollInterval, TimeSpan? enterSettleDelay, Func<string[]>? screenSnapshot,
        TimeSpan? submitVerifyBeat, Guid sessionId, Func<int, byte[]?>? clearKeysFor, Func<bool>? composerHoldsNothing,
        bool throwWhenParked, bool clearRetainedUnconditionally, Func<string?>? composerText)
    {

        // RESOLVE A RETAINED COMPOSER BEFORE CHOOSING A ROUTE, NOT INSIDE ONE OF THEM (issue #2818).
        //
        // This guard first lived inside EchoVerifiedInlineSubmitAsync, which the code review proved was
        // the wrong place: a multiline send with bracketed paste enabled - reachable from the production
        // path, because Session.SendTextAsync passes the session's own BracketedPasteEnabled setting -
        // leaves here for BracketedPasteSubmitAsync and never passes the guard at all. It then typed and
        // pressed Enter over a composer still holding an earlier unsent prompt, submitting BOTH as one
        // instruction. That is exactly the corruption from pull request #1513 that this change exists to
        // prevent, reintroduced through a route the guard did not cover.
        //
        // Every route that writes new text is downstream of this line.
        await ResolveRetainedComposerAsync(backend, driverTag, screenSnapshot, clearKeysFor, composerHoldsNothing, clearRetainedUnconditionally);

        var textForCheck = text.TrimEnd('\r', '\n');
        if (ShouldUseInstructionFile(driverTag, textForCheck)
            && !string.IsNullOrWhiteSpace(backend.WorkingDirectory))
        {
            var instruction = await SubmitViaInstructionFileAsync(
                backend,
                textForCheck,
                driverTag,
                bracketedPasteEnabled,
                requireEcho,
                echoTimeout,
                pollInterval,
                enterSettleDelay,
                screenSnapshot,
                submitVerifyBeat,
                sessionId,
                throwWhenParked);
            ComposerRetention.Clear(backend);
            return instruction;
        }

        // A LONG TEXT REACHES CLAUDE CODE AS A FILE, NOT A PASTE (issue #3290, measured 24 September 2026). With every core
        // busy, Claude Code took in none of an 8 KB bracketed paste for minutes - no repaint at all - and the send was lost;
        // idle, the same paste arrived in ten seconds. An @-reference is sixty typed characters that Claude Code reads
        // itself, so its size no longer decides whether it arrives on a busy machine.
        if (ShouldReferenceRatherThanPaste(driverTag, textForCheck) && !string.IsNullOrWhiteSpace(backend.WorkingDirectory))
        {
            var reference = await SubmitViaAtReferenceAsync(backend, textForCheck, driverTag, echoTimeout, pollInterval, enterSettleDelay, screenSnapshot, submitVerifyBeat, sessionId, throwWhenParked);
            ComposerRetention.Clear(backend);
            return reference;
        }

        if (LargeInputHandler.IsLargeInput(textForCheck) || ShouldPasteRatherThanType(driverTag, textForCheck))
        {
            if (bracketedPasteEnabled)
            {
                await BracketedPasteSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay, submitVerifyBeat, throwWhenParked, composerText);
                ComposerRetention.Clear(backend);
                return textForCheck;
            }

            if (!string.IsNullOrWhiteSpace(backend.WorkingDirectory))
            {
                var atReference = await SubmitViaAtReferenceAsync(backend, textForCheck, driverTag, echoTimeout, pollInterval, enterSettleDelay, screenSnapshot, submitVerifyBeat, sessionId, throwWhenParked);
                ComposerRetention.Clear(backend);
                return atReference;
            }
        }

        if (requireEcho)
        {
            await EchoVerifiedInlineSubmitAsync(
                backend,
                textForCheck,
                driverTag,
                echoTimeout,
                pollInterval,
                enterSettleDelay,
                screenSnapshot,
                submitVerifyBeat,
                sessionId,
                throwWhenParked);
        }
        else
        {
            await TypeSettleEnterSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay, submitVerifyBeat, throwWhenParked);
        }

        // Every route that RETURNS has submitted; only a throw leaves a mark standing. Clearing here as
        // well as on each early return means no route can quietly keep a stale mark alive and make the
        // NEXT send press Escape over text that belongs to the owner.
        ComposerRetention.Clear(backend);
        return textForCheck;
    }

    /// <summary>
    /// THE DOORBELL'S SUBMIT (the Message Load mission, slice 2 fix round, inspection 4 ruling 2). A cut-down
    /// echo-verified submit for the one fixed doorbell line, with every step that could touch the owner's words
    /// taken out:
    ///  - NO NUDGE LADDER. Enter is pressed exactly once. <see cref="SubmitVerifier"/> presses Enter again on
    ///    every quiet beat for about ten seconds; an owner who starts typing in that window, while the agent's
    ///    answer is short, would have their half-typed words submitted.
    ///  - NO ESCAPE AND NO RETYPE. An echo that never arrives ends the attempt with Enter unpressed.
    ///  - NO RETAINED-COMPOSER STEP. The ringer never rings while a retention mark stands, so the Escape that
    ///    step presses is never needed here.
    ///  - THE LAST LOOK RUNS IMMEDIATELY BEFORE THE FIRST BYTE (ruling 1): when <paramref name="mayTypeNow"/>
    ///    says no, nothing is written at all.
    ///
    /// The submit is VERIFIED only when <paramref name="turnStarted"/> - the ringer's reading of the screen - says
    /// the line left the composer and a turn took it, within <paramref name="watch"/>. The byte count the shared
    /// verifier relies on is not used: a one-word answer never reaches it, and bytes cannot tell a submitted
    /// line from one the interface discarded.
    /// </summary>
    /// <param name="mayTypeNow">The last look; false means type nothing.</param>
    /// <param name="composerShowsLine">True when the rendered composer holds exactly the line - a second witness
    /// for the echo when the byte stream misses it.</param>
    /// <param name="turnStarted">True when the screen proves the line was submitted.</param>
    /// <param name="pause">How to wait between polls; tests pass one that does not sleep.</param>
    /// <param name="beforeEnter">Runs immediately before the one Enter is written.</param>
    public static async Task<DoorbellSubmitOutcome> DoorbellSubmitAsync(
        ISessionBackend backend,
        string line,
        string driverTag,
        Func<bool> mayTypeNow,
        Func<bool> composerShowsLine,
        Func<bool> turnStarted,
        TimeSpan? echoTimeout = null,
        TimeSpan? watch = null,
        TimeSpan? poll = null,
        Func<TimeSpan, Task>? pause = null,
        Action? beforeEnter = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(mayTypeNow);
        ArgumentNullException.ThrowIfNull(composerShowsLine);
        ArgumentNullException.ThrowIfNull(turnStarted);
        var wait = pause ?? (d => Task.Delay(d));
        var step = poll ?? TimeSpan.FromMilliseconds(100);
        var echoPolls = Polls(echoTimeout ?? BaseEchoTimeout, step);
        var watchPolls = Polls(watch ?? DoorbellWatch, step);

        if (!mayTypeNow())
            return DoorbellSubmitOutcome.NotTyped;

        var buffer = backend.Buffer;
        var cursor = buffer?.TotalBytesWritten ?? 0;
        await WriteTextAsync(backend, line);

        var needle = NormalizeForEcho(line);
        var echoed = false;
        for (var i = 0; i < echoPolls && !echoed; i++)
        {
            echoed = (buffer is not null && EchoSeen(buffer, cursor, needle)) || composerShowsLine();
            if (!echoed) await wait(step);
        }
        if (!echoed)
        {
            FileLog.Write($"[{driverTag}] DoorbellSubmit: the line never echoed - Enter NOT pressed");
            return DoorbellSubmitOutcome.NotVerified;
        }

        await wait(TimeSpan.FromMilliseconds(40));
        beforeEnter?.Invoke();
        backend.Write(DoorbellEnter);
        for (var i = 0; i < watchPolls; i++)
        {
            await wait(step);
            if (turnStarted())
            {
                FileLog.Write($"[{driverTag}] DoorbellSubmit: submitted, the screen shows the turn");
                return DoorbellSubmitOutcome.Verified;
            }
        }
        FileLog.Write($"[{driverTag}] DoorbellSubmit: Enter pressed once, no turn seen within {(watch ?? DoorbellWatch).TotalSeconds:0}s - NOT verified, no nudge sent");
        return DoorbellSubmitOutcome.NotVerified;
    }

    /// <summary>How long the doorbell submit watches the screen for the turn after its one Enter.</summary>
    public static readonly TimeSpan DoorbellWatch = TimeSpan.FromSeconds(10);

    private static readonly byte[] DoorbellEnter = [0x0D];

    private static int Polls(TimeSpan total, TimeSpan step) =>
        Math.Max(1, (int)Math.Ceiling(total.TotalMilliseconds / Math.Max(1, step.TotalMilliseconds)));

    private static bool EchoSeen(CircularTerminalBuffer buffer, long cursor, string needle)
    {
        var (bytes, _) = buffer.GetWrittenSince(cursor);
        var hay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
        return needle.Length > 0 && (hay.Contains(needle, StringComparison.Ordinal) || IndexOfInterleaved(hay, needle) >= 0);
    }

    /// <summary>
    /// Type <paramref name="text"/>, wait for the composer to echo it, then press Enter. Falls back
    /// to the backend's blind submit for large/multi-line input (the @-temp-file path) and for
    /// non-buffering backends (nothing to echo-verify against). Throws if the composer never echoes
    /// the typed text after two attempts, rather than silently parking the prompt.
    /// </summary>
    public static async Task EchoVerifiedSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null,
        TimeSpan? submitVerifyBeat = null,
        Guid sessionId = default)
        => await SharedSubmitAsync(
            backend,
            text,
            driverTag,
            bracketedPasteEnabled: false,
            requireEcho: true,
            echoTimeout,
            pollInterval,
            enterSettleDelay,
            screenSnapshot,
            submitVerifyBeat,
            sessionId);

    private static async Task EchoVerifiedInlineSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null,
        TimeSpan? submitVerifyBeat = null,
        Guid sessionId = default,
        bool throwWhenParked = true)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var buffer = backend.Buffer;
        if (buffer is null)
        {
            backend.Write(Encoding.UTF8.GetBytes(text));
            await PressEnterAndVerifyAsync(
                backend, text, driverTag, enterSettleDelay ?? TimeSpan.FromMilliseconds(50), submitVerifyBeat, throwWhenParked);
            return;
        }

        // THE DEADLINE IS MEASURED, NOT ASSUMED (issue #2818), AND IT RUNS FROM THE LAST SIGN OF PROGRESS (issue #3290).
        // A caller that passed an explicit timeout still gets exactly that fixed deadline - tests and drivers that
        // chose a value keep it.
        var pressure = MemoryPressure.Level(MemoryProbe.Read());
        var to = echoTimeout ?? ScaledEchoTimeout(pressure);
        var cap = echoTimeout ?? EchoProgressCap;
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var settle = enterSettleDelay ?? TimeSpan.FromMilliseconds(40);
        var needle = NormalizeForEcho(text);
        var visibleTailNeedle = VisibleTailNeedle(needle);

        if (echoTimeout is null && MemoryPressure.IsUnderPressure(pressure))
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: {MemoryPressure.Describe(pressure)}, so the composer " +
                          $"echo deadline is {to.TotalSeconds:F0}s after the last output instead of {BaseEchoTimeout.TotalSeconds:F0}s. " +
                          "A slow repaint is not a stuck interface.");

        // TYPED ONCE, NEVER CLEARED AND RETYPED (issue #3290). This used to press Escape and type the text again when
        // the echo was late. Measured on 24 September 2026 with real agents: a single Escape empties the composer of
        // NEITHER Claude Code NOR Codex, and on a slow terminal the Escape and the next letter arrive together and are
        // read as one Alt keystroke. So the "retype" appended a second copy - Codex, Pi and OpenCode all received their
        // prompt twice, run together - and the next send was appended to that. A text that is not proven to be in the
        // composer is now reported, with the composer left as it is and marked, never typed again here.
        var cursor = buffer.TotalBytesWritten;
        var prefixBefore = ScreenPrefixLength(screenSnapshot, needle);
        // TEXT ALREADY ON SCREEN IS NOT AN ECHO (issue #3290). Measured on 24 September 2026 under full processor load:
        // the payload instruction sent to Codex ends in the same words every time, an earlier one was still visible in
        // Codex's history, and its tail passed for the echo of a new instruction Codex was still taking in one letter at
        // a time. Enter went in half way through, the prompt was lost, and the rest of it ran into the next prompt. So a
        // tail already on screen is never used, and the whole text counts only when the screen shows it MORE often than
        // it did before the typing.
        var rowsBefore = screenSnapshot is null ? null : ReadScreen(screenSnapshot);
        var needleBefore = rowsBefore is null ? 0 : CountIn(NormalizeForEcho(string.Concat(rowsBefore)), needle);
        if (visibleTailNeedle is not null && rowsBefore is not null
            && NormalizeForEcho(string.Concat(rowsBefore)).Contains(visibleTailNeedle, StringComparison.Ordinal))
            visibleTailNeedle = null;
        if (needleBefore > 0)
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: the same text is already on screen {needleBefore} time(s); " +
                          "only a new copy on screen counts as its echo");
        await WriteTextAsync(backend, text);

        if (needle.Length == 0 || await WaitForEchoAsync(buffer, cursor, needle, visibleTailNeedle, to, poll, cap,
                screenSnapshot, prefixBefore, needleBefore,
                new SendWaitNotice(driverTag, $"the composer to echo a {text.Length}-character text",
                    $"{to.TotalSeconds:F0}s after the terminal last moved, at most {cap.TotalSeconds:F0}s")))
        {
            await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat, throwWhenParked);
            ComposerRetention.Clear(backend);
            return;
        }

        // The byte stream is a poor witness for input that WRAPPED across composer rows (issue #1592), so the rendered
        // screen is asked before the text is called missing. The reading is taken NOW, not before typing.
        pressure = MemoryPressure.Level(MemoryProbe.Read());
        var evidence = await ObserveComposerAsync(screenSnapshot, needle, visibleTailNeedle, pressure, needleBefore);
        if (evidence == ComposerEvidence.Present)
        {
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: byte-stream echo missed but the rendered screen shows the " +
                          $"typed text (len={text.Length}) - pressing Enter");
            await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat, throwWhenParked);
            ComposerRetention.Clear(backend);
            return;
        }

        if (evidence == ComposerEvidence.Unknown)
        {
            // WE CANNOT SEE, SO WE KEEP WATCHING (issue #2818). The text may be in the composer with the repaint simply
            // late, so the byte stream is watched for a further, finite budget - sized from the pressure read NOW, not
            // before the typing - without typing again and without clearing.
            var extra = echoTimeout ?? UnknownEvidenceBudget(ScaledEchoTimeout(pressure));
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: composer echo not seen (len={text.Length}) and the rendered " +
                          $"screen cannot say whether the text is there. Watching for a further {extra.TotalSeconds:F0}s. " +
                          $"{MemoryPressure.Describe(pressure)}.");
            if (await WaitForEchoAsync(buffer, cursor, needle, visibleTailNeedle, extra, poll, needleBefore: needleBefore,
                    notice: new SendWaitNotice(driverTag, "a late composer echo", $"{extra.TotalSeconds:F0}s"))
                || await ObserveComposerAsync(screenSnapshot, needle, visibleTailNeedle, pressure, needleBefore) == ComposerEvidence.Present)
            {
                FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: the text arrived while waiting - pressing Enter.");
                await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat, throwWhenParked);
                ComposerRetention.Clear(backend);
                return;
            }
        }

        if (driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
        {
            // OpenCode tears its own echo on every repaint, so the echo can never be confirmed there; the text was
            // typed once and Enter is pressed after it, in order.
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: OpenCode echo was torn; pressing Enter after the one typing");
            await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat, throwWhenParked);
            ComposerRetention.Clear(backend);
            return;
        }

        PromptDeliveryFailures.RecordComposerEchoMiss(sessionId, driverTag, 1, text.Length);
        ComposerRetention.MarkMayHoldText(backend, driverTag, text);
        var reacted = buffer.TotalBytesWritten > cursor;
        throw new ComposerNotAcceptingInputException(
            $"[{driverTag}] EchoVerifiedSubmit: the composer never echoed the typed text (screen evidence: {evidence}). " +
            "The text was typed ONCE and was NOT cleared or retyped - clearing with Escape and retyping is what doubled " +
            $"prompts (issue #3290); it may still be sitting in the composer on screen. {MemoryPressure.Describe(pressure)}. " +
            $"{EchoMissDiagnostics(buffer, cursor, screenSnapshot, needle, visibleTailNeedle)} " +
            $"Readable buffer tail: {TailOf(buffer)}")
        { TerminalReacted = reacted };
    }

    private static async Task BracketedPasteSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? enterSettleDelay = null,
        TimeSpan? submitVerifyBeat = null,
        bool throwWhenParked = true,
        Func<string?>? composerText = null)
    {
        FileLog.Write($"[{driverTag}] SharedSubmit: bracketed paste submit len={text.Length}");
        // What the composer held BEFORE the paste: a composer that already held text is not a paste taken in.
        var composerBefore = ReadComposerText(composerText);
        backend.Write(BracketedPasteStart);
        await WriteTextAsync(backend, text);
        backend.Write(BracketedPasteEnd);
        // THE PASTE IS TAKEN IN BEFORE THE ENTER (issue #3290). An agent still reading a large paste can take the Enter
        // as part of it. The agent repaints its composer once the paste is in; the Enter waits for that repaint to end.
        await WaitForPasteTakenInAsync(backend, driverTag, text.Length, composerText, composerBefore);
        await PressEnterAndVerifyAsync(
            backend, text, driverTag, enterSettleDelay ?? TimeSpan.FromMilliseconds(80), submitVerifyBeat, throwWhenParked);
    }

    private static async Task TypeSettleEnterSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? enterSettleDelay = null,
        TimeSpan? submitVerifyBeat = null,
        bool throwWhenParked = true)
    {
        FileLog.Write($"[{driverTag}] SharedSubmit: type-settle-enter submit len={text.Length}");
        await WriteTextAsync(backend, text);
        await PressEnterAndVerifyAsync(
            backend, text, driverTag, enterSettleDelay ?? TimeSpan.FromMilliseconds(50), submitVerifyBeat, throwWhenParked);
    }

    /// <summary>
    /// The large-input route. It no longer calls the submit watchdog itself: the Enter inside
    /// <see cref="EchoVerifiedInlineSubmitAsync"/> is now verified like every other Enter, so calling
    /// it again here would watch the same submit twice.
    /// </summary>
    private static async Task<string> SubmitViaAtReferenceAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null,
        TimeSpan? submitVerifyBeat = null,
        Guid sessionId = default,
        bool throwWhenParked = true)
    {
        var tempPath = LargeInputHandler.CreateTempFile(text, backend.WorkingDirectory);
        var relRef = LargeInputHandler.MakeAtReference(tempPath, backend.WorkingDirectory);
        var atReference = $"@{relRef}";
        FileLog.Write($"[{driverTag}] SharedSubmit: large input ({text.Length} chars), using temp file reference: {atReference}");

        await EchoVerifiedInlineSubmitAsync(
            backend,
            atReference,
            driverTag,
            echoTimeout,
            pollInterval,
            enterSettleDelay,
            screenSnapshot,
            submitVerifyBeat,
            sessionId,
            throwWhenParked);
        return atReference;
    }

    private static async Task<string> SubmitViaInstructionFileAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        bool bracketedPasteEnabled,
        bool requireEcho,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null,
        TimeSpan? submitVerifyBeat = null,
        Guid sessionId = default,
        bool throwWhenParked = true)
    {
        var tempPath = LargeInputHandler.CreateTempFile(text, backend.WorkingDirectory);
        var relRef = LargeInputHandler.MakeAtReference(tempPath, backend.WorkingDirectory);
        var fileName = Path.GetFileName(tempPath);
        // SHORT AND NEUTRAL (issue #3290). The instruction is typed key by key, and a Codex on a loaded machine takes
        // about eight characters a second, so the 367-character wording took most of a minute. It also ended "reply
        // with the requested strings only" - an order to the agent that every long prompt since July carried, whatever
        // the prompt asked for.
        var instruction = "The user's message for this turn is in the file " + relRef +
            " (not hidden context). Read it and act on it as the user's prompt.";
        FileLog.Write($"[{driverTag}] SharedSubmit: payload file instruction len={text.Length}, file={relRef}");

        if (requireEcho)
        {
            await EchoVerifiedInlineSubmitAsync(
                backend,
                instruction,
                driverTag,
                echoTimeout,
                pollInterval,
                enterSettleDelay,
                screenSnapshot,
                submitVerifyBeat,
                sessionId,
                throwWhenParked);
        }
        else
        {
            await TypeSettleEnterSubmitAsync(backend, instruction, driverTag, enterSettleDelay, submitVerifyBeat, throwWhenParked);
        }
        return instruction;
    }

    /// <summary>
    /// Deal with text an earlier submit may have left in this composer, BEFORE any route writes new
    /// text over it (issue #2818).
    ///
    /// Looks first rather than pressing Escape blindly: if the owner has since sent the orphan
    /// themselves, the composer now holds whatever they are typing instead, and an Escape would fall on
    /// that. Only <see cref="ComposerEvidence.Absent"/> - proof it is gone - leaves the composer alone;
    /// the reasoning for the other two branches is written on
    /// <see cref="ComposerRetention.ShouldClearBeforeTyping"/>.
    ///
    /// The pressure reading is taken HERE rather than passed in, because this runs before the route is
    /// chosen and each route reads its own.
    /// </summary>
    private static async Task ResolveRetainedComposerAsync(
        ISessionBackend backend, string driverTag, Func<string[]>? screenSnapshot,
        Func<int, byte[]?>? clearKeysFor = null, Func<bool>? composerHoldsNothing = null, bool unconditionally = false)
    {
        if (ComposerRetention.TakeRetainedText(backend) is not { } retained) return;
        // SIZED BY THE TEXT THAT WAS LEFT, not the one about to be sent (review finding 8): sixty-four Backspaces cannot
        // empty a Claude Code composer holding a 200-character line.
        var clearKeys = clearKeysFor?.Invoke(retained.Length);

        var pressure = MemoryPressure.Level(MemoryProbe.Read());
        var retainedNeedle = NormalizeForEcho(retained);
        var orphan = await ObserveComposerAsync(
            screenSnapshot, retainedNeedle, VisibleTailNeedle(retainedNeedle), pressure);

        // UNCONDITIONALLY only for the retry inside the same held-input send (issue #3290): nothing in the composer can
        // be the owner's then, and a partly echoed text does not read as "present", so the evidence rule would leave it
        // there and the retry would be appended to it.
        if ((unconditionally && clearKeys is not null) || ComposerRetention.ShouldClearBeforeTyping(orphan))
        {
            FileLog.Write($"[{driverTag}] ResolveRetainedComposer: the previous send may have left {retained.Length} " +
                          $"characters in this composer (evidence: {orphan}) - clearing before typing so the two " +
                          "cannot run together.");
            await WaitForQuietAsync(backend, driverTag);
            // THE MEASURED KEY, NOT A GUESSED ONE (issue #3290). A single Escape empties neither Claude Code's nor
            // Codex's composer, so this step used to leave the orphan in place and the new send was appended to it -
            // two prompts submitted as one. The caller passes the keys measured for its agent.
            backend.Write(clearKeys ?? EscapeByte);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            if (composerHoldsNothing is not null)
            {
                // A loaded machine takes seconds to act on the clear, so the check runs for up to half a minute
                // (review note) rather than five seconds that turned a slow clear into a failed send.
                var empty = false;
                var notice = new SendWaitNotice(driverTag, "the composer to read empty after the clear", "100 looks, about 30s");
                for (var i = 0; i < 100 && !empty; i++)
                {
                    notice.Check();
                    empty = composerHoldsNothing();
                    if (empty) { await Task.Delay(BetweenScreenSamples); empty = composerHoldsNothing(); }
                    if (!empty) await Task.Delay(TimeSpan.FromMilliseconds(150));
                }
                notice.End(empty ? "the composer is empty" : "the composer still holds text - nothing is typed");
                if (!empty)
                {
                    ComposerRetention.MarkMayHoldText(backend, driverTag, retained);
                    throw new ComposerNotAcceptingInputException(
                        $"[{driverTag}] ResolveRetainedComposer: the composer still holds text after it was cleared, so " +
                        "nothing was typed - typing now would run the new text together with what is there.");
                }
            }
        }
        else
        {
            FileLog.Write($"[{driverTag}] ResolveRetainedComposer: the previous send's text is provably gone from " +
                          "this composer - not clearing, so nothing typed since is disturbed.");
        }
    }

    /// <summary>
    /// Before a clear: wait until the terminal has printed nothing for <see cref="QuietBeforeClear"/>, up to
    /// <see cref="QuietBeforeClearLimit"/> (issue #3290). Characters typed earlier can still be on their way into the
    /// composer - measured on Codex, which went on taking an instruction after the send had given up on it - and a clear
    /// pressed while they arrive empties only what had arrived so far, leaving the rest to run into the next prompt.
    /// </summary>
    private static async Task WaitForQuietAsync(ISessionBackend backend, string driverTag)
    {
        if (backend.Buffer is not { } buffer) return;
        var notice = new SendWaitNotice(driverTag, $"the terminal to stop printing for {QuietBeforeClear.TotalSeconds:F0}s before a clear",
            $"{QuietBeforeClearLimit.TotalSeconds:F0}s");
        var started = DateTime.UtcNow;
        var lastTotal = buffer.TotalBytesWritten;
        var lastChange = started;
        while (DateTime.UtcNow - lastChange < QuietBeforeClear)
        {
            notice.Check();
            if (DateTime.UtcNow - started >= QuietBeforeClearLimit)
            {
                FileLog.Write($"[{driverTag}] ResolveRetainedComposer: the terminal was still printing after " +
                              $"{QuietBeforeClearLimit.TotalSeconds:F0}s - clearing anyway, and checking the composer after.");
                notice.End("the limit was reached - clearing anyway");
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var total = buffer.TotalBytesWritten;
            if (total != lastTotal) { lastTotal = total; lastChange = DateTime.UtcNow; }
        }
        notice.End("the terminal went quiet");
    }

    /// <summary>
    /// Wait until the agent has taken in a bracketed paste, so the Enter that follows submits it rather than landing
    /// inside it. Either of two signs ends the wait:
    ///  - THE COMPOSER SHOWS THE PASTE AND HAS STOPPED CHANGING, where the caller can read the composer
    ///    (<paramref name="composerText"/>). This is the sign for an agent that is working (Voice Delivery mission,
    ///    phase 3): on 25 September 2026 at 09:05 a working Claude Code redrew its spinner without a break, so the
    ///    terminal never went quiet and a spoken prompt waited the whole two-minute limit before its Enter, while the
    ///    composer had shown "[Pasted text ...]" all along. The spinner is not the composer: only the composer's own
    ///    text is watched, and it must differ from what it held before the paste and hold still for
    ///    <see cref="PasteQuiet"/>.
    ///  - THE TERMINAL REPAINTED SINCE THE PASTE AND THEN WENT QUIET for <see cref="PasteQuiet"/> - the sign for an
    ///    agent whose composer cannot be read.
    /// An agent that shows neither has not read the paste yet (measured on Pi under full load: thirty seconds without a
    /// byte), so it is waited for, up to <see cref="PasteTakenInLimit"/>, and the wait says so in the log.
    /// </summary>
    private static async Task WaitForPasteTakenInAsync(
        ISessionBackend backend, string driverTag, int length, Func<string?>? composerText = null, string? composerBefore = null)
    {
        if (backend.Buffer is not { } buffer) return;
        var notice = new SendWaitNotice(driverTag, $"the agent to take in a {length}-character paste " +
            (composerText is null ? "(a repaint followed by a quiet terminal)" : "(the composer showing it, or a quiet terminal)"),
            $"{PasteTakenInLimit.TotalSeconds:F0}s");
        var started = DateTime.UtcNow;
        var startTotal = buffer.TotalBytesWritten;
        var lastTotal = startTotal;
        var lastChange = started;
        var lastLook = DateTime.MinValue;
        string? shown = null;
        var shownSince = started;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            notice.Check();
            var now = DateTime.UtcNow;
            var total = buffer.TotalBytesWritten;
            if (total != lastTotal) { lastTotal = total; lastChange = now; }
            if (total != startTotal && now - lastChange >= PasteQuiet)
            {
                notice.End("the terminal repainted and went quiet - pressing Enter");
                return;
            }
            if (composerText is not null && now - lastLook >= ComposerLookInterval)
            {
                lastLook = now;
                var current = ReadComposerText(composerText);
                if (!string.Equals(current, shown, StringComparison.Ordinal)) { shown = current; shownSince = now; }
                else if (current is not null && !string.Equals(current, composerBefore, StringComparison.Ordinal)
                         && now - shownSince >= PasteQuiet)
                {
                    var label = current.Length > 60 ? current[..60] + "..." : current;
                    FileLog.Write($"[{driverTag}] SharedSubmit: the composer shows the paste (\"{label.Replace('\n', ' ')}\") after " +
                                  $"{(now - started).TotalSeconds:F1}s - taken in, pressing Enter");
                    notice.End("the composer shows the paste - pressing Enter");
                    return;
                }
            }
            if (now - started >= PasteTakenInLimit)
            {
                FileLog.Write($"[{driverTag}] SharedSubmit: no quiet repaint within {PasteTakenInLimit.TotalSeconds:F0}s of a " +
                              $"{length}-character paste (repainted={total != startTotal}) - pressing Enter; the records decide");
                notice.End("the limit was reached - pressing Enter");
                return;
            }
        }
    }

    /// <summary>The composer's text, or null when it holds nothing, cannot be read, or there is no reader.</summary>
    private static string? ReadComposerText(Func<string?>? composerText) => composerText?.Invoke();

    internal static readonly TimeSpan PasteQuiet = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan PasteTakenInLimit = TimeSpan.FromSeconds(120);

    /// <summary>How often the composer is read while a paste is taken in. A read renders the screen, so not every poll.</summary>
    private static readonly TimeSpan ComposerLookInterval = TimeSpan.FromMilliseconds(200);

    internal static readonly TimeSpan QuietBeforeClear = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan QuietBeforeClearLimit = TimeSpan.FromSeconds(15);

    /// <summary>The composer echo deadline on a machine that is not short of memory. Unchanged at four seconds.</summary>
    internal static readonly TimeSpan BaseEchoTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The memory probe every submit consults.
    ///
    /// PUBLIC BECAUSE EVERY TEST ASSEMBLY THAT DRIVES A SUBMIT MUST BE ABLE TO PIN IT, and that is a
    /// cost this change introduced rather than a convenience. Reading the machine put ambient state on a
    /// path used by every driver, so any test anywhere that reaches a submit now depends on how much
    /// memory the build agent happens to have free. It was found the honest way: a suite in a DIFFERENT
    /// assembly - CcDirector.HostedAgent.Tests - failed on a laptop that had drifted into Tight while
    /// passing on the same code minutes earlier. Non-determinism is worse than a consistent failure,
    /// because it is the kind of red that gets re-run rather than read.
    ///
    /// Making it internal would have left that assembly unable to protect itself: it has no
    /// InternalsVisibleTo from Core and no direct reference to it. The precedent for a seam like this in
    /// product code is <c>PromptDeliveryFailures.ResetForTests</c>.
    ///
    /// THE DIRECTOR NEVER SETS THIS. Only tests do, and they restore it - see PinnedMachineMemory.
    /// </summary>
    public static IMachineMemoryProbe MemoryProbe { get; set; } = MachineMemoryProbe.Shared;

    /// <summary>
    /// The composer echo deadline for a measured pressure level. Exactly
    /// <see cref="BaseEchoTimeout"/> on a healthy machine and on one whose memory could not be read -
    /// this must never change a deadline off an absent measurement.
    /// </summary>
    internal static TimeSpan ScaledEchoTimeout(MemoryPressureLevel pressure) =>
        BaseEchoTimeout * MemoryPressure.DeadlineMultiplier(pressure);

    /// <summary>
    /// How long to keep WATCHING a composer we cannot see into, after the echo deadline has passed,
    /// before giving up without clearing it.
    ///
    /// Finite by design. The old code had no such state - it cleared and retyped immediately - and an
    /// unbounded wait would simply move the harm from "your words were deleted" to "your send never
    /// returns". One further deadline's worth is enough for a repaint that is late rather than absent.
    /// </summary>
    internal static TimeSpan UnknownEvidenceBudget(TimeSpan echoTimeout) => echoTimeout;

    /// <summary>
    /// How long to leave between the two screen samples that <see cref="ObserveComposerAsync"/> takes.
    /// </summary>
    private static readonly TimeSpan BetweenScreenSamples = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// LOOK AT THE COMPOSER AND SAY WHAT IS KNOWN - the three-valued replacement for the boolean
    /// the old boolean screen check (issue #2818).
    ///
    /// Two samples, not one. A single negative reading is not proof of absence, because the rows can be
    /// captured mid-repaint: the existing code already records that disease for the byte stream and for
    /// the screen (issue #1592). Two consecutive readings that both render something and neither shows
    /// the text is the evidence a destructive Escape needs; anything less is <see cref="ComposerEvidence.Unknown"/>.
    ///
    /// A screen that renders NOTHING is Unknown, never Absent. An empty capture is a broken instrument,
    /// not a clean reading, and treating it as proof the composer is empty is exactly the mistake that
    /// deleted the owner's words.
    /// </summary>
    private static async Task<ComposerEvidence> ObserveComposerAsync(
        Func<string[]>? screenSnapshot, string needle, string? visibleTailNeedle, MemoryPressureLevel pressure,
        int needleBefore = 0)
    {
        if (screenSnapshot is null || needle.Length == 0) return ComposerEvidence.Unknown;

        var first = ReadScreen(screenSnapshot);
        if (first is null) return ComposerEvidence.Unknown;
        if (ScreenRowsShowText(first, needle, visibleTailNeedle, needleBefore)) return ComposerEvidence.Present;

        await Task.Delay(BetweenScreenSamples);

        var second = ReadScreen(screenSnapshot);
        if (second is null) return ComposerEvidence.Unknown;
        if (ScreenRowsShowText(second, needle, visibleTailNeedle, needleBefore)) return ComposerEvidence.Present;

        // TWO NEGATIVE SAMPLES ARE NOT PROOF OF ABSENCE ON A STARVED MACHINE, and this is the sharpest
        // point the design review made. The samples are 120 milliseconds apart; a machine that is paging
        // can leave the renderer stalled for far longer than that, so both captures can be the SAME
        // stale frame taken from a screen that has not repainted since before the text was typed.
        // Concluding "absent" from that and pressing Escape would be the original defect wearing the
        // disguise of evidence.
        //
        // So the destructive verdict is refused outright whenever memory pressure is measurable. On a
        // healthy machine a stalled renderer is not the explanation and two negatives mean what they
        // say; on a starved one we say Unknown and keep the text. The cost is that a genuinely stuck
        // interface on a starved machine is not recovered by retyping - it is reported instead, with the
        // owner's words left on screen, which is the right way round.
        if (MemoryPressure.IsUnderPressure(pressure)) return ComposerEvidence.Unknown;

        return ComposerEvidence.Absent;
    }

    /// <summary>
    /// One screen capture, or null when there is nothing to read. A snapshot that throws, returns no
    /// rows, or renders only blank space is null: the instrument did not answer, and an unanswered
    /// question must not be recorded as a "no".
    /// </summary>
    private static string[]? ReadScreen(Func<string[]> screenSnapshot)
    {
        string[] rows;
        try
        {
            rows = screenSnapshot();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalSubmit] ReadScreen FAILED, treating the composer as unreadable: {ex.Message}");
            return null;
        }

        if (rows is null || rows.Length == 0) return null;
        return rows.All(string.IsNullOrWhiteSpace) ? null : rows;
    }

    /// <summary>Does this captured screen show the text? Split out so one capture can be asked twice.</summary>
    private static bool ScreenRowsShowText(string[] rows, string needle, string? visibleTailNeedle, int needleBefore = 0)
    {
        if (needle.Length == 0) return false;

        var hay = NormalizeForEcho(string.Concat(rows));
        if (needleBefore > 0)
            return CountIn(hay, needle) > needleBefore;
        if (hay.Contains(needle, StringComparison.Ordinal))
            return true;

        // The rendered screen has the same disease as the byte stream from the other end: rows are
        // concatenated without separators and can be snapshotted mid-paint, so a footer hint lands
        // inside the typed text here too (issue #1592). Same question, same answer.
        if (IndexOfInterleaved(hay, needle) >= 0)
            return true;

        return visibleTailNeedle is not null && hay.Contains(visibleTailNeedle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The longest single line typed into Claude Code key by key; a longer one goes as a bracketed paste when the
    /// agent has turned paste mode on (issue #3290).
    ///
    /// Measured on 24 September 2026, three agents running at once on a machine short of memory: an 869-character
    /// line typed into Claude Code arrived in bursts that Claude Code's own paste detector folded into
    /// "[Pasted text #1]" part-way through. The composer never showed the whole line, the send was cleared and typed
    /// again after 48 seconds, and characters of the first typing still on their way were run into the NEXT prompt -
    /// three later prompts arrived with pieces of it. A bracketed paste says "this is one paste" in the terminal's own
    /// protocol instead of leaving the agent to guess from the timing, and the conversation records prove it arrived.
    /// </summary>
    internal const int ClaudeTypingLimit = 200;

    private static bool ShouldPasteRatherThanType(string driverTag, string text) =>
        text.Length > ClaudeTypingLimit
        && (driverTag.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            // Pi too (issue #3290, measured 24 September 2026 with every core busy): an 869-character line typed into Pi
            // was not drawn for over twenty seconds, while a 292-character paste was taken in within twelve.
            || driverTag.Equals("Pi", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The longest text pasted into Claude Code or Pi. Longer goes through a file - an @-reference for Claude Code, the
    /// instruction line for Pi, whose @ only searches file names - because with every core busy neither took in an 8 KB
    /// paste in time (issue #3290, measured 24 September 2026: Claude Code not at all, Pi after the three-minute window).
    /// </summary>
    internal const int PasteLimit = 2000;

    private static bool ShouldReferenceRatherThanPaste(string driverTag, string text) =>
        text.Length > PasteLimit && driverTag.Contains("Claude", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseInstructionFile(string driverTag, string text)
    {
        if (!LargeInputHandler.IsLargeInput(text) && text.Length <= 300)
            return false;

        if (text.Length > PasteLimit && driverTag.Equals("Pi", StringComparison.OrdinalIgnoreCase))
            return true;

        return driverTag.Contains("Codex", StringComparison.OrdinalIgnoreCase)
               || driverTag.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
               || driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The longest a send waits for its echo while the terminal is still visibly working on it (issue #3290).
    /// Measured on 24 September 2026: Codex, while its header still said "loading", took typed characters at about
    /// one every 1.5 seconds, and Pi at startup echoed a long line for well over four seconds. A fixed four-second
    /// deadline called both "not accepting input" and cleared-and-retyped a composer that was filling up, which
    /// doubled the prompt. The deadline now runs from the LAST sign of progress, up to this limit.
    /// </summary>
    internal static readonly TimeSpan EchoProgressCap = TimeSpan.FromSeconds(120);

    /// <summary>How long a composer that has visibly begun taking the text may pause before the echo is called missing.</summary>
    internal static readonly TimeSpan PrefixQuietAllowance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a terminal that has printed nothing at all since the typing is waited for. Waiting costs nothing - the
    /// keystrokes queue in the pipe - and with every core busy Pi printed nothing for over twenty seconds (issue #3290).
    /// </summary>
    internal static readonly TimeSpan NoReactionAllowance = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ScreenProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How much of the start of <paramref name="needle"/> the rendered screen shows, in normalized characters - 0 when
    /// the screen cannot be read. Containment of a prefix is monotone (a screen showing a longer prefix shows every
    /// shorter one), so the length is found by bisection.
    /// </summary>
    internal static int ScreenPrefixLength(Func<string[]>? screenSnapshot, string needle)
    {
        if (screenSnapshot is null || needle.Length == 0) return 0;
        var rows = ReadScreen(screenSnapshot);
        if (rows is null) return 0;
        return PrefixLengthIn(NormalizeForEcho(string.Concat(rows)), needle);
    }

    /// <summary>How many times <paramref name="needle"/> occurs in <paramref name="hay"/>, without overlap.</summary>
    internal static int CountIn(string hay, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        for (var at = hay.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = hay.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    internal static int PrefixLengthIn(string hay, string needle)
    {
        int lo = 0, hi = needle.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (hay.Contains(needle.AsSpan(0, mid), StringComparison.Ordinal)) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Poll the terminal byte stream until the typed text echoes back in the composer. The wait ends
    /// <paramref name="timeout"/> after the terminal last printed anything, and never later than
    /// <paramref name="hardCap"/> after it began: a composer still repainting our characters is given the time it
    /// needs, and a silent one is given up on as before.
    /// </summary>
    private static async Task<bool> WaitForEchoAsync(
        CircularTerminalBuffer buffer, long cursor, string needle, string? visibleTailNeedle, TimeSpan timeout, TimeSpan poll,
        TimeSpan? hardCap = null, Func<string[]>? screenSnapshot = null, int prefixBefore = 0, int needleBefore = 0,
        SendWaitNotice? notice = null)
    {
        var found = await WaitForEchoCoreAsync(buffer, cursor, needle, visibleTailNeedle, timeout, poll, hardCap, screenSnapshot,
            prefixBefore, needleBefore, notice);
        notice?.End(found ? "the composer echoed the text" : "no echo - the text is not proven to be in the composer");
        return found;
    }

    private static async Task<bool> WaitForEchoCoreAsync(
        CircularTerminalBuffer buffer, long cursor, string needle, string? visibleTailNeedle, TimeSpan timeout, TimeSpan poll,
        TimeSpan? hardCap, Func<string[]>? screenSnapshot, int prefixBefore, int needleBefore, SendWaitNotice? notice)
    {
        var started = DateTime.UtcNow;
        var cap = hardCap ?? timeout;
        var lastProgress = started;
        var lastTotal = buffer.TotalBytesWritten;
        var lastPrefix = prefixBefore;
        var lastScreenLook = DateTime.MinValue;
        var quiet = timeout;
        while (true)
        {
            notice?.Check();
            var now = DateTime.UtcNow;
            var total = buffer.TotalBytesWritten;
            if (total != lastTotal) { lastTotal = total; lastProgress = now; }

            // THE COMPOSER FILLING UP IS PROGRESS EVEN WHEN THE TERMINAL IS QUIET (issue #3290). Measured on 24
            // September 2026: Codex, typed to the moment its previous turn ended, printed nothing for nine seconds and
            // then went on taking the characters - the byte stream called that a miss, and the clear that followed
            // raced the characters still arriving. Once the screen shows more of this text than it did before the
            // typing, the agent has begun taking it: every gain restarts the deadline, and a pause is allowed
            // PrefixQuietAllowance rather than the few seconds allowed a terminal that never reacted at all.
            if (screenSnapshot is not null && hardCap is not null && now - lastScreenLook >= ScreenProgressInterval)
            {
                lastScreenLook = now;
                var prefix = ScreenPrefixLength(screenSnapshot, needle);
                if (prefix > lastPrefix)
                {
                    lastPrefix = prefix;
                    lastProgress = now;
                    if (quiet < PrefixQuietAllowance) quiet = PrefixQuietAllowance;
                }
            }
            // A TERMINAL THAT HAS NOT REACTED AT ALL HAS NOT REFUSED ANYTHING (issue #3290). Measured on 24 September 2026:
            // Pi, typed to just after its previous turn ended, printed not one byte for six seconds and then took the
            // whole line. The keystrokes wait in the pipe; four seconds of silence called that a miss, and the clear and
            // retype that followed ran into the characters as Pi caught up.
            var timeoutNow = hardCap is not null && total == cursor && quiet < NoReactionAllowance ? NoReactionAllowance : quiet;

            // The same text already on screen: a repaint can re-send the old copy, so only the screen counting a NEW
            // copy is an echo.
            if (needleBefore > 0)
            {
                if (screenSnapshot is not null && ReadScreen(screenSnapshot) is { } rowsNow
                    && CountIn(NormalizeForEcho(string.Concat(rowsNow)), needle) > needleBefore)
                    return true;
                if (now - lastProgress >= timeoutNow || now - started >= cap) return false;
                await Task.Delay(poll);
                continue;
            }

            var (bytes, _) = buffer.GetWrittenSince(cursor);
            var hay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
            var index = hay.LastIndexOf(needle, StringComparison.Ordinal);

            // The contiguous run is the common case. When it misses, the echo may still be sitting in
            // the composer with a footer repaint spliced through it (issue #1592) - so ask whether our
            // characters are all present, in order, and densely packed before giving up on them.
            if (index < 0)
                index = IndexOfInterleaved(hay, needle);

            if (index >= 0)
            {
                // A leading slash means the TUI read our text as a slash COMMAND, not as data.
                // Pressing Enter there would execute it, so this is never an echo we accept.
                if (index > 0 && hay[index - 1] == '/' && !needle.StartsWith('/'))
                {
                    if (now - lastProgress >= timeoutNow || now - started >= cap) return false;
                    await Task.Delay(poll);
                    continue;
                }

                return true;
            }

            if (visibleTailNeedle is not null && hay.Contains(visibleTailNeedle, StringComparison.Ordinal))
                return true;

            if (now - lastProgress >= timeoutNow || now - started >= cap) return false;
            await Task.Delay(poll);
        }
    }

    private static async Task WriteTextAsync(ISessionBackend backend, string text)
    {
        var asKeyEvents = backend is ConPtyBackend;
        const int bulkThreshold = 48;
        if (Encoding.UTF8.GetByteCount(text) <= bulkThreshold)
        {
            backend.Write(Encode(text, asKeyEvents));
            return;
        }

        foreach (var chunk in Chunks(text, asKeyEvents))
        {
            backend.Write(chunk);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>
    /// The bytes that put <paramref name="text"/> into an agent's composer. With <paramref name="nonAsciiAsKeyEvents"/>
    /// (a Windows pseudo console) every character outside ASCII is written as a win32-input-mode key press - the form
    /// Windows Terminal itself uses to hand the pseudo console a keystroke - and ASCII is written as it is.
    ///
    /// WHY (issue #3290), measured on 24 September 2026 with the qualification rig (`--chars`): written as UTF-8, the
    /// text "e\u00e9 d\u2013 q\u201cx\u201d p\u00a3 u\u20ac" reached Codex and Grok as "e\u00e9 d qx p u" - the pseudo
    /// console hands those two agents each UTF-8 byte as a character of its own, so Codex RECORDED "Caf\u00e9" as
    /// "Caf\u00c3\u00a9" and the dashes, curly quotes, pound and euro vanished. One write, a write per character and a
    /// bracketed paste all lost them the same way. As key presses the text reached Claude Code, Codex, Pi, OpenCode and
    /// Grok intact, and inside a bracketed paste Claude Code still received it as one two-line paste. Dictation
    /// produces curly quotes and dashes all the time, so this is not an edge case.
    /// </summary>
    internal static byte[] Encode(string text, bool nonAsciiAsKeyEvents)
    {
        if (!nonAsciiAsKeyEvents || text.All(c => c < 0x80))
            return Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder(text.Length + 64);
        foreach (var c in text)
        {
            if (c < 0x80)
            {
                sb.Append(c);
                continue;
            }
            // CSI Vk;Sc;Uc;Kd;Cs;Rc _ : no virtual key, no scan code, the UTF-16 unit, key down then key up, once.
            // A character outside the basic plane is sent as its two surrogate units, as Windows Terminal sends it.
            sb.Append("\u001b[0;0;").Append((int)c).Append(";1;0;1_");
            sb.Append("\u001b[0;0;").Append((int)c).Append(";0;0;1_");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The text as chunks of about sixteen characters' bytes that NEVER split a character - a character, and a
    /// character sent as key presses, always travels in one write.
    /// </summary>
    internal static IEnumerable<byte[]> Chunks(string text, bool nonAsciiAsKeyEvents = false, int chunkSize = 16)
    {
        var pending = new List<byte>(chunkSize + 64);
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = Encode((string)e.Current, nonAsciiAsKeyEvents);
            if (pending.Count > 0 && pending.Count + element.Length > chunkSize)
            {
                yield return pending.ToArray();
                pending.Clear();
            }
            pending.AddRange(element);
        }
        if (pending.Count > 0) yield return pending.ToArray();
    }

    /// <summary>
    /// Codex horizontally scrolls its composer for longer inputs, so the terminal byte stream can
    /// repaint only the visible tail. A long fresh tail is still proof that this exact write reached
    /// the composer because the search is scoped to bytes emitted after the write cursor.
    /// </summary>
    private static string? VisibleTailNeedle(string needle)
    {
        const int tailLength = 16;
        return needle.Length > tailLength ? needle[^tailLength..] : null;
    }

    /// <summary>
    /// The last 500 readable characters of the buffer, for diagnostic log lines. Reads only the
    /// last 64 kilobytes rather than the whole ring: across 60 real session logs of at least
    /// 600 kilobytes, a 64 kilobyte tail gave the identical 500 characters every time.
    /// </summary>
    private static string TailOf(CircularTerminalBuffer buffer)
    {
        const int tailBytes = 64 * 1024;
        var text = NormalizeWhitespace(StripAnsi(Encoding.UTF8.GetString(buffer.DumpTail(tailBytes))));
        const int maxChars = 500;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    /// <summary>
    /// Compact evidence for a missed composer echo, for post-mortem troubleshooting of a false
    /// "composer not accepting input" (issue #1493). Reports what the submit was looking for (the
    /// full needle and the visible-tail needle) and what each witness actually held - the raw byte
    /// stream written since this attempt's cursor, and the rendered screen grid - each normalized to
    /// the echo alphabet and tail-bounded. Last time this fired the log carried only the byte tail,
    /// which could not show why the rendered-screen fallback ALSO missed; the screen fields close
    /// that gap. Distinguishes "text present but full needle scrolled off" (HasTail true, HasNeedle
    /// false) from "text genuinely not on the grid" (both false) from "grid empty" (screenLen 0).
    /// </summary>
    private static string EchoMissDiagnostics(
        CircularTerminalBuffer buffer,
        long cursor,
        Func<string[]>? screenSnapshot,
        string needle,
        string? visibleTailNeedle)
    {
        const int tailChars = 400;

        var (bytes, _) = buffer.GetWrittenSince(cursor);
        var byteHay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
        var byteHasNeedle = byteHay.Contains(needle, StringComparison.Ordinal);
        var byteHasTail = visibleTailNeedle is not null && byteHay.Contains(visibleTailNeedle, StringComparison.Ordinal);

        string screenInfo;
        // A snapshot that throws must not escape from here. This is DIAGNOSTICS - it runs on the way to
        // reporting a failure, and letting it replace that failure with an unrelated exception loses the
        // report entirely. Found by the evidence tests for issue #2818: a renderer that threw turned a
        // ComposerNotAcceptingInputException into an InvalidOperationException from inside the log line.
        var rows = screenSnapshot is null ? null : ReadScreen(screenSnapshot);
        if (rows is null)
        {
            screenInfo = screenSnapshot is null
                ? "screen=<no snapshot supplied by driver>"
                : "screen=<the snapshot could not be read>";
        }
        else
        {
            var screenHay = NormalizeForEcho(string.Concat(rows));
            var screenHasNeedle = screenHay.Contains(needle, StringComparison.Ordinal);
            var screenHasTail = visibleTailNeedle is not null && screenHay.Contains(visibleTailNeedle, StringComparison.Ordinal);
            screenInfo =
                $"screenRows={rows.Length}, screenLen={screenHay.Length}, " +
                $"screenHasNeedle={screenHasNeedle}, screenHasTail={screenHasTail}, " +
                $"screenTail=\"{TailBounded(screenHay, tailChars)}\"";
        }

        return
            $"[echo-miss] needleLen={needle.Length}, tailNeedle=\"{visibleTailNeedle}\", " +
            $"byteLen={byteHay.Length}, byteHasNeedle={byteHasNeedle}, byteHasTail={byteHasTail}, " +
            $"byteTail=\"{TailBounded(byteHay, tailChars)}\", {screenInfo}";
    }

    private static string TailBounded(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];

    private static string NormalizeWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasWhitespace)
                    sb.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            sb.Append(c);
            previousWasWhitespace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Drop ANSI escape sequences (CSI / OSC / two-byte) from a terminal chunk.</summary>
    public static string StripAnsi(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\x1B')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= raw.Length) break;
            var kind = raw[i + 1];
            if (kind == '[')
            {
                i += 2;
                while (i < raw.Length && (raw[i] < '\x40' || raw[i] > '\x7E')) i++;
            }
            else if (kind == ']')
            {
                i += 2;
                while (i < raw.Length && raw[i] != '\a' && raw[i] != '\x1B') i++;
                if (i + 1 < raw.Length && raw[i] == '\x1B') i++;
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Shortest needle we will match as an interleaved subsequence. Below this a coincidental
    /// in-order match is plausible, so short prompts keep the strict contiguous rule.
    /// </summary>
    internal const int MinInterleavedNeedleLength = 40;

    /// <summary>
    /// How far the needle's characters may be spread before we stop believing they are OUR echo.
    /// A repaint inserts a bounded amount of foreign text (a footer hint is tens of characters), so
    /// a real interleaved echo stays dense. A coincidental in-order match is scattered across
    /// thousands of unrelated characters and blows this budget.
    /// </summary>
    internal const int MaxInterleavedStretch = 3;

    /// <summary>
    /// Find <paramref name="needle"/> in <paramref name="hay"/> as an ORDERED, DENSE subsequence,
    /// returning the start index or -1.
    ///
    /// WHY THIS EXISTS (issue #1592). A TUI paints its footer by moving the cursor out of the
    /// composer, writing the hint, and moving back. <see cref="StripAnsi"/> then discards those
    /// cursor moves - the only thing that said the hint belongs ELSEWHERE on screen - so the hint is
    /// spliced into the middle of the typed text: "...and not grea[bypass permissions on shift+tab to
    /// cycle]t for us...". A contiguous search over that flattened stream can never match, so the
    /// echo check declared the composer dead and threw away two phone dictations on 2026-07-15.
    ///
    /// The insight is that a repaint only ever INSERTS characters. It never removes ours and never
    /// reorders them. So "all my characters, in order, densely packed" is exactly a repaint-torn echo,
    /// while genuinely partial text (the tail without the head) and a slash-corrupted echo are still
    /// missing or misplacing characters and still fail - which is what keeps the safety properties
    /// this class already had.
    /// </summary>
    internal static int IndexOfInterleaved(string hay, string needle)
    {
        if (needle.Length < MinInterleavedNeedleLength || hay.Length < needle.Length)
            return -1;

        var budget = needle.Length * MaxInterleavedStretch;
        for (var start = 0; start + needle.Length <= hay.Length; start++)
        {
            if (hay[start] != needle[0])
                continue;

            var limit = Math.Min(hay.Length, start + budget);
            var n = 0;
            for (var i = start; i < limit && n < needle.Length; i++)
                if (hay[i] == needle[n])
                    n++;

            if (n == needle.Length)
                return start;
        }
        return -1;
    }

    /// <summary>Letters, digits and '/' only - the comparison alphabet for composer echo checks.</summary>
    public static string NormalizeForEcho(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '/')
                sb.Append(c);
        return sb.ToString();
    }
}
