using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>How <see cref="PromptArrival.ConfirmAsync"/> ended.</summary>
public enum PromptArrivalOutcome
{
    /// <summary>Claude Code wrote the prompt into its conversation file.</summary>
    Arrived,

    /// <summary>The first send was lost with an empty composer; the one resend arrived.</summary>
    ArrivedAfterResend,

    /// <summary>A new prompt reached the agent, but its text is not the text that was sent - characters were dropped or
    /// changed on the way. Never resent: the agent already has a prompt, and a second would run the work twice.</summary>
    ArrivedAltered,

    /// <summary>The prompt never reached the conversation file.</summary>
    NotArrived,
}

/// <summary>
/// THE PROOF THAT A PROMPT REACHED CLAUDE CODE (issue #3290).
///
/// WHY THIS EXISTS. Every other check on a submit reads the terminal: the echo check reads the byte stream, and the
/// submit verifier counts how many bytes followed the Enter. Neither can tell a prompt Claude Code took from one it
/// threw away. On 24 September 2026 the 07:45 LinkedIn schedule pasted a 3,606 character prompt into a Claude Code
/// that was still starting. Claude Code dropped the text, kept the paste markers, and when the verifier pressed Enter
/// it printed "Removed 4 invisible characters - nothing left to send". That message was more than 2 KB of output, so
/// the verifier logged "submitted" and the session was shown Working with nothing to do. Three of that morning's
/// twelve long scheduled prompts were lost the same way.
///
/// THE WITNESS. Claude Code writes every prompt it accepts into its conversation file (the transcript) as a user line,
/// or, when it is busy, as a queue "enqueue" line. A prompt is delivered when a line written AFTER the send holds its
/// text. Nothing else counts.
///
/// THE ONE RESEND. When no such line appears within the window, no turn is running, and the composer shows nothing of
/// ours (empty, or only Claude Code's grey "Try ..." suggestion), the text is gone and typing it again cannot double it.
/// It is resent once - never when the owner has typed since the send began, whose keystrokes are not held during an
/// ordinary send (review finding 1). A
/// running turn (the session's state or the screen's working marker) forbids it, because a loaded machine can start
/// the turn before it writes the line. Anything else in the composer - the text parked, a paste placeholder, a dialog
/// - is left alone and reported not delivered.
/// </summary>
public static class PromptArrival
{
    /// <summary>How long to wait for the conversation file after the send. Claude Code writes the line as the turn
    /// starts, well inside a second on a healthy machine; the rest is room for a loaded one.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    /// <summary>How often the conversation file is read while waiting.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>How many characters of the prompt must be found in the line. Enough to be the prompt, short enough to
    /// survive any wrapper Claude Code puts around a paste.</summary>
    internal const int FingerprintLength = 80;

