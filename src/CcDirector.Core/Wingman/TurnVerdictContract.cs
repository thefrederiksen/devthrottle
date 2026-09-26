using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// THE turn-verdict contract - CALL A: the one question the Wingman asks the model about a stop that no code step
/// decided (<see cref="CallACodeSteps"/>), and the mechanical reading of its one-word answer.
///
/// Everything here is pure - no model calls, no input or output beyond reading the embedded prompt and
/// writing a log line. Reading the answer is MECHANICAL and never interpretive: the answer is one of three
/// words or it is refused. Nothing here reads the answer for sense.
///
/// THE DIRECTION OF EVERY DOUBT. A calm verdict takes a session out of the owner's queue, so a wrong
/// calm verdict is the one that goes unnoticed - the session sits there and nobody comes. Every rule
/// here therefore fails AWAY from calm: a refused answer leaves the row exactly as the detector left
/// it, which is red. Silence is never a decision, and a broken answer never moves a row toward quiet.
///
/// The prompt lives in Prompts/turn-verdict-v4.txt as an embedded resource rather than in this file,
/// because the grading tool in the internal repository renders the SAME prompt from the SAME bytes. A
/// prompt that existed twice would be graded in one version and shipped in another.
/// </summary>
public static class TurnVerdictContract
{
    /// <summary>Stamped on every verdict record so a stored answer can say which contract produced it.
    /// Bump on every change to the prompt or to validation, and rename the prompt file with it.
    ///
    /// v1: the judge contract as slice C shipped it. v2 (slice D): the prompt gained finishedKind and the
    /// owned-sessions facts, and validation requires finishedKind on a finished verdict and refuses it on
    /// every other. Records stored under v1 keep their v1 stamp and stay readable; nothing reads the
    /// version to decide whether a record may be shown.
    ///
    /// v2.1 (2026-09-16): the SPOKEN section only. The judge no longer writes the session title - it is
    /// prepended from the record after the answer - and it is told plainly that no downstream step LOOKS
    /// FOR an identifier it leaves in. (A general Markdown pass does run before synthesis and drops fenced
    /// blocks whole; it is not identifier-specific and is no safety net, which is what the judge is told.) An earlier draft of this note said the opposite, promising a scrub that
    /// was written, reviewed three times and then deleted because no pattern separates an identifier from
    /// a number in prose without deleting real answers. Nothing about the JSON shape or validation changed, which is why the
    /// resource file keeps its v2 name and the grading tool keeps its path: renaming it would move a file
    /// the grader reads, for a revision that cannot change how any stored record is read. The stamp still
    /// moves, so a record can say which wording produced it.
    ///
    /// v2.2 (2026-09-16, slice I): VALIDATION only - the prompt is byte for byte the v2.1 prompt. A REFUSED
    /// answer that was readable JSON keeps its spoken text on the failed record, so a field check no longer
    /// silences voice; the refusal still stands for the row. (A rewrite of the spoken section was graded in the
    /// same slice and withdrawn on the Architect's ruling: on the fast judge it did not reach the old
    /// translator's fidelity and raised refusals.) The JSON shape is unchanged, so the file keeps its v2 name.
    ///
    /// v3 (2026-09-18, the owner's ruling on the Wingman redesign report): TWELVE FIELDS BECOME FIVE, and the prompt
    /// is a new file. The judge is asked for "state", "label", "agentRecommends", "menu" and "options" and for
    /// nothing else. Gone: "spoken", "summary", "evidence", "risk", "confidence", "answerVia" and "finishedKind" -
    /// the last folded into "state", and "answerVia" derived from whether a menu was drawn. Measured on the live
    /// fleet that morning, 40 per cent of readings failed: 48 of 198 timed out at sixty seconds and 30 were refused
    /// by a content rule, most of them the verbatim receipt. Every one of those seven fields was a further way for a
    /// fast model to fail a shape check on a call every session pays for at every stop, and four of them never
    /// reached a screen at all.
    ///
    /// THE RECEIPT IS GONE, AND THAT IS A COST TAKEN KNOWINGLY. It was a machine-checkable anchor against invention,
    /// and nothing replaces it. It could not be satisfied: the agent's reply carries Markdown, so a judge quoting the
    /// sentence the way a person reads it wrote "Three new contacts" where the reply held it in asterisks, and the
    /// comparison is word for word. A check nothing can pass is not a check, it is an outage with a reason attached -
    /// and it threw away the whole answer, label and words and all, so the owner read "The Wingman could not explain
    /// this stop" above a complete and correct briefing.
    ///
    /// WHAT DID NOT CHANGE: every rule that decides what BYTES reach a live session. The options' shape, the menu's
    /// shape, the one-option refusal, the at-most-one-recommended refusal and <see cref="ValidateExecutable"/> are
    /// exactly as they were. Those guard a button that ACTS; the seven removed fields guarded prose.
    ///
    /// v3.1 (2026-09-23, issue devthrottle_internal#2243): PROMPT ONLY - the JSON shape is byte for byte the v3
    /// shape, so the resource file keeps its v3 name, exactly as the v2.1 revision kept v2's. The "carrying-on"
    /// definition gains the exclusion its wording had left open: a reply that reports the work complete is one of
    /// the two finisheds, never carrying-on, however long the session stays open or what a later round will do.
    /// On the live fleet a finished report ("This week's round is done... this session stays open for your
    /// changes") was read as carrying-on, and the carrying-on clock then expired it and narrated that it "did
    /// not continue" over its own words saying it was done.
    ///
    /// v4 (2026-09-26, the turn pipeline mission, design v2 approved by the owner): ONE WORD, AND CODE FIRST. The
    /// five fields become one word - needs-you, done or carrying-on - and a stop reaches the model only when none of
    /// the four code steps in <see cref="CallACodeSteps"/> decided it. The prompt is a new file: the wording measured
    /// in phase 1 as "v3 + reply" at temperature 0 (devthrottle_internal
    /// docs/missions/turn-pipeline-2026-09-25/phase-1/call-a-prompt-v3-reply.txt) with the owned-sessions line taken
    /// out, because the data says owning working sessions points the other way and it is not fed until measured. It is
    /// given every visible screen row and the agent's latest reply, and nothing else: no conversation, no recent
    /// turns, no first ask, no previous label. The label, what the agent recommends, the menu and the options are no
    /// longer asked for - the label moves to the narration call in phase 4, and until then a v4 record carries none
    /// and the row shows its plain state. A record stored under v3 keeps its v3 stamp and its old fields and still
    /// renders.</summary>
    public const string Version = "v4";

