namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE ONE WORD A READING ANSWERS WITH, from contract v3 (the owner's ruling on the Wingman redesign report,
/// 18 September 2026): what happened, in one word. It drives the colour of a row and where it sorts, and it is
/// the only state word any client is told.
///
/// IT IS THE OLD VERDICT WORD WITH finishedKind FOLDED INTO IT, and nothing else changed. "finished" plus a
/// separate kind was two fields saying one thing, and the second was a member the judge had to remember to put on
/// exactly one verdict and leave off the other five - a rule that refused whole answers on its own. So the two
/// finisheds became two words, and the two awkward hyphenations became the words a person would say.
///
/// THE STORED SPELLING IS NOT RENAMED, and that is deliberate. <c>TurnVerdictDto.Verdict</c> keeps the six words
/// because every record ever written carries one, and because the labelling corpus in the internal repository is
/// graded on those exact words - renaming them would silently split a corpus label from a live reading. So this
/// is the SURFACE and that is the STORE, and <see cref="VerdictFor"/> and <see cref="Of"/> are the one place the
/// two spellings meet. Nothing else in the product converts between them.
///
/// THIS LIVES IN CONTRACTS, NOT IN CORE, because <c>TurnVerdictDto.State</c> folds it and Contracts may not
/// reference Core. The six verdict words appear here as literals and again in <c>TurnVerdictVocabulary</c>;
/// a test pins the two together, exactly as the vocabulary is already pinned to the labelling tool.
/// </summary>
public static class TurnVerdictStates
{
    /// <summary>It stopped and it is waiting for you.</summary>
    public const string NeedsYou = "needs-you";

    /// <summary>The work is complete. Nothing to read, nothing to answer.</summary>
    public const string FinishedDone = "finished-done";

    /// <summary>The work is complete AND there is something for you to read.</summary>
    public const string FinishedReport = "finished-report";

    /// <summary>It stopped to tell you something and is still going. Nothing is needed.</summary>
    public const string CarryingOn = "carrying-on";

    /// <summary>It hit a problem it can probably get itself out of.</summary>
    public const string StuckRecoverable = "stuck-recoverable";

    /// <summary>It hit a problem it cannot get out of alone.</summary>
    public const string StuckNeedsPerson = "stuck-needs-person";

    /// <summary>The screen does not support a judgement. Say so rather than guess.</summary>
    public const string CannotTell = "cannot-tell";

    /// <summary>The seven words a v3 answer may carry. Anything else rejects the whole answer.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        NeedsYou, FinishedDone, FinishedReport, CarryingOn, StuckRecoverable, StuckNeedsPerson, CannotTell,
    };

    /// <summary>What each word means, for the prompt and for a reader of this file.</summary>
    public static readonly IReadOnlyDictionary<string, string> Meanings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NeedsYou] = "It stopped and it is waiting for you.",
            [FinishedDone] = "The work is complete. Nothing to read, nothing to answer.",
            [FinishedReport] = "The work is complete and there is something for you to read.",
            [CarryingOn] = "It stopped to tell you something and is still going. Nothing is needed.",
            [StuckRecoverable] = "It hit a problem it can probably get itself out of.",
            [StuckNeedsPerson] = "It hit a problem it cannot get out of alone.",
            [CannotTell] = "The screen does not support a judgement. Say so rather than guess.",
        };

    /// <summary>True when the word is one of the seven.</summary>
    public static bool IsState(string? word) => word is not null && All.Contains(word, StringComparer.Ordinal);

    /// <summary>
    /// The state word split back into the stored pair: the verdict word every record and the corpus carry, and
    /// the finished kind, null on every state but the two finisheds. Throws on a word that is not one of the
    /// seven, because a caller that has not checked must not be handed a silent default.
    /// </summary>
    public static (string Verdict, string? FinishedKind) VerdictFor(string state) => state switch
    {
        NeedsYou => ("needed-you", null),
        FinishedDone => ("finished", "done"),
        FinishedReport => ("finished", "report"),
        CarryingOn => ("continues-alone", null),
        StuckRecoverable => ("stuck-recoverable", null),
        StuckNeedsPerson => ("stuck-needs-person", null),
        CannotTell => ("cannot-tell", null),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state,
            "not one of the seven state words: " + string.Join(", ", All)),
    };

    /// <summary>
    /// The state word for a stored pair - the other direction of <see cref="VerdictFor"/>, so a record written
    /// before v3 still reads as one of the seven words a client renders.
    ///
    /// A "finished" record with NO kind - every finished record stored before 15 September 2026 - reads as
    /// <see cref="FinishedReport"/>. That is the honest answer when nothing said which of the two it was: it
    /// offers the reader the body rather than telling him there is nothing there. Empty for a refused record,
    /// and for any word not in the list.
    /// </summary>
    public static string Of(string? verdict, string? finishedKind) => verdict switch
    {
        "needed-you" => NeedsYou,
        "finished" => string.Equals(finishedKind, "done", StringComparison.Ordinal) ? FinishedDone : FinishedReport,
        "continues-alone" => CarryingOn,
        "stuck-recoverable" => StuckRecoverable,
        "stuck-needs-person" => StuckNeedsPerson,
        "cannot-tell" => CannotTell,
        _ => "",
    };
}
