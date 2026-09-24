using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>How <see cref="ClaudePromptArrival.ConfirmAsync"/> ended.</summary>
public enum PromptArrivalOutcome
{
    /// <summary>Claude Code wrote the prompt into its conversation file.</summary>
    Arrived,

    /// <summary>The first send was lost with an empty composer; the one resend arrived.</summary>
    ArrivedAfterResend,

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
/// THE ONE RESEND. When no such line appears within the window and the composer shows nothing of ours (empty, or only
/// Claude Code's grey "Try ..." suggestion), the text is gone and typing it again cannot double it: the owner's
/// keystrokes are held for the whole send, so nothing else can be in the composer. It is resent once. Anything else
/// in the composer - the text parked, a paste placeholder, a dialog - is left alone and reported not delivered.
/// </summary>
public static class ClaudePromptArrival
{
    /// <summary>How long to wait for the conversation file after the send. Claude Code writes the line as the turn
    /// starts, well inside a second on a healthy machine; the rest is room for a loaded one.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(20);

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

        string tail;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = fromOffset <= fs.Length ? fromOffset : 0;
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            tail = reader.ReadToEnd();
        }

        foreach (var line in tail.Split('\n'))
        {
            var said = PromptTextOf(line);
            if (said is not null && Normalize(said).Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the composer holds nothing of ours: empty, or only Claude Code's grey suggestion, which the screen
    /// rows carry as text because they carry no colour.
    /// </summary>
    public static bool ComposerHoldsNothing(Drivers.ComposerReading reading, string composerText) =>
        reading == Drivers.ComposerReading.Empty
        || (reading == Drivers.ComposerReading.HoldsText && ClaudeSuggestion.IsMatch(composerText.Trim()));

    /// <summary>
    /// Wait for the prompt to reach the conversation file, resending it once if it was lost with an empty composer.
    /// </summary>
    /// <param name="arrived">True once the conversation file holds the prompt.</param>
    /// <param name="composerHoldsNothing">True when the composer shows nothing of ours; asked twice, a beat apart,
    /// before a resend, so a frame caught mid-repaint cannot license one.</param>
    /// <param name="resend">Types and submits the prompt again. Called at most once.</param>
    /// <param name="mayResend">False when a resend is not allowed at all (a guarded send, which must stay bounded).</param>
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

        if (await WaitAsync(arrived, window, step, wait, clock))
            return PromptArrivalOutcome.Arrived;

        if (!mayResend)
        {
            FileLog.Write($"[ClaudePromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s; a resend is not allowed on this send");
            return PromptArrivalOutcome.NotArrived;
        }

        var empty = composerHoldsNothing();
        if (empty)
        {
            await wait(step);
            empty = composerHoldsNothing() && !arrived();
        }
        if (!empty)
        {
            FileLog.Write($"[ClaudePromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s, and the composer " +
                          "is not empty - NOT resending, so nothing can be doubled");
            return arrived() ? PromptArrivalOutcome.Arrived : PromptArrivalOutcome.NotArrived;
        }

        FileLog.Write($"[ClaudePromptArrival] '{label}' is not in the conversation file after {window.TotalSeconds:F0}s and the composer " +
                      "is empty - the text was lost; sending it once more");
        await resend();
        if (await WaitAsync(arrived, window, step, wait, clock))
        {
            FileLog.Write($"[ClaudePromptArrival] '{label}' arrived after the resend");
            return PromptArrivalOutcome.ArrivedAfterResend;
        }
        FileLog.Write($"[ClaudePromptArrival] '{label}' is still not in the conversation file after the resend");
        return PromptArrivalOutcome.NotArrived;
    }

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

            if (type != "user") return null;
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
            if (!message.TryGetProperty("content", out var content)) return null;

            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind != JsonValueKind.Array) return null;
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var it) && it.GetString() == "text"
                    && item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    sb.Append(tx.GetString()).Append(' ');
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }

    /// <summary>The start of the prompt as it must appear in the line: whitespace and invisible characters folded
    /// away, because Claude Code strips invisible characters and a line ending may be CRLF on one side and LF on the
    /// other.</summary>
    internal static string Fingerprint(string text)
    {
        var normal = Normalize(text);
        return normal.Length <= FingerprintLength ? normal : normal[..FingerprintLength];
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