    private static readonly Regex ClaudeSuggestion = new("^Try \"[^\"]*\"$", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the conversation file can prove this text arrived. A slash command, a bash line ('!') and a memory
    /// line ('#') are recorded by Claude Code in their own shapes, and /clear starts a new file, so they are not checked.
    /// </summary>
    public static bool CanProve(string text)
    {
        var start = (text ?? "").TrimStart();
        return start.Length > 0 && start[0] is not ('/' or '!' or '#') && Fingerprint(text!).Length > 0;
    }

    /// <summary>The current end of the conversation file, the point after which the prompt must appear. Zero when the
    /// file does not exist yet, as before a session's first prompt.</summary>
    public static long EndOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    /// <summary>
    /// True when a line written after <paramref name="fromOffset"/> holds <paramref name="text"/>: a user line that is
    /// not meta and not a sidechain, or a queue enqueue. A file shorter than the offset was replaced, and is read whole.
    /// </summary>
    public static bool Arrived(string? path, long fromOffset, string text)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var needle = Fingerprint(text);
        if (needle.Length == 0) return false;

        if (ReadTail(path, fromOffset) is not { } tail) return false;
        foreach (var line in tail.Split('\n'))
        {
            var said = PromptTextOf(line);
            if (said is not null && Loose(said).Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The file from <paramref name="fromOffset"/> to its end, or null when it cannot be read right now (review finding
    /// 10): a scanner or another process holding the file for a moment is "not yet", never a lost prompt.
    /// </summary>
    private static string? ReadTail(string path, long fromOffset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(fromOffset <= fs.Length ? fromOffset : 0, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[PromptArrival] could not read {path} this time ({ex.GetType().Name}: {ex.Message}); looking again next poll");
            return null;
        }
    }

    /// <summary>True when any user prompt at all was written to the file after <paramref name="fromOffset"/>.</summary>
    public static bool AnyPromptAfter(string? path, long fromOffset)
    {
        foreach (var said in PromptsAfter(path, fromOffset))
            if (!string.IsNullOrWhiteSpace(said)) return true;
        return false;
    }

    private static IEnumerable<string?> PromptsAfter(string? path, long fromOffset)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) yield break;
        if (ReadTail(path, fromOffset) is not { } tail) yield break;
        foreach (var line in tail.Split('\n'))
            yield return PromptTextOf(line);
    }

    /// <summary>
    /// True when the composer holds nothing of ours: empty, or only Claude Code's grey suggestion, which the screen
    /// rows carry as text because they carry no colour.
    /// </summary>
    public static bool ComposerHoldsNothing(ComposerReading reading, string composerText) =>
        reading == ComposerReading.Empty
        || (reading == ComposerReading.HoldsText && ClaudeSuggestion.IsMatch(composerText.Trim()));

    /// <summary>
    /// Wait for the prompt to reach the conversation file, resending it once if it was lost with an empty composer.
    /// </summary>
    /// <param name="arrived">True once the conversation file holds the prompt.</param>
    /// <param name="composerHoldsNothing">True when the composer shows nothing of ours; asked twice, a beat apart,
    /// before a resend, so a frame caught mid-repaint cannot license one.</param>
    /// <param name="resend">Types and submits the prompt again. Called at most once.</param>
    /// <param name="mayResend">False when a resend is not allowed at all (a guarded send, which must stay bounded).</param>
    /// <param name="agentPrintedSinceSend">False when the terminal has printed nothing since the send: the agent has not
    /// read its input yet, so an empty composer proves nothing and a resend would queue behind the first copy.</param>
    /// <param name="window">How long to wait for each send.</param>
    /// <param name="label">What was sent, for the log.</param>
    /// <param name="poll">How often to look; <see cref="DefaultPoll"/> when null.</param>
    /// <param name="pause">How to wait between looks; tests pass one that does not sleep.</param>
    /// <param name="utcNow">The clock; tests pass one they advance.</param>
    public static async Task<PromptArrivalOutcome> ConfirmAsync(
        Func<bool> arrived,
        Func<bool> composerHoldsNothing,
        Func<Task> resend,
        bool mayResend,
        TimeSpan window,
        string label,
        Func<bool>? anyNewPrompt = null,
        Func<Task<bool>>? textWaitsUnsubmitted = null,
        Action? pressEnter = null,
        Func<bool>? ownerTyped = null,
        Func<bool>? agentPrintedSinceSend = null,
        TimeSpan? poll = null,
        Func<TimeSpan, Task>? pause = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(arrived);
        ArgumentNullException.ThrowIfNull(composerHoldsNothing);
        ArgumentNullException.ThrowIfNull(resend);
        var step = poll ?? DefaultPoll;
        var wait = pause ?? (d => Task.Delay(d));
        var clock = utcNow ?? (() => DateTime.UtcNow);

        // THE ONLY ENTER PRESSED AGAIN IS ONE THE SCREEN ASKS FOR (issue #3290). Blind nudges on a quiet terminal turned
        // into blank lines in the next prompt. Here: not in the records after EnterAgainAfter, no turn running, and the
        // composer on screen still holding text - the Enter was eaten (an autocomplete menu takes the first one) and one
        // more submits it. At most MaxEnterAgain times.
        var deadline = clock() + window;
        if (textWaitsUnsubmitted is not null && pressEnter is not null)
        {
            for (var again = 0; again < MaxEnterAgain; again++)
            {
                var checkAt = clock() + EnterAgainAfter;
                if (await WaitAsync(arrived, checkAt < deadline ? EnterAgainAfter : deadline - clock(), step, wait, clock))
                    return PromptArrivalOutcome.Arrived;
                if (clock() >= deadline) break;
                if (ownerTyped?.Invoke() == true || anyNewPrompt?.Invoke() == true || !await textWaitsUnsubmitted()) continue;
                FileLog.Write($"[PromptArrival] '{label}' is not in the records after {EnterAgainAfter.TotalSeconds:F0}s and the composer " +
                              $"still holds text with no turn running - pressing Enter again ({again + 1}/{MaxEnterAgain})");
                pressEnter();
            }
        }
        var remaining = deadline - clock();
        if (await WaitAsync(arrived, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, step, wait, clock))
            return PromptArrivalOutcome.Arrived;

        // A PROMPT ARRIVED, JUST NOT OURS WORD FOR WORD: never resend. Resending on a text mismatch sent a Codex prompt
        // twice on 24 September - Codex had it, with its dashes and curly quotes stripped, and had already answered.
        if (anyNewPrompt?.Invoke() == true)
        {
            FileLog.Write($"[PromptArrival] '{label}' is not in the conversation records word for word, but a new prompt IS " +
                          "there - it arrived altered. NOT resending.");
            return PromptArrivalOutcome.ArrivedAltered;
        }

        if (ownerTyped?.Invoke() == true)
        {
            FileLog.Write($"[PromptArrival] '{label}' is not in the records after {window.TotalSeconds:F0}s and the owner typed during " +
                          "the send - NOT resending, so their text is never submitted or merged by us");
            return PromptArrivalOutcome.NotArrived;
        }

        if (!mayResend)
        {
            FileLog.Write($"[PromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s; a resend is not allowed on this send");
            return PromptArrivalOutcome.NotArrived;
        }

        // AN AGENT THAT HAS PRINTED NOTHING HAS NOT READ ITS INPUT (issue #3290, measured 24 September 2026). Under full
        // CPU load Claude Code took in none of an 8 KB paste for minutes; its composer read as empty because it had not
        // drawn the paste yet, and the resend pasted the same text a second time behind the first.
        if (agentPrintedSinceSend?.Invoke() == false)
        {
            FileLog.Write($"[PromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s and the terminal " +
                          "has printed nothing since the send - the agent has not read its input yet. NOT resending, so nothing can be doubled");
            return PromptArrivalOutcome.NotArrived;
        }

        var empty = composerHoldsNothing();
        if (empty)
        {
            await wait(step);
            empty = composerHoldsNothing() && !arrived() && anyNewPrompt?.Invoke() != true;
        }
        if (!empty)
        {
            FileLog.Write($"[PromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s, and the composer " +
                          "is not empty - NOT resending, so nothing can be doubled");
            return arrived() ? PromptArrivalOutcome.Arrived : PromptArrivalOutcome.NotArrived;
        }

        FileLog.Write($"[PromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s and the composer " +
                      "is empty - the text was lost; sending it once more");
        await resend();
        if (await WaitAsync(arrived, window, step, wait, clock))
        {
            FileLog.Write($"[PromptArrival] '{label}' arrived after the resend");
            return PromptArrivalOutcome.ArrivedAfterResend;
        }
        FileLog.Write($"[PromptArrival] '{label}' is still not in the conversation file after the resend");
        return PromptArrivalOutcome.NotArrived;
    }

    internal static readonly TimeSpan EnterAgainAfter = TimeSpan.FromSeconds(8);
    internal const int MaxEnterAgain = 2;

    private static async Task<bool> WaitAsync(
        Func<bool> arrived, TimeSpan window, TimeSpan step, Func<TimeSpan, Task> wait, Func<DateTime> clock)
    {
        var deadline = clock() + window;
        while (true)
        {
            if (arrived()) return true;
            if (clock() >= deadline) return false;
            await wait(step);
        }
    }

    /// <summary>The prompt text a conversation line carries, or null when the line is not a prompt.</summary>
    internal static string? PromptTextOf(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null; // the last line may be half written
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

            if (type == "queue-operation")
            {
                var op = root.TryGetProperty("operation", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                return op == "enqueue" && root.TryGetProperty("content", out var qc) && qc.ValueKind == JsonValueKind.String
                    ? qc.GetString()
                    : null;
            }

            // Codex: {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text",...}]}}
            if (type == "response_item")
                return root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                       && StringOf(payload, "role") == "user"
                    ? TextParts(payload)
                    : null;

            // Copilot (session-state/<id>/events.jsonl): {"type":"user.message","data":{"content":"..."}}
            if (type == "user.message")
                return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    ? StringOf(data, "content")
                    : null;

            // Pi: {"type":"message","message":{"role":"user","content":[{"type":"text",...}]}}
            if (type == "message")
                return root.TryGetProperty("message", out var piMessage) && piMessage.ValueKind == JsonValueKind.Object
                       && StringOf(piMessage, "role") == "user"
                    ? TextParts(piMessage)
                    : null;

            if (type != "user") return null;
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;

            // Claude Code: {"type":"user","message":{"content": "..." or [text parts]}}. Grok: {"type":"user","content":[...]}.
            return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                ? TextParts(message)
                : TextParts(root);
        }
    }

    private static string? StringOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The text of an element's "content": the string itself, or its text parts joined.</summary>
    private static string? TextParts(JsonElement holder)
    {
        if (!holder.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var kind = StringOf(item, "type");
            if (kind is "text" or "input_text" && StringOf(item, "text") is { } text)
                sb.Append(text).Append(' ');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>The start of the prompt as it must appear in the line: whitespace and invisible characters folded
    /// away, because Claude Code strips invisible characters and a line ending may be CRLF on one side and LF on the
    /// other.</summary>
    internal static string Fingerprint(string text)
    {
        var loose = Loose(text);
        return loose.Length <= FingerprintLength ? loose : loose[..FingerprintLength];
    }

    /// <summary>
    /// The letters and digits of <paramref name="text"/>, and nothing else. The prompt is found in the records by these
    /// alone (review finding 7): an agent that drops a variation selector, re-flows spacing or strips punctuation still
    /// holds the prompt, and calling that a failed delivery invites the owner to send it again.
    /// </summary>
    internal static string Loose(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control) continue;
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
