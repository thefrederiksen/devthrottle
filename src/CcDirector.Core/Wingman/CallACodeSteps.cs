using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>One tool use from the agent's last turn: the tool's name and its input as the raw JSON the
/// conversation stored. The last turn is every agent message after the last thing the person typed.</summary>
public sealed record CallAToolUse(string Name, string Input);

/// <summary>
/// What decided a stop, and why, in words a person can read. <see cref="Step"/> is one of the closed words in
/// <see cref="CallACodeSteps"/> - a code step, or <see cref="CallACodeSteps.ModelStep"/> when no code step fired and
/// the model answered.
/// </summary>
public sealed record CallADecision(string Step, string Word, string Reason);

/// <summary>
/// CALL A, CODE FIRST (the turn pipeline mission, design v2, approved by the owner on 26 September 2026). Before the
/// model is asked whether a stop needs its owner, four plain rules are tried in order, and the FIRST that fires
/// decides. Only a stop none of them decides reaches the model.
///
///   1. picker     a picker or permission prompt is drawn on the screen (<see cref="PickerOnScreen"/>, the one
///                 footer rule - not a second one)                                           -> needs-you
///   2. verdict    the agent's last message carries its own CC-DISMISS block saying needs-human -> needs-you
///   3. question   the agent's latest reply asks the person a real question                  -> needs-you
///   4. way-back   the agent set itself a way back in its last turn: a ScheduleWakeup, a Monitor, a session
///                 spawn or a background run                                                   -> carrying-on
///
/// Every needs-you rule comes first, because a missed "needs you" is the costly error: the session sits and nobody
/// comes. The rules are ported from the phase 1 measurement (devthrottle_internal
/// docs/missions/turn-pipeline-2026-09-25/phase-1/code_first.py), which decided 191 of 381 labelled stops with them
/// and the phrase list together.
///
/// DELIBERATELY NOT HERE:
///   - the phrase list ("your call", "waiting on you" ...). The owner: only after it is checked against his own
///     labels. It was read off the same stops it was scored on, and it is this fleet's house style.
///   - "owns sessions that are still working" means carrying-on. The data says the opposite: a manager waiting on
///     the owner while its workers run was labelled needs-you 56 times of 72.
///   - an ask-the-user tool use (AskUserQuestion, ExitPlanMode). It fired on none of the 381 stops and is not in the
///     approved list; a live one is drawn as a picker, which step 1 already reads.
///
/// A FAILURE WITH NO REPLY IS NEVER CALM. On a terminal-failure stop the way-back step does not fire: the contract has
/// always refused a calm answer on that shape, and a code step does not get to give one the model may not.
///
/// Pure: no model call, no read, no state. Every decision carries its step and its reason, so a red row the code made
/// can say why.
/// </summary>
public static class CallACodeSteps
{
    /// <summary>A picker or permission prompt is drawn on the screen.</summary>
    public const string PickerStep = "picker";

    /// <summary>The agent's own CC-DISMISS block says needs-human.</summary>
    public const string AgentVerdictStep = "agent-verdict";

    /// <summary>The agent's latest reply asks the person a question.</summary>
    public const string QuestionStep = "question";

    /// <summary>The agent set itself a way back in its last turn.</summary>
    public const string WayBackStep = "way-back";

    /// <summary>No code step fired, so the model decided.</summary>
    public const string ModelStep = "model";

    /// <summary>Every step word, in the order the steps run, then the model.</summary>
    public static readonly IReadOnlyList<string> Steps = new[] { PickerStep, AgentVerdictStep, QuestionStep, WayBackStep, ModelStep };

    /// <summary>The longest question quoted in a reason. A reason is read in a debug view, not a transcript.</summary>
    public const int MaxQuotedQuestionChars = 160;

    /// <summary>
    /// Run the four steps in order and return the first that fires, or null when none does and the model must decide.
    /// </summary>
    /// <param name="package">The stop: its screen, its agent, its shape and the agent's latest reply.</param>
    /// <param name="lastTurnToolUses">The tool uses of the agent's last turn. Empty when the conversation holds none
    /// or could not be read - then the way-back step cannot fire, which leaves the stop to the model.</param>
    public static CallADecision? Decide(TurnVerdictPackage package, IReadOnlyList<CallAToolUse> lastTurnToolUses)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(lastTurnToolUses);

