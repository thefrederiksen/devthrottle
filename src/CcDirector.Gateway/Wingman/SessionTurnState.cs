using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Wingman;

/// <summary>What the carrying-on clock believes about one owned session.</summary>
/// <param name="InTurn">True when the session counts as still working for its owner, so the owner's clock does not
/// run.</param>
/// <param name="StoppedAtUtc">When the session stopped, or null when it has not or nothing says.</param>
/// <param name="Rule">Which rule decided, for the log and the tests.</param>
public sealed record OwnedTurnReading(bool InTurn, DateTime? StoppedAtUtc, string Rule);

/// <summary>
/// IS AN OWNED SESSION STILL WORKING FOR ITS OWNER (issue #2992; the Architect's rulings, round 2). The one place the
/// carrying-on clock's reading of a single owned session is decided.
///
/// WHY NOT THE TERMINAL ALONE. The terminal-state detector calls a session waiting after ten seconds without a byte,
/// whatever the reason. A Worker inside a long command that prints nothing is still working - its conversation ends
/// in the tool call with no result yet - and its owner, which said it would wait for that Worker, was marked
/// "Said it would continue and did not" while the Worker was still running.
///
/// WHY NOT THE DIRECTOR'S "Working" ALONE (round 1's mistake). That state was built as a display hint. It says
/// "Working" for a tool call waiting on a PERSON and for a conversation whose last line is an interrupt or a local
/// command, and every one of those is silent. Read as work, each held its owner purple forever.
///
/// THE RULE. Busy means a tool is running FOR THE AGENT, not waiting for a person. A session counts as in a turn when
/// its terminal is printing right now, or when the Director says "Working" and the stored conversation ends in either
///  - an assistant tool call with no result, none of whose calls waits on a person (see
///    <see cref="ToolsThatWaitOnAPerson"/>), while the terminal is not showing a permission prompt; or
///  - a real prompt from the person, with no reply yet - the agent is thinking.
/// A turn has ENDED when the Director says "NeedsYou" and the conversation ends in an assistant entry that calls no
/// tool; the stop moment is that entry's own time in the conversation, so a reconnect or a repeated push - which
/// re-stamp the stored head - can never move it.
///
/// EVERYTHING ELSE READS THE TERMINAL, AS BEFORE ISSUE #2992, BY RULE: in a turn while the terminal says Working,
/// stopped at its last write. That covers a tool waiting on a person, a permission prompt, an interrupt, a local
/// command, a meta line, background work still running ("BackgroundRunning" has no bound, so it earns no hold), an
/// exited process ("Idle"), a conversation the stored turns do not yet show the end of, and every agent the Director
/// derives no state for (today all but Claude Code) - so a long silent command under such an agent still reads as
/// stopped. That is a known limit of what those agents tell us, not a default standing in for a signal that exists.
/// </summary>
public static class SessionTurnState
{
    /// <summary>How many of the conversation's last turns the reading looks at. Parallel tool calls are written one
    /// line each, so the block of calls at the end can span several turns; twenty covers any block the agent emits in
    /// one reply.</summary>
    public const int TailLength = 20;

    /// <summary>
    /// TOOLS THAT WAIT ON A PERSON. A call to one of these is written to the conversation before it runs and has no
    /// result for as long as the person takes, so an open call is a question to a person, not work:
    ///  - <c>AskUserQuestion</c>: the agent's multiple-choice question. Measured on this machine: one call waited
    ///    45 minutes for its answer.
    ///  - <c>ExitPlanMode</c>: the agent asks the person to approve its plan.
    /// Any OTHER tool can wait on a person too, when the terminal shows it blocked on a permission prompt - that one is
    /// read from the terminal detector (<see cref="TerminalWaitingOnPermission"/>), because the conversation cannot
    /// tell a prompted call from a running one.
    /// </summary>
    public static readonly IReadOnlySet<string> ToolsThatWaitOnAPerson =
        new HashSet<string>(StringComparer.Ordinal) { "AskUserQuestion", "ExitPlanMode" };

    /// <summary>The terminal detector's activity state for a session blocked on a permission prompt.</summary>
    public const string TerminalWaitingOnPermission = "WaitingForPerm";

