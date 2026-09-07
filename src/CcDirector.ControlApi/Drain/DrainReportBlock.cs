using System.Text.RegularExpressions;

namespace CcDirector.ControlApi.Drain;

/// <summary>One seat this document's author says its own document accounts for.</summary>
/// <param name="SessionId">The covered seat's session id, as written.</param>
/// <param name="Note">Why reporting up was the right answer for that one, in the author's words.</param>
public sealed record DrainCoveredClaim(string SessionId, string Note);

/// <summary>
/// The machine-readable block a drained session puts at the END of its handover document.
///
/// WHY THERE IS A BLOCK AT ALL. Everything a drain does mechanically it must be able to KNOW
/// mechanically, and four of the facts it needs are ones only the session itself can supply:
///
///  - that it reached a clean stop, or that it is BLOCKED and on what;
///  - which of its subordinates its own document accounts for, so those seats are recorded
///    <c>covered</c> and no thin file is invented for them;
///  - whether it should be brought back after the restart, and why - which is a judgment about its own
///    work, and the session doing the work is the one that knows;
///  - the questions it is leaving on the owner, word for word.
///
/// In the first real drain a human read seventeen replies to learn those four things. The block is the
/// same information, declared once by the seat that owns it, in a form that survives the session.
///
/// THE ORDINARY CASE NEEDS NO BLOCK. A document that exists at the seat's own path IS a handover, and a
/// seat that wrote one is <c>drained</c> with an undecided restore. The block is required only for the
/// four things a file's existence cannot say. That keeps the common path exactly as forgiving as the
/// hand-run's, and it means a missing block is never read as a missing handover.
///
/// The shape, in an HTML comment so it does not render, all ASCII, one fact per line:
///
/// <code>
/// &lt;!-- drain-report
/// state: drained
/// restore: yes
/// why: The packaging work is half done and the exact next action is named above.
/// covered: 08c7bba5-... | Re-seated by a fresh Manager on the committed brief.
/// question: Deploy the merged ring change (pull request 2718)? It needs your go.
/// --&gt;
/// </code>
///
/// A document may carry more than one block - a session that amends its handover after it was read adds
/// one rather than editing in place. THE LAST BLOCK WINS, because it is the author's latest word.
/// </summary>
public sealed class DrainReportBlock
{
    /// <summary>The declared drain state, lowercased, or null when the block did not declare one. Checked
    /// against <see cref="Gateway.Contracts.WorkspaceDrainStates"/> by the caller, which owns what a valid
    /// state is.</summary>
    public string? State { get; init; }

    /// <summary>What the seat says it is blocked on, in its own words. Null unless it declared one.</summary>
    public string? BlockedReason { get; init; }

    /// <summary>True when the seat asked to be brought back, false when it asked to be left closed, null
    /// when it did not say - which is a real third answer and is recorded as undecided, never as "no".</summary>
    public bool? Restore { get; init; }

    /// <summary>Why that restore answer, in the seat's own words.</summary>
    public string? Why { get; init; }

    /// <summary>The subordinate seats this document accounts for.</summary>
    public IReadOnlyList<DrainCoveredClaim> Covered { get; init; } = Array.Empty<DrainCoveredClaim>();

    /// <summary>Questions this seat is leaving on the owner, word for word.</summary>
    public IReadOnlyList<string> Questions { get; init; } = Array.Empty<string>();

    /// <summary>Lines inside a block that were not understood. Never dropped: a seat that misspelled a key
    /// meant to say something, and a silently ignored line is a fact the drain lost.</summary>
    public IReadOnlyList<string> UnparsedLines { get; init; } = Array.Empty<string>();

    // Non-greedy so the LAST block is found by taking the last match rather than one giant span.
    private static readonly Regex BlockPattern = new(
        @"<!--\s*drain-report\s*(?<body>.*?)-->",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse the last drain-report block in a handover document. Returns null when the document carries
    /// none, which is the ordinary case and is not an error.
    /// </summary>
    /// <param name="documentText">The whole document.</param>
    public static DrainReportBlock? Parse(string? documentText)
    {
        if (string.IsNullOrWhiteSpace(documentText)) return null;

        Match? last = null;
        foreach (Match m in BlockPattern.Matches(documentText)) last = m;
        if (last is null) return null;

        string? state = null;
        string? blockedReason = null;
        bool? restore = null;
        string? why = null;
        var covered = new List<DrainCoveredClaim>();
        var questions = new List<string>();
        var unparsed = new List<string>();

        foreach (var raw in last.Groups["body"].Value.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                unparsed.Add(line);
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "state":
                    state = value.Length == 0 ? null : value.ToLowerInvariant();
                    break;
                case "blocked-reason":
                case "blockedreason":
                    blockedReason = value.Length == 0 ? null : value;
                    break;
                case "restore":
                    restore = ParseYesNo(value, unparsed, line);
                    break;
                case "why":
                    why = value.Length == 0 ? null : value;
                    break;
                case "covered":
                    {
                        var bar = value.IndexOf('|');
                        var id = (bar < 0 ? value : value[..bar]).Trim();
                        var note = bar < 0 ? "" : value[(bar + 1)..].Trim();
                        if (id.Length == 0) unparsed.Add(line);
                        else covered.Add(new DrainCoveredClaim(id, note));
                        break;
                    }
                case "question":
                    if (value.Length == 0) unparsed.Add(line);
                    else questions.Add(value);
                    break;
                default:
                    unparsed.Add(line);
                    break;
            }
        }

        return new DrainReportBlock
        {
            State = state,
            BlockedReason = blockedReason,
            Restore = restore,
            Why = why,
            Covered = covered,
            Questions = questions,
            UnparsedLines = unparsed,
        };
    }

    private static bool? ParseYesNo(string value, List<string> unparsed, string originalLine)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "yes":
            case "true":
            case "restore":
                return true;
            case "no":
            case "false":
            case "close":
                return false;
            case "":
                return null;
            default:
                // NOT guessed. "restore: probably" is a seat that meant something, and reading it as
                // either answer would put a wrong decision in the one field a stranger acts on.
                unparsed.Add(originalLine);
                return null;
        }
    }

    /// <summary>
    /// The block text a seat is asked to write, rendered for the drain message so the instruction and the
    /// parser can never disagree about the shape.
    /// </summary>
    public static string Template =>
        "<!-- drain-report / state: drained | blocked | declined / restore: yes | no / " +
        "why: <one line> / covered: <session id> | <why that seat reported up> / " +
        "question: <a question you are leaving on the owner, word for word> / -->";
}
