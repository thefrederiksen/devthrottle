using System.Text;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// What checking a stated reason against the screen it was given produced. <see cref="Statement"/> is never
/// empty - that is the point of it. A firing must always be able to say what the grounding check found,
/// including that there was nothing to check, so that a run in which the check NEVER RAN cannot look
/// identical to a run in which it ran and found nothing wrong.
/// </summary>
public sealed record RuleGrounding(
    bool IsGrounded, string Statement, IReadOnlyList<string> NotOnTheScreen, bool HasCitation)
{
    /// <summary>
    /// Whether this reason may carry an ACT. It must CITE something - a passage long enough to be a claim
    /// about the screen - and everything it cites must be on the screen.
    ///
    /// The two halves are separate on purpose. <see cref="IsGrounded"/> answers "is anything it said about
    /// the screen wrong", which is the right question for a DECLINE: declining does nothing, so an
    /// unfaithful decline is recorded as it happened with the mismatch noted. Acting is the direction that
    /// touches the world, and for that "nothing it said was wrong" is not enough, because a reason that
    /// says nothing checkable is never wrong.
    /// </summary>
    public bool CanCarryAnAct => HasCitation && IsGrounded;
}

/// <summary>
/// AN ACT'S REASON MUST BE GROUNDED IN THE SCREEN IT WAS GIVEN (Architect ruling A12).
///
/// This exists because of one live run. A rule declined, and its recorded reason quoted a sentence that was
/// not on the screen the firing record stores - the words had been on that session's screen twelve minutes
/// earlier, in an unrelated run. The decline was safe, because declining is the direction that does
/// nothing. THE SAME UNFAITHFULNESS IN THE OTHER DIRECTION IS A RULE ACTING ON EVIDENCE THAT WAS NOT
/// THERE, and that is the sharpest thing this mission has learned about its own design.
///
/// So: an ACT whose stated reason quotes text the screen does not contain is REFUSED. A DECLINE that does
/// the same is recorded as it happened - declining is safe and the record should show what actually
/// occurred - but recorded with the mismatch NOTED, so the unfaithfulness is visible rather than smoothed
/// over.
///
/// AND AN ACT MUST CITE SOMETHING AT ALL. The first version of this refused a reason that quoted words the
/// screen does not contain, and called a reason that quoted NOTHING grounded - "there was nothing to
/// check". An agent could then avoid the whole bound by writing a plausible sentence with no quotation in
/// it, and act on evidence nobody can go back and verify. That is an ABSENCE being read as a positive
/// result, which is the exact shape this mission's own standard forbids. So an act now needs a citation
/// present AND correct; see <see cref="RuleGrounding.CanCarryAnAct"/>. A decline needs neither, and its
/// record says which it had.
///
/// WHAT IT CHECKS, AND WHAT IT DOES NOT. It checks QUOTED passages: text the reason puts in quotation marks
/// is a claim about what the screen says, and that claim is checkable. It does not check paraphrase, and it
/// cannot - a reason that says the screen "looks like a limit notice" is a judgement, and judging the
/// judgement is what the agent was asked for in the first place. So this is a floor, not a proof of
/// faithfulness, and the report says so. In particular a citation that IS on the screen does not make the
/// conclusion drawn from it correct; it makes the conclusion checkable by a person reading the record.
/// </summary>
public static class RuleReasonGrounding
{
    /// <summary>Quoted passages shorter than this are ignored. A reason that says the word 'act' in quotes
    /// is not making a claim about the screen, and a one-word match against forty lines of terminal output
    /// would be noise in both directions.</summary>
    public const int ShortestCheckablePassage = 8;

    /// <summary>The statement a firing carries when its reason is this Gateway's own words rather than the
    /// agent's - a refusal, or an abandonment. There is nothing to check, and saying so is not the same as
    /// saying nothing.</summary>
    public const string NotTheAgentsReason =
        "grounding: not applicable - this reason is the Gateway's own, not the agent's, so there is nothing " +
        "of the agent's to cite or to check.";

    /// <summary>Check a stated reason against the screen it was given.</summary>
    public static RuleGrounding Check(string? reason, string? screenText)
    {
        var quoted = QuotedPassages(reason ?? "");
        if (quoted.Count == 0)
            return new RuleGrounding(
                true,
                "grounding: the reason cites nothing from the screen, so there is nothing on it that can be " +
                "checked. Nothing it said was contradicted; nothing it said was verifiable either.",
                Array.Empty<string>(),
                HasCitation: false);

        var screen = Flatten(screenText);
        var missing = quoted.Where(p => !screen.Contains(Flatten(p), StringComparison.Ordinal)).ToList();

        if (missing.Count == 0)
            return new RuleGrounding(
                true,
                $"grounding: {quoted.Count} passage(s) cited from this screen, all found on it.",
                Array.Empty<string>(),
                HasCitation: true);

        return new RuleGrounding(
            false,
            "grounding: the reason cites text this screen does not contain: " +
            string.Join("; ", missing.Select(m => "'" + m.Trim() + "'")) + ".",
            missing,
            HasCitation: true);
    }

    /// <summary>The quoted passages in a piece of text, long enough to be a claim about the screen.</summary>
    internal static IReadOnlyList<string> QuotedPassages(string text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text)) return found;

        var pairs = new (char Open, char Close)[]
        {
            ('\'', '\''),
            ('"', '"'),
            // The typographic pair, written as escapes so this file stays pure ASCII. A model writing
            // prose uses them without being asked, and a quotation we could not recognise would read as a
            // reason that quoted nothing at all.
            ('\u2018', '\u2019'),
            ('\u201C', '\u201D'),
        };

        foreach (var (open, close) in pairs)
        {
            var index = 0;
            while (index < text.Length)
            {
                var start = NextQuotationMark(text, open, index);
                if (start < 0) break;
                var end = NextQuotationMark(text, close, start + 1);
                if (end < 0) break;
                var passage = text[(start + 1)..end];
                if (passage.Trim().Length >= ShortestCheckablePassage) found.Add(passage);
                index = end + 1;
            }
        }
        return found;
    }

    /// <summary>
    /// The next <paramref name="mark"/> at or after <paramref name="from"/> that is a quotation mark rather
    /// than an apostrophe (ruling G1). A straight single quote or a right single quotation mark with a letter
    /// or digit immediately on BOTH sides is an apostrophe inside a word - "You've", "instruction's" - and
    /// never opens or closes a quoted passage. Without this the September notice, which says "You've", could
    /// not be quoted at all: the apostrophe closed the quotation and the next pair produced a passage that is
    /// not on the screen. One letter-side only ("the agents' turn") is still a quotation mark: deciding it
    /// was a possessive would be a guess about grammar, and a wrong guess would swallow the next quotation.
    /// </summary>
    private static int NextQuotationMark(string text, char mark, int from)
    {
        var at = text.IndexOf(mark, from);
        while (at >= 0 && IsApostrophe(text, at))
            at = text.IndexOf(mark, at + 1);
        return at;
    }

    private static bool IsApostrophe(string text, int at) =>
        (text[at] == '\'' || text[at] == '\u2019')
        && at > 0 && char.IsLetterOrDigit(text[at - 1])
        && at < text.Length - 1 && char.IsLetterOrDigit(text[at + 1]);

    /// <summary>Whitespace collapsed and case dropped, so a quotation that crossed a line wrap on the
    /// terminal is still recognised as the same words.</summary>
    internal static string Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }
}
