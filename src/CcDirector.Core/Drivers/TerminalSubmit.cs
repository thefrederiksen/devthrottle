using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
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
    /// <see cref="ScreenShowsText"/>).
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
            return;
        }

        if (LargeInputHandler.IsLargeInput(textForCheck))
        {
            if (bracketedPasteEnabled)
            {
                await BracketedPasteSubmitAsync(backend, textForCheck, driverTag, enterSettleDelay, submitVerifyBeat);
                return;
            }

            if (!string.IsNullOrWhiteSpace(backend.WorkingDirectory))
            {
                await SubmitViaAtReferenceAsync(backend, textForCheck, driverTag, echoTimeout, pollInterval, enterSettleDelay, screenSnapshot, submitVerifyBeat, sessionId);
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

        var to = echoTimeout ?? TimeSpan.FromSeconds(4);
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var settle = enterSettleDelay ?? TimeSpan.FromMilliseconds(40);
        var needle = NormalizeForEcho(text);
        var visibleTailNeedle = VisibleTailNeedle(needle);

        var cursor = buffer.TotalBytesWritten;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cursor = buffer.TotalBytesWritten;
            await WriteTextAsync(backend, text);

            if (needle.Length == 0 || await WaitForEchoAsync(buffer, cursor, needle, visibleTailNeedle, to, poll))
            {
                await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat);
                return;
            }

            // The byte stream missed the echo, but that stream is a poor witness for input that
            // WRAPPED across composer rows: the TUI repaints wrapped text interleaved with box
            // borders, footer hints ("esc again to clear") and cursor moves, so the typed text may
            // never appear in the bytes as one contiguous run even though it is sitting in the
            // composer. Ask the rendered screen - the final visual state, free of interleaving -
            // before treating the attempt as a failure and disturbing the composer with Escape.
            if (ScreenShowsText(screenSnapshot, needle, visibleTailNeedle))
            {
                FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: byte-stream echo missed on attempt {attempt} " +
                              $"but the rendered screen shows the typed text (len={text.Length}) - pressing Enter");
                await PressEnterAndVerifyAsync(backend, text, driverTag, settle, submitVerifyBeat);
                return;
            }

            if (attempt == 2 && driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
                break;

            FileLog.Write($"[{driverTag}] EchoVerifiedSubmit: composer echo not seen on attempt {attempt} " +
                          $"(len={text.Length}) - clearing the composer and retyping. " +
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
            return;
        }

        throw new ComposerNotAcceptingInputException(
            $"[{driverTag}] EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts - " +
            "the TUI is not accepting input (a modal, a picker, or a composer still initializing). " +
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
        var instruction = LargeInputHandler.FormatInputFileInstruction(relRef);
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
    /// Second-opinion echo check against the RENDERED screen instead of the raw byte stream. Wrapped
    /// composer rows reconstruct into the original text when the rows are concatenated and normalized
    /// (the wrap points, borders and padding all fall outside <see cref="NormalizeForEcho"/>'s
    /// alphabet), so finding the needle here is proof the composer holds the typed text even when the
    /// byte stream only ever carried it as interleaved fragments. Also accepts the visible-tail
    /// needle for composers that truncate the display of long input.
    /// </summary>
    private static bool ScreenShowsText(Func<string[]>? screenSnapshot, string needle, string? visibleTailNeedle)
    {
        if (screenSnapshot is null || needle.Length == 0)
            return false;

        var hay = NormalizeForEcho(string.Concat(screenSnapshot()));
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
        var isLargeOrMultiline = LargeInputHandler.IsLargeInput(text);
        if (driverTag.Contains("Codex", StringComparison.OrdinalIgnoreCase))
            return isLargeOrMultiline;

        return (isLargeOrMultiline || text.Length > 300)
               && (driverTag.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
                   || driverTag.Contains("OpenCode", StringComparison.OrdinalIgnoreCase));
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
        if (screenSnapshot is null)
        {
            screenInfo = "screen=<no snapshot supplied by driver>";
        }
        else
        {
            var rows = screenSnapshot();
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
