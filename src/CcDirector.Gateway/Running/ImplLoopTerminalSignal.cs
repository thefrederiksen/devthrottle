using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Running;

/// <summary>
/// One of the three terminal-signal values the implementation-loop emits (issue #272,
/// DEVELOPMENT_METHOD.md Section 7a). There are exactly three; the queue runner (#274) reads
/// exactly one per started session.
/// </summary>
public enum ImplLoopSignal
{
    /// <summary>The issue was verified and squash-merged to main; the run is fully complete.</summary>
    Done,

    /// <summary>The run stopped cleanly and a human must act; work is committed/parked.</summary>
    NeedsHuman,

    /// <summary>The run could not complete - it stopped abnormally and produced no usable result.</summary>
    Failed,
}

/// <summary>
/// The parsed <c>IMPL-LOOP-TERMINAL</c> sentinel block (issue #272, DEVELOPMENT_METHOD.md
/// Section 7a). The implementation-loop prints exactly one such block as its final transcript
/// output for an issue; the queue runner (#274) reads it off the session transcript to learn that
/// one loop run has finished without parsing prose. This type is the machine-readable contract:
///
/// <code>
/// IMPL-LOOP-TERMINAL
/// issue: &lt;N&gt;
/// signal: done | needs-human | failed
/// pr: &lt;pr number or none&gt;
/// merged: yes | no
/// reason: &lt;one line - why this terminal state&gt;
/// </code>
/// </summary>
public sealed class ImplLoopTerminalSignal
{
    /// <summary>The issue number the signal correlates with (the block's <c>issue:</c> field).</summary>
    public int Issue { get; init; }

    /// <summary>Which of the three terminal values the run ended on.</summary>
    public ImplLoopSignal Signal { get; init; }

    /// <summary>The PR number, or null when the block carried <c>none</c>.</summary>
    public int? Pr { get; init; }

    /// <summary>True only on the <c>done</c> signal (the block's <c>merged: yes</c>).</summary>
    public bool Merged { get; init; }

    /// <summary>The single human-readable line explaining the terminal state.</summary>
    public string Reason { get; init; } = "";

    private const string Marker = "IMPL-LOOP-TERMINAL";

    /// <summary>
    /// Find the LAST complete <c>IMPL-LOOP-TERMINAL</c> block in the supplied transcript text that
    /// correlates with <paramref name="expectedIssue"/>, and parse it. Returns null when no such
    /// block is present yet (the run has not reached its terminal signal), or when the most recent
    /// block correlates with a different issue. Reading the LAST block makes the parse idempotent
    /// against a transcript that the runner re-reads as the session keeps producing output.
    /// </summary>
    public static ImplLoopTerminalSignal? ParseLatest(string? transcript, int expectedIssue)
    {
        if (string.IsNullOrEmpty(transcript))
            return null;

        var lines = transcript.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Walk every marker occurrence; keep the last one that parses AND correlates by issue.
        ImplLoopTerminalSignal? latest = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals(Marker, StringComparison.Ordinal))
                continue;

            var parsed = ParseBlock(lines, i);
            if (parsed is not null && parsed.Issue == expectedIssue)
                latest = parsed;
        }

        return latest;
    }

    /// <summary>
    /// Parse a single block whose marker line is at <paramref name="markerIndex"/>. The block's
    /// fields are the lines immediately following the marker, in any order, up to the first line
    /// that is not a recognized <c>key: value</c> field. A block missing <c>issue</c> or
    /// <c>signal</c> is incomplete and returns null (the runner waits rather than guessing).
    /// </summary>
    private static ImplLoopTerminalSignal? ParseBlock(string[] lines, int markerIndex)
    {
        int? issue = null;
        ImplLoopSignal? signal = null;
        int? pr = null;
        var merged = false;
        var reason = "";

        for (var i = markerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                break; // not a key: value field - the block has ended

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "issue":
                    if (int.TryParse(value, out var n)) issue = n;
                    break;
                case "signal":
                    signal = ParseSignal(value);
                    break;
                case "pr":
                    pr = value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Length == 0
                        ? null
                        : int.TryParse(value.TrimStart('#'), out var p) ? p : null;
                    break;
                case "merged":
                    merged = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "reason":
                    reason = value;
                    break;
                default:
                    // A non-field line after the marker ends the block.
                    return Build(issue, signal, pr, merged, reason);
            }
        }

        return Build(issue, signal, pr, merged, reason);
    }

    private static ImplLoopTerminalSignal? Build(int? issue, ImplLoopSignal? signal, int? pr, bool merged, string reason)
    {
        // A block is only valid once it carries both the correlation key and a recognized signal.
        if (issue is null || signal is null)
        {
            FileLog.Write("[ImplLoopTerminalSignal] incomplete block (missing issue or signal)");
            return null;
        }

        return new ImplLoopTerminalSignal
        {
            Issue = issue.Value,
            Signal = signal.Value,
            Pr = pr,
            // merged: yes is only meaningful on done; never trust merged=yes on a non-done signal.
            Merged = merged && signal.Value == ImplLoopSignal.Done,
            Reason = reason,
        };
    }

    private static ImplLoopSignal? ParseSignal(string value) => value.ToLowerInvariant() switch
    {
        "done" => ImplLoopSignal.Done,
        "needs-human" => ImplLoopSignal.NeedsHuman,
        "failed" => ImplLoopSignal.Failed,
        _ => null,
    };
}