    /// <summary>The embedded name of the prompt template. A test pins the embedded copy to the file at
    /// <see cref="PromptResourcePath"/> byte for byte.</summary>
    public const string PromptResourceName = "CcDirector.Core.Wingman.Prompts.turn-verdict-v4.txt";

    /// <summary>The repository-relative path of the same file, for the tool that reads it off disk and
    /// for the test that pins the embedded copy to it.</summary>
    public const string PromptResourcePath = "src/CcDirector.Core/Wingman/Prompts/turn-verdict-v4.txt";

    // ==================================================================== the prompt

    /// <summary>Every placeholder the template may contain. The template is filled in ONE pass, so a
    /// value that happens to contain a placeholder is never rescanned - a screen row reading
    /// "{{SCREEN_ROWS}}" is inert text, not a way into the prompt.</summary>
    public static readonly IReadOnlyList<string> PromptPlaceholders = new[]
    {
        "ALTERNATE_SCREEN",
        "CURSOR_ROW",
        "SCREEN_ROWS",
        "LATEST_REPLY",
    };

    /// <summary>What an absent fact is written as. One string for all of them, so the judge never has
    /// to tell "we did not look" apart from "there was nothing" by the shape of a blank line.</summary>
    public const string AbsentValue = "(none)";

    private static string? _promptTemplate;

    /// <summary>The prompt template exactly as embedded. Throws when the resource is missing, because a
    /// build that shipped without its prompt cannot judge anything and must say so loudly rather than
    /// send an empty question to a model.</summary>
    public static string PromptTemplate
    {
        get
        {
            if (_promptTemplate is not null) return _promptTemplate;
            var assembly = typeof(TurnVerdictContract).Assembly;
            using var stream = assembly.GetManifestResourceStream(PromptResourceName)
                ?? throw new InvalidOperationException(
                    $"The turn-verdict prompt resource '{PromptResourceName}' is not embedded in "
                    + $"{assembly.GetName().Name}. It is declared as an EmbeddedResource in "
                    + "CcDirector.Core.csproj and its source is " + PromptResourcePath + ".");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _promptTemplate = reader.ReadToEnd();
            return _promptTemplate;
        }
    }

