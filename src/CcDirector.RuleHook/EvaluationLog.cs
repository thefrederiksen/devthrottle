using System.Globalization;
using System.Text;

namespace CcDirector.RuleHook;

/// <summary>
/// The hook's evaluation log (wire contract W4): <c>&lt;sessionId&gt;.eval.log</c> beside the session's
/// rules file, ONE LINE PER EVALUATION, match or not. It is what turns a negative control into a
/// presence: "this command fired nothing" is proven by a <c>no_match</c> line, never by the absence of a
/// firing.
///
/// A line is tab-separated: UTC time, tool, <c>rules=</c>n, <c>matched=</c>n, the outcome
/// (<c>would_deny</c>, <c>would_allow</c>, <c>no_match</c>, <c>no_rules_file</c> or <c>error: ...</c>),
/// the evaluation time in microseconds with a <c>us</c> suffix, and the agent's call id (Claude Code's
/// <c>tool_use_id</c>, or <c>-</c> when the event could not be read) - ruling P14, which ties a line to one
/// command. It never holds command text.
/// </summary>
internal static class EvaluationLog
{
    // UTF-8 without a byte-order mark: a mark at the start of a new log would glue itself to the first
    // line's time.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The log beside <paramref name="rulesFile"/>: <c>x/&lt;id&gt;.json</c> logs to <c>x/&lt;id&gt;.eval.log</c>.</summary>
    public static string PathFor(string rulesFile) => Path.ChangeExtension(rulesFile, ".eval.log");

    public static void Append(string rulesFile, DateTime occurredUtc, string tool, int rulesRead, int matched,
        string outcome, long microseconds, string callId, long limitBytes)
    {
        var path = PathFor(rulesFile);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{occurredUtc:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}\t{tool}\trules={rulesRead}\tmatched={matched}\t{outcome}\t{microseconds}us\t{callId}\n");
        // Shared for reading, writing and deleting: Claude Code can run two shell calls at once, so two
        // hooks can append to one log, and on Windows a plain append would refuse the second one.
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var bytes = Utf8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
        }

        if (new FileInfo(path).Length > limitBytes)
            CutToNewestHalf(path);
    }

    /// <summary>
    /// Keep the newest half of the log, starting on a whole line. Written to a sibling and moved over
    /// the log, so a reader never sees a half-cut file. Two hooks cutting at once lose at most a line
    /// appended between one's read and its move - acceptable for a size cap, and never a wrong line.
    /// </summary>
    private static void CutToNewestHalf(string path)
    {
        var text = File.ReadAllText(path, Utf8);
        var newline = text.IndexOf('\n', text.Length / 2);
        var kept = newline < 0 ? "" : text[(newline + 1)..];
        // Named for this process, so two hooks cutting at once never write into the same sibling.
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, kept, Utf8);
        File.Move(tmp, path, overwrite: true);
    }
}