    /// <summary>The start of a user-role line that is not a prompt from the person: an interrupt the person pressed,
    /// or the output and echo of a local command the agent never sees as a turn.</summary>
    private static readonly string[] NotAPrompt =
    {
        "[Request interrupted by user",
        "<local-command-",
        "<command-name>",
        "<command-message>",
        "<command-args>",
        "<bash-input>",
        "<bash-stdout>",
        "<bash-stderr>",
    };

    /// <summary>The reading for one owned session. <paramref name="tail"/> is the end of its stored conversation, or
    /// null when nothing has been pushed for it.</summary>
    public static OwnedTurnReading Read(SessionTurnTail? tail, SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var terminal = TerminalRule(session);
        if (terminal.InTurn) return terminal;
        if (tail is null || !tail.IsSupported || string.IsNullOrEmpty(tail.HistoryState)) return terminal;

        var main = tail.Turns.Where(t => !t.IsSidechain).ToList();
        if (main.Count == 0) return terminal;
        var last = main[^1];

        switch (tail.HistoryState)
        {
            case "Working":
                if (IsAssistant(last) && last.Parts.Any(IsToolCall))
                {
                    if (string.Equals(session.ActivityState, TerminalWaitingOnPermission, StringComparison.Ordinal))
                        return terminal with { Rule = "terminal: a tool call blocked on a permission prompt" };
                    var calls = TrailingToolCalls(main);
                    if (calls.Any(c => c.ToolName is { } name && ToolsThatWaitOnAPerson.Contains(name)))
                        return terminal with { Rule = "terminal: a tool call waiting on a person" };
                    return new OwnedTurnReading(true, null, "conversation: a tool call running for the agent");
                }
                if (IsRealPrompt(last))
                    return new OwnedTurnReading(true, null, "conversation: a prompt with no reply yet");
                return terminal with { Rule = "terminal: the conversation ends in neither a running tool nor a prompt" };

            case "NeedsYou":
                if (IsAssistant(last) && !last.Parts.Any(IsToolCall) && last.TimestampUtc is { } endedAt)
                    return new OwnedTurnReading(false, endedAt, "conversation: the turn ended");
                return terminal with { Rule = "terminal: the stored conversation does not show the turn's end" };

            case "BackgroundRunning":
                return terminal with { Rule = "terminal: background work has no bound" };

            case "Idle":
                return terminal with { Rule = "terminal: no conversation or an exited process" };

            default:
                FileLog.Write($"[SessionTurnState] Read: session={session.SessionId} carries unrecognised history state '{tail.HistoryState}'; read from its terminal");
                return terminal with { Rule = "terminal: unrecognised history state" };
        }
    }

    private static OwnedTurnReading TerminalRule(SessionDto session)
    {
        var working = string.Equals(session.ActivityState, "Working", StringComparison.Ordinal);
        return new OwnedTurnReading(working, session.LastActivityAt,
            working ? "terminal: printing" : "terminal: silent");
    }

    private static bool IsAssistant(StoredTurn turn) => string.Equals(turn.Role, "Assistant", StringComparison.Ordinal);

    private static bool IsToolCall(HistoryPartDto part) => string.Equals(part.Kind, "ToolUse", StringComparison.Ordinal);

    /// <summary>Every tool call in the block of assistant turns the conversation ends with.</summary>
    private static List<HistoryPartDto> TrailingToolCalls(List<StoredTurn> main)
    {
        var calls = new List<HistoryPartDto>();
        for (var i = main.Count - 1; i >= 0 && IsAssistant(main[i]); i--)
            calls.AddRange(main[i].Parts.Where(IsToolCall));
        return calls;
    }

    private static bool IsRealPrompt(StoredTurn turn)
    {
        if (!string.Equals(turn.Role, "User", StringComparison.Ordinal) || turn.IsMeta) return false;
        var texts = turn.Parts.Where(p => string.Equals(p.Kind, "Text", StringComparison.Ordinal)).ToList();
        if (texts.Count == 0 || texts.Count != turn.Parts.Count) return false;
        var start = texts[0].Text.TrimStart();
        return !NotAPrompt.Any(prefix => start.StartsWith(prefix, StringComparison.Ordinal));
    }
}