        var picker = PickerOnScreen.Read(package.ScreenRows, package.AgentKind);
        if (picker.Drawn)
            return Decided(PickerStep, TurnVerdictContract.NeedsYouWord,
                "a picker or permission prompt is drawn on the screen (" + string.Join(", ", picker.Signs) + ")");

        var reply = (package.LatestReply ?? "").Trim();

        if (DismissVerdictSignal.ParseLatest(reply) is { Verdict: DismissVerdict.NeedsHuman } signal)
            return Decided(AgentVerdictStep, TurnVerdictContract.NeedsYouWord,
                "the agent's own CC-DISMISS block says needs-human"
                + (signal.Reason.Length > 0 ? ": " + Quote(signal.Reason) : ""));

        var questions = AskedQuestions(reply);
        if (questions.Count > 0)
            return Decided(QuestionStep, TurnVerdictContract.NeedsYouWord,
                "the reply asks a question: " + Quote(questions[^1]));

        if (package.Kind != TurnVerdictPackageKind.TerminalFailure && WayBack(lastTurnToolUses) is { } wayBack)
            return Decided(WayBackStep, TurnVerdictContract.CarryingOnWord,
                "the agent set itself a way back in its last turn: " + wayBack);

        return null;
    }

    private static CallADecision Decided(string step, string word, string reason)
    {
        FileLog.Write($"[CallACodeSteps] Decide: step={step} word={word}");
        return new CallADecision(step, word, reason);
    }

    // ==================================================================== step 3: a real question

    /// <summary>A heading or a list item: "## Can we see it? Yes.", "- **Speed** - is it fast?".</summary>
    private static readonly Regex ListOrHeading = new(@"^\s*(#|[-*+]\s|\d+[.)]\s)", RegexOptions.Compiled);

    /// <summary>A quoted or italic question: 'the "What is Codex?" explainer', '*what do the rules need?*'. The
    /// closing curly quote is written as its code point so this file stays plain keyboard text.</summary>
    private static readonly Regex Quoted = new("\\?[\"'\\u201d]|(?<!\\*)\\?\\*(?!\\*)", RegexOptions.Compiled);

    /// <summary>A question the same line answers itself: "**Do I need anything? No blockers.**".</summary>
    private static readonly Regex SelfAnswered = new(@"\?\**\s+(yes|no)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+|\n+", RegexOptions.Compiled);

    /// <summary>
    /// The sentences of a reply that ask the person something, in order. Not counted: a heading or list item, a
    /// quoted or italic question, and a question the same line answers itself. Ported from code_first.py's
    /// asked_questions, which agreed with the label on 28 of the 28 stops it decided.
    /// </summary>
    public static IReadOnlyList<string> AskedQuestions(string? reply)
    {
        var found = new List<string>();
        foreach (var line in (reply ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.Contains('?') || ListOrHeading.IsMatch(line) || Quoted.IsMatch(line) || SelfAnswered.IsMatch(line))
                continue;
            foreach (var sentence in SentenceBreak.Split(line))
            {
                var s = sentence.Trim();
                if (s.Length > 0 && IsQuestion(s)) found.Add(s);
            }
        }
        return found;
    }

    private static bool IsQuestion(string sentence) => sentence.TrimEnd('*', '_', '`', ' ', ')').EndsWith('?');

    // ==================================================================== step 4: a way back

    /// <summary>
    /// The first tool use of the last turn that set up the agent's own way back, named for a reason, or null.
    /// Ported from code_first.py's wait-tool rule: 55 of the 58 stops it fired on were labelled carrying-on.
    /// </summary>
    private static string? WayBack(IReadOnlyList<CallAToolUse> toolUses)
    {
        foreach (var use in toolUses)
        {
            var name = use.Name ?? "";
            var input = use.Input ?? "";
            if (name is "ScheduleWakeup" or "Monitor") return "a " + name + " call";
            if (input.Contains("session spawn", StringComparison.Ordinal)) return "a session spawn";
            if (input.Replace(" ", "").Contains("\"run_in_background\":true", StringComparison.Ordinal)) return "a background run";
        }
        return null;
    }

    private static string Quote(string text)
    {
        var t = text.Trim();
        if (t.Length > MaxQuotedQuestionChars) t = t[..MaxQuotedQuestionChars].TrimEnd() + "...";
        return "\"" + t + "\"";
    }
}
