using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// Durable per-turn review log. One JSON record is written every time a session's state
/// flips from Working to "needs you" (a turn end, as decided by our own
/// <c>TerminalStateDetector</c> - never a Claude Code hook). Each record holds the terminal
/// for that turn (the resolved screen plus the transcript produced during it) and whatever
/// the Wingman said or did that turn, so any recent turn can be pulled up and reviewed.
///
/// Layout (date-partitioned so purge is a cheap "delete old day-folders"):
///   base/turn-review/&lt;yyyy-MM-dd&gt;/&lt;sessionId&gt;/&lt;HHmm ss-fff&gt;.json
///
/// Best-effort and self-contained: every method swallows and logs its own exceptions so a
/// logging failure can never break a turn. Retention is <see cref="RetentionDays"/> days.
/// </summary>
public static class TurnReviewLog
{
    public const int RetentionDays = 7;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Write one turn record. Purges expired day-folders first.</summary>
    public static void Write(TurnReviewRecord record)
    {
        try
        {
            Purge();
            var day = record.TsUtc.ToString("yyyy-MM-dd");
            var dir = Path.Combine(CcStorage.TurnReviewLogs(), day, Sanitize(record.SessionId));
            Directory.CreateDirectory(dir);
            var name = record.TsUtc.ToString("HHmmss-fff") + ".json";
            File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(record, JsonOpts));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnReviewLog] Write failed for session={record.SessionId}: {ex.Message}");
        }
    }

    /// <summary>Delete day-folders older than the retention window. Best-effort.</summary>
    private static void Purge()
    {
        try
        {
            var root = CcStorage.TurnReviewLogs();
            var cutoff = DateTime.UtcNow.Date.AddDays(-RetentionDays);
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var day)
                    && day.Date < cutoff)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) { FileLog.Write($"[TurnReviewLog] Purge skip {name}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnReviewLog] Purge failed: {ex.Message}");
        }
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}

/// <summary>One turn-review record. Terminal half is always present; the Wingman half holds
/// whatever it had said/done by the turn end (empty when it stayed silent).</summary>
public sealed class TurnReviewRecord
{
    public DateTime TsUtc { get; set; } = DateTime.UtcNow;
    public string SessionId { get; set; } = "";
    public string? SessionName { get; set; }

    // ----- terminal half -----
    /// <summary>The resolved on-screen grid at the turn end (trailing-trimmed rows, top to bottom).</summary>
    public List<string> Screen { get; init; } = new();
    /// <summary>The terminal output produced DURING this turn (ANSI-stripped, since the previous flip).</summary>
    public string Transcript { get; set; } = "";

    // ----- wingman half (what it said / did, if anything) -----
    public string StatusColor { get; set; } = "";
    public string StatusReason { get; set; } = "";
    /// <summary>The Wingman's latest spoken/briefing text for this session, if any.</summary>
    public string? WingmanSaid { get; set; }
    /// <summary>Actuations the Wingman performed during this turn (type/keys/submit), if any.</summary>
    public List<TurnReviewAction> WingmanActions { get; init; } = new();
}

/// <summary>One Wingman actuation captured in a turn-review record.</summary>
public sealed class TurnReviewAction
{
    public DateTime At { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Reason { get; set; } = "";
}