    /// <summary>
    /// The whole question asked of the model for one stop: every visible screen row, the cursor row, the full-screen
    /// flag, and the agent's latest reply in full. NOTHING ELSE - no conversation, no recent turns, no first ask, no
    /// previous label and no owned-sessions line (design v2). A terminal-failure stop has no reply; its failure is on
    /// the screen, which the model is given whole.
    /// </summary>
    public static string BuildPrompt(TurnVerdictPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ALTERNATE_SCREEN"] = package.IsAlternateScreen ? "yes" : "no",
            ["CURSOR_ROW"] = package.CursorRow >= 0 ? package.CursorRow.ToString() : AbsentValue,
            ["SCREEN_ROWS"] = package.ScreenRows.Count == 0
                ? AbsentValue
                : string.Join("\n", package.ScreenRows),
            ["LATEST_REPLY"] = Present(package.LatestReply),
        };

        return Fill(PromptTemplate, values);
    }

    /// <summary>
    /// Fill the template in ONE left-to-right pass. A filled value is never rescanned, so no screen
    /// row, reply or title can introduce a placeholder of its own. An unknown placeholder THROWS: a
    /// template carrying a name the code does not fill is a defect in this pair of files, and sending
    /// the model a question with a literal "{{...}}" in it would hide that defect behind a worse
    /// answer.
    /// </summary>
    private static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length + 8_192);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(template, i, template.Length - i); break; }
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }

            var name = template[(open + 2)..close];
            if (!values.TryGetValue(name, out var value))
                throw new InvalidOperationException(
                    $"The turn-verdict prompt template contains the placeholder '{{{{{name}}}}}', which "
                    + "nothing fills. Add it to TurnVerdictContract.PromptPlaceholders and give it a value "
                    + "in BuildPrompt, or take it out of " + PromptResourcePath + ".");

            sb.Append(template, i, open - i);
            sb.Append(value);
            i = close + 2;
        }
        return sb.ToString();
    }

    private static string Present(string? value)
        => string.IsNullOrWhiteSpace(value) ? AbsentValue : value;

    // ==================================================================== the answer

    /// <summary>The stop needs its owner: red, stored as needed-you.</summary>
    public const string NeedsYouWord = TurnVerdictStates.NeedsYou;

    /// <summary>The turn is over and nothing is asked: cyan, stored as finished (kind done).</summary>
    public const string DoneWord = "done";

    /// <summary>Nothing is asked and the session will act again by itself: purple, stored as continues-alone.</summary>
    public const string CarryingOnWord = TurnVerdictStates.CarryingOn;

    /// <summary>The three words Call A may answer, and the only three.</summary>
    public static readonly IReadOnlyList<string> Words = new[] { NeedsYouWord, DoneWord, CarryingOnWord };

    /// <summary>The longest stretch of a refused answer quoted in its reason.</summary>
    public const int MaxQuotedAnswerChars = 40;

    /// <summary>How the person answers on every v4 record: in words. Call A no longer reads a menu; a picker on the
    /// screen is simply needs-you (the owner, 25 September: "A menu is simply needs you").</summary>
    public const string AnswerViaReply = "reply";

    /// <summary>
    /// Read the model's answer. NEVER returns null: an answer that is not exactly one of <see cref="Words"/> comes
    /// back as a record with <see cref="TurnVerdictDto.Failed"/> set and the reason in plain words, so a refusal is
    /// stored and answerable by query exactly as an accepted answer is - and a failed record is red.
    ///
    /// The answer is trimmed, stripped of surrounding backticks, quotes and a full stop, and lower-cased - the same
    /// reading the phase 1 measurement applied - and must then BE one of the three words. Nothing else is searched
    /// for inside it: "done, but needs-you" is not an answer.
    /// </summary>
    /// <param name="raw">The model's answer, exactly as it came back.</param>
    /// <param name="package">The package the answer was asked about.</param>
    /// <param name="model">Which model answered. Stored, and required: a record that cannot say who answered cannot
    /// be graded.</param>
    /// <param name="turnEndObservedAtUtc">When the detector observed the stop - the join key to the turn log.</param>
    public static TurnVerdictDto ParseWord(
        string? raw,
        TurnVerdictPackage package,
        string model,
        DateTime turnEndObservedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(raw))
            return Refuse(package, model, turnEndObservedAtUtc, "the model answered nothing");

        var word = ReadWord(raw);
        if (!Words.Contains(word, StringComparer.Ordinal))
            return Refuse(package, model, turnEndObservedAtUtc,
                $"the answer {Quoted(Cut(raw.Trim()))} is not one of the three words " + string.Join(", ", Words));

        // A failure with no reply is never calm - the one rule the contract has always kept about the shape of a stop.
        if (package.Kind == TurnVerdictPackageKind.TerminalFailure && word != NeedsYouWord)
            return Refuse(package, model, turnEndObservedAtUtc,
                $"the answer {Quoted(word)} is calm, and this stop has no reply at all - its turn ended on a failure "
                + "shown on screen, which cannot mean the session is done or carrying on");

        return Accepted(package, model, turnEndObservedAtUtc, word, CallACodeSteps.ModelStep,
            $"no code step fired; the model answered {Quoted(word)}");
    }

    /// <summary>The record a code step's decision becomes: accepted, with no model call and no model named.</summary>
    /// <param name="model">What decided, as it is stored in the record's model column - <see cref="CodeModel"/>.</param>
    public static TurnVerdictDto FromCodeStep(
        CallADecision decision,
        TurnVerdictPackage package,
        DateTime turnEndObservedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(package);
        return Accepted(package, CodeModel, turnEndObservedAtUtc, decision.Word, decision.Step, decision.Reason);
    }

    /// <summary>What a record decided by a code step names as its model: no model was asked.</summary>
    public const string CodeModel = "code";

    /// <summary>The stored verdict spelling of a word: needs-you = needed-you, done = finished (kind done),
    /// carrying-on = continues-alone. The stored words are the ones every earlier record and the corpus carry.</summary>
    public static (string Verdict, string? FinishedKind) StoredWords(string word)
        => TurnVerdictVocabulary.SplitState(word == DoneWord ? TurnVerdictStates.FinishedDone : word);

    private static TurnVerdictDto Accepted(
        TurnVerdictPackage package, string model, DateTime turnEndObservedAtUtc, string word, string step, string reason)
    {
        var (verdict, finishedKind) = StoredWords(word);
        return new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = DateTime.UtcNow,
            TurnEndObservedAtUtc = turnEndObservedAtUtc,
            ScreenHash = package.ScreenHash,
            Model = model,
            ContractVersion = Version,
            PackageKind = TurnVerdictPackage.WireName(package.Kind),
            Failed = false,
            FailureReason = null,
            Verdict = verdict,
            FinishedKind = finishedKind,
            // No label from Call A (design v2): it moves to the narration call in phase 4. Until then the row shows
            // its plain state, exactly as it does for any reading with no label.
            Label = "",
            AnswerVia = AnswerViaReply,
            DecidedBy = step,
            DecisionReason = reason,
        };
    }

    private static string ReadWord(string raw)
        => raw.Trim().Trim('`', '\'', '"', '.').Trim().ToLowerInvariant();

    private static string Cut(string text)
        => text.Length <= MaxQuotedAnswerChars ? text : text[..MaxQuotedAnswerChars] + "...";

    /// <summary>A word from the model, in single quotes, for a refusal reason a person reads.</summary>
    private static string Quoted(string word) => "'" + word + "'";

    /// <summary>A refused answer: a failed record, red, on the retry schedule.</summary>
    private static TurnVerdictDto Refuse(
        TurnVerdictPackage package,
        string model,
        DateTime turnEndObservedAtUtc,
        string reason)
    {
        FileLog.Write($"[TurnVerdictContract] refused: {reason}");
        return new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = DateTime.UtcNow,
            TurnEndObservedAtUtc = turnEndObservedAtUtc,
            ScreenHash = package.ScreenHash,
            Model = model,
            ContractVersion = Version,
            PackageKind = TurnVerdictPackage.WireName(package.Kind),
            Failed = true,
            FailureReason = reason,
            DecidedBy = CallACodeSteps.ModelStep,
            DecisionReason = reason,
        };
    }
}
