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
        TimeSpan? submitVerifyBeat)
    {
        await Task.Delay(settle);
        await SubmitVerifier.PressEnterAndVerifyAsync(
            backend.Buffer, backend.Write, LabelFor(text, driverTag), submitVerifyBeat);
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
    public static async Task SharedSubmitAsync(
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
        Guid sessionId = default)
    {
        ArgumentNullException.ThrowIfNull(backend);

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
        await ResolveRetainedComposerAsync(backend, driverTag, screenSnapshot);

        var textForCheck = text.TrimEnd('\r', '\n');
        if (ShouldUseInstructionFile(driverTag, textForCheck)
            && !string.IsNullOrWhiteSpace(backend.WorkingDirectory))
        {
            await SubmitViaInstructionFileAsync(
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
                sessionId);
            ComposerRetention.Clear(backend);
            return;
        }

        if (LargeInputHandler.IsLargeInput(textForCheck))
        {
            if (bracketedPasteEnabled)
            {
                await BracketedPasteSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay, submitVerifyBeat);
                ComposerRetention.Clear(backend);
                return;
            }

            if (!string.IsNullOrWhiteSpace(backend.WorkingDirectory))
            {
                await SubmitViaAtReferenceAsync(backend, textForCheck, driverTag, echoTimeout, pollInterval, enterSettleDelay, screenSnapshot, submitVerifyBeat, sessionId);
                ComposerRetention.Clear(backend);
                return;
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
                sessionId);
        }
        else
        {
            await TypeSettleEnterSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay, submitVerifyBeat);
        }

        // Every route that RETURNS has submitted; only a throw leaves a mark standing. Clearing here as
        // well as on each early return means no route can quietly keep a stale mark alive and make the
        // NEXT send press Escape over text that belongs to the owner.
        ComposerRetention.Clear(backend);
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
        Func<TimeSpan, Task>? pause = null)
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
        Guid sessionId = default)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var buffer = backend.Buffer;
        if (buffer is null)
        {
            backend.Write(Encoding.UTF8.GetBytes(text));
            await PressEnterAndVerifyAsync(
                backend, text, driverTag, enterSettleDelay ?? TimeSpan.FromMilliseconds(50), submitVerifyBeat);
            return;
        }

        // THE DEADLINE IS MEASURED, NOT ASSUMED (issue #2818). Four seconds is right on a machine that
        // can run; on one that is paging, the agent's terminal interface genuinely cannot repaint in
        // four seconds, and treating that as "not accepting input" is what deleted the owner's typed
        // sentences. A caller that passed an explicit timeout still wins - tests and drivers that have
        // chosen a value keep it - so nothing changes anywhere until a reading proves it should.
        var pressure = MemoryPressure.Level(MemoryProbe.Read());
        var to = echoTimeout ?? ScaledEchoTimeout(pressure);
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var settle = enterSettleDelay ?? TimeSpan.FromMilliseconds(40);
        var needle = NormalizeForEcho(text);
        var visibleTailNeedle = VisibleTailNeedle(needle);

        if (echoTimeout is null && MemoryPressure.IsUnderPressure(pressure))
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: {MemoryPressure.Describe(pressure)}, so the composer " +
                          $"echo deadline is {to.TotalSeconds:F0}s instead of {BaseEchoTimeout.TotalSeconds:F0}s. " +
                          "A slow repaint is not a stuck interface.");

        var cursor = buffer.TotalBytesWritten;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // A FRESH READING PER ATTEMPT, so attempt two is not waiting to a deadline sized for the
            // machine as it was before attempt one. The caller's explicit timeout still wins.
            pressure = MemoryPressure.Level(MemoryProbe.Read());
            to = echoTimeout ?? ScaledEchoTimeout(pressure);

            cursor = buffer.TotalBytesWritten;
            await WriteTextAsync(backend, text);

            if (needle.Length == 0 || await WaitForEchoAsync(buffer, cursor, needle, visibleTailNeedle, to, poll))
            {
                await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat);
                ComposerRetention.Clear(backend);
                return;
            }

            // The byte stream missed the echo, but that stream is a poor witness for input that
            // WRAPPED across composer rows: the TUI repaints wrapped text interleaved with box
            // borders, footer hints ("esc again to clear") and cursor moves, so the typed text may
            // never appear in the bytes as one contiguous run even though it is sitting in the
            // composer. Ask the rendered screen - the final visual state, free of interleaving -
            // before treating the attempt as a failure and disturbing the composer with Escape.
            //
            // The answer is THREE-VALUED (see ComposerEvidence). This used to be a bool whose false
            // meant both "the screen does not show it" and "there is no screen to look at", and the
            // Escape below fired on either. Only Absent - we looked, and it is genuinely not there -
            // now licenses a destructive step.
            // RE-READ THE MACHINE AT THE MOMENT THE DEADLINE EXPIRES, NOT ONCE BEFORE TYPING.
            //
            // The reading taken before the write describes the machine as it was up to four seconds
            // ago, and the code review proved what that costs: a machine with room when the send began
            // and short of memory by the time the deadline passed used the STALE "Normal" reading,
            // pressed Escape twice, deleted the prompt, and reported that the machine had memory to
            // spare. The transition INTO pressure is the common case, not an edge one - it is what a
            // session starting up on a loaded Director looks like - so the destructive decision below
            // must be taken on what is true now.
            pressure = MemoryPressure.Level(MemoryProbe.Read());

            var evidence = await ObserveComposerAsync(screenSnapshot, needle, visibleTailNeedle, pressure);
            if (evidence == ComposerEvidence.Present)
            {
                FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: byte-stream echo missed on attempt {attempt} " +
                              $"but the rendered screen shows the typed text (len={text.Length}) - pressing Enter");
                await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat);
                ComposerRetention.Clear(backend);
                return;
            }

            // THE NEW BEHAVIOUR IS SCOPED TO A MEASURABLY STARVED MACHINE, AND THAT SCOPE IS DELIBERATE.
            //
            // An earlier draft took the preserve-and-watch path on ANY unknown evidence. That silently
            // removed the clear-and-retype recovery from every driver call site that passes no screen
            // snapshot - which is most of them (ClaudeDriver, CodexDriver, the backends) - on healthy
            // machines as well as starved ones. Three long-standing tests caught it. That recovery is
            // proven and valuable: on a machine with room, a composer that has not echoed in four
            // seconds usually really did lose the text, and retyping gets it back.
            //
            // So on a healthy machine, or one whose memory could not be read, this falls through to
            // exactly the code that ran before issue #2818. Only a machine measured to be short of
            // memory - where a late repaint is the likely explanation and Escape is the thing that
            // deletes the owner's sentence - takes the new path.
            if (evidence == ComposerEvidence.Unknown && MemoryPressure.IsUnderPressure(pressure))
            {
                // WE CANNOT SEE, SO WE DO NOT CUT. Watch the byte stream for a further, finite budget
                // without typing again and without clearing. This is the path a starved machine takes:
                // the text IS in the composer and the repaint simply has not happened yet.
                // SIZED FROM THE RE-READ VERDICT, NOT THE ONE TAKEN BEFORE TYPING (issue #2818).
                //
                // This was the half-applied half of the fix for "pressure that began during the wait".
                // The DECISION to preserve the text correctly used the fresh reading, but the budget did
                // not: it came from `to`, computed before the first keystroke. So in exactly the
                // transition the re-read exists for - room at send start, Critical by the deadline - the
                // Escape was correctly refused, the machine was correctly described as nearly out of
                // memory, and the observation window was then the HEALTHY machine's four seconds instead
                // of the sixteen the same rationale asks for. A quarter of the window, in the case the
                // design calls the common one.
                var extra = echoTimeout ?? UnknownEvidenceBudget(ScaledEchoTimeout(pressure));
                FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: composer echo not seen on attempt {attempt} " +
                              $"(len={text.Length}) and the rendered screen cannot say whether the text is there. " +
                              $"NOT clearing it. Watching for a further {extra.TotalSeconds:F0}s. " +
                              $"{MemoryPressure.Describe(pressure)}.");

                if (await WaitForEchoAsync(buffer, cursor, needle, visibleTailNeedle, extra, poll)
                    || await ObserveComposerAsync(screenSnapshot, needle, visibleTailNeedle, pressure) == ComposerEvidence.Present)
                {
                    FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: the text arrived while waiting - pressing Enter. " +
                                  "Clearing the composer here would have deleted it.");
                    await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat);
                    ComposerRetention.Clear(backend);
                    return;
                }

                // Still unknown after the budget. Give up WITHOUT clearing: the words may be on the
                // owner's screen, where they can press Enter themselves, and the next send clears first.
                PromptDeliveryFailures.RecordComposerEchoMiss(sessionId, driverTag, attempt, text.Length);
                ComposerRetention.MarkMayHoldText(backend, driverTag, text);
                throw new ComposerNotAcceptingInputException(
                    $"[{driverTag}] EchoVerifiedSubmit: the composer never echoed the typed text, and the rendered " +
                    $"screen could not say whether it is there. {MemoryPressure.Describe(pressure)}. The text was " +
                    "NOT cleared - it may still be sitting in the composer on screen. " +
                    $"{EchoMissDiagnostics(buffer, cursor, screenSnapshot, needle, visibleTailNeedle)} " +
                    $"Readable buffer tail: {TailOf(buffer)}");
            }

            if (attempt == 2 && driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
                break;

            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: composer echo not seen on attempt {attempt} " +
                          $"(len={text.Length}, evidence={evidence}, {MemoryPressure.Describe(pressure)}) - " +
                          "clearing the composer and retyping. " +
                          EchoMissDiagnostics(buffer, cursor, screenSnapshot, needle, visibleTailNeedle));
            // Count it as well as log it (issue internal#811). A miss that recovers on the retype costs
            // the user nothing, so it raises no alarm - but it is the leading indicator of the failures
            // that DO cost them their words, and it was invisible until somebody grepped a log file.
            PromptDeliveryFailures.RecordComposerEchoMiss(sessionId, driverTag, attempt, text.Length);
            backend.Write(EscapeByte);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        if (driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: OpenCode echo was torn; pressing Enter instead of failing route");
            await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat);
            ComposerRetention.Clear(backend);
            return;
        }

        // Reached after two cleared-and-retyped attempts on a machine that was not measurably short of
        // memory - so a late repaint is not the explanation, and the composer was cleared each time on
        // that basis rather than on a guess about a starved machine.
        throw new ComposerNotAcceptingInputException(
            $"[{driverTag}] EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts - " +
            "the TUI is not accepting input (a modal, a picker, or a composer still initializing). " +
            $"{MemoryPressure.Describe(pressure)}. " +
            $"{EchoMissDiagnostics(buffer, cursor, screenSnapshot, needle, visibleTailNeedle)} " +
            $"Readable buffer tail: {TailOf(buffer)}");
    }

    private static async Task BracketedPasteSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? enterSettleDelay = null,
        TimeSpan? submitVerifyBeat = null)
    {
        FileLog.Write($"[{driverTag}] SharedSubmit: bracketed paste submit len={text.Length}");
        backend.Write(BracketedPasteStart);
        await WriteTextAsync(backend, text);
        backend.Write(BracketedPasteEnd);
        await PressEnterAndVerifyAsync(
            backend, text, driverTag, enterSettleDelay ?? TimeSpan.FromMilliseconds(80), submitVerifyBeat);
    }

    private static async Task TypeSettleEnterSubmitAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? enterSettleDelay = null,
        TimeSpan? submitVerifyBeat = null)
    {
        FileLog.Write($"[{driverTag}] SharedSubmit: type-settle-enter submit len={text.Length}");
        await WriteTextAsync(backend, text);
        await PressEnterAndVerifyAsync(
            backend, text, driverTag, enterSettleDelay ?? TimeSpan.FromMilliseconds(50), submitVerifyBeat);
    }

    /// <summary>
    /// The large-input route. It no longer calls the submit watchdog itself: the Enter inside
    /// <see cref="EchoVerifiedInlineSubmitAsync"/> is now verified like every other Enter, so calling
    /// it again here would watch the same submit twice.
    /// </summary>
    private static async Task SubmitViaAtReferenceAsync(
        ISessionBackend backend,
        string text,
        string driverTag,
        TimeSpan? echoTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? enterSettleDelay = null,
        Func<string[]>? screenSnapshot = null,
        TimeSpan? submitVerifyBeat = null,
        Guid sessionId = default)
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
            sessionId);
    }

    private static async Task SubmitViaInstructionFileAsync(
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
        Guid sessionId = default)
    {
        var tempPath = LargeInputHandler.CreateTempFile(text, backend.WorkingDirectory);
        var relRef = LargeInputHandler.MakeAtReference(tempPath, backend.WorkingDirectory);
        var fileName = Path.GetFileName(tempPath);
        var instruction = "Read file " + fileName + " in the .temp directory. Path: " + relRef +
            ". If the path fails, search for " + fileName +
            ". This file was explicitly created as the user-provided message payload for this turn; it is not hidden context. " +
            "Follow the instructions in that file and reply with the requested strings only.";
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
                sessionId);
        }
        else
        {
            await TypeSettleEnterSubmitAsync(backend, instruction, driverTag, enterSettleDelay, submitVerifyBeat);
        }
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
        ISessionBackend backend, string driverTag, Func<string[]>? screenSnapshot)
    {
        if (ComposerRetention.TakeRetainedText(backend) is not { } retained) return;

        var pressure = MemoryPressure.Level(MemoryProbe.Read());
        var retainedNeedle = NormalizeForEcho(retained);
        var orphan = await ObserveComposerAsync(
            screenSnapshot, retainedNeedle, VisibleTailNeedle(retainedNeedle), pressure);

        if (ComposerRetention.ShouldClearBeforeTyping(orphan))
        {
            FileLog.Write($"[{driverTag}] ResolveRetainedComposer: the previous send may have left {retained.Length} " +
                          $"characters in this composer (evidence: {orphan}) - clearing before typing so the two " +
                          "cannot run together.");
            backend.Write(EscapeByte);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        else
        {
            FileLog.Write($"[{driverTag}] ResolveRetainedComposer: the previous send's text is provably gone from " +
                          "this composer - not clearing, so nothing typed since is disturbed.");
        }
    }

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
        Func<string[]>? screenSnapshot, string needle, string? visibleTailNeedle, MemoryPressureLevel pressure)
    {
        if (screenSnapshot is null || needle.Length == 0) return ComposerEvidence.Unknown;

        var first = ReadScreen(screenSnapshot);
        if (first is null) return ComposerEvidence.Unknown;
        if (ScreenRowsShowText(first, needle, visibleTailNeedle)) return ComposerEvidence.Present;

        await Task.Delay(BetweenScreenSamples);

        var second = ReadScreen(screenSnapshot);
        if (second is null) return ComposerEvidence.Unknown;
        if (ScreenRowsShowText(second, needle, visibleTailNeedle)) return ComposerEvidence.Present;

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
    private static bool ScreenRowsShowText(string[] rows, string needle, string? visibleTailNeedle)
    {
        if (needle.Length == 0) return false;

        var hay = NormalizeForEcho(string.Concat(rows));
        if (hay.Contains(needle, StringComparison.Ordinal))
            return true;

        // The rendered screen has the same disease as the byte stream from the other end: rows are
        // concatenated without separators and can be snapshotted mid-paint, so a footer hint lands
        // inside the typed text here too (issue #1592). Same question, same answer.
        if (IndexOfInterleaved(hay, needle) >= 0)
            return true;

        return visibleTailNeedle is not null && hay.Contains(visibleTailNeedle, StringComparison.Ordinal);
    }

    private static bool ShouldUseInstructionFile(string driverTag, string text)
    {
        if (!LargeInputHandler.IsLargeInput(text) && text.Length <= 300)
            return false;

        return driverTag.Contains("Codex", StringComparison.OrdinalIgnoreCase)
               || driverTag.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
               || driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Poll the terminal byte stream until the typed text echoes back in the composer.</summary>
    private static async Task<bool> WaitForEchoAsync(
        CircularTerminalBuffer buffer, long cursor, string needle, string? visibleTailNeedle, TimeSpan timeout, TimeSpan poll)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
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
                    await Task.Delay(poll);
                    continue;
                }

                return true;
            }

            if (visibleTailNeedle is not null && hay.Contains(visibleTailNeedle, StringComparison.Ordinal))
                return true;

            await Task.Delay(poll);
        }
        return false;
    }

    private static async Task WriteTextAsync(ISessionBackend backend, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        const int bulkThreshold = 48;
        const int chunkSize = 16;
        if (bytes.Length <= bulkThreshold)
        {
            backend.Write(bytes);
            return;
        }

        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            backend.Write(bytes.AsSpan(offset, count).ToArray());
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
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

    private static string TailOf(CircularTerminalBuffer buffer)
    {
        var text = NormalizeWhitespace(StripAnsi(Encoding.UTF8.GetString(buffer.DumpAll())));
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
