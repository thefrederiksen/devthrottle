namespace CcDirector.Core.Wingman;

/// <summary>
/// The closed word lists a turn verdict is allowed to use, and what each word means.
///
/// WHY A FIXED VOCABULARY RATHER THAN FREE TEXT. Two readers - a person labelling a corpus and the
/// model judging a live stop - have to be able to AGREE, and you cannot compare two paragraphs. If
/// one writes "the session was waiting for its owner to answer" and the other writes "needs a
/// person", nothing mechanical can tell whether that is agreement or two different readings, and a
/// pipeline that guesses will manufacture agreement it never had. So the verdict is one word from
/// this list and the reasoning goes in the free-text fields that nothing compares.
///
/// THIS LIST IS ONE OF A PAIR. The labelling tool in the internal repository keeps the same words in
/// its own file (tools/turn-log/verdicts.py, VERSION 2); the meanings below are copied from it word
/// for word so a corpus label and a live verdict mean the same thing. A test in each repository pins
/// the list. Changing a word here without changing it there silently splits the two meanings apart,
/// and every grading number computed across the split is then a comparison of two different
/// questions.
///
/// <see cref="NotATurnEnd"/> is in the list and is NEVER emitted by the Wingman. It belongs to the
/// DETECTOR - it says the boundary fired while the session was still working, which is a fact about
/// the detector rather than a state of the session. The Wingman is asked what a stop MEANS and is
/// only ever asked about stops the detector has already decided happened, so an answer carrying this
/// word is rejected like any other unknown word.
/// </summary>
public static class TurnVerdictVocabulary
{
    /// <summary>The vocabulary version the labelling tool stamps on every label (verdicts.py VERSION).
    /// Records labelled under an older vocabulary do not silently become records under a newer one, so
    /// a corpus that spans a change can still say which meaning was in force.</summary>
    public const int Version = 2;

    // ---------------------------------------------------------------- verdicts

    public const string NeededYou = "needed-you";
    public const string Finished = "finished";
    public const string StuckRecoverable = "stuck-recoverable";
    public const string StuckNeedsPerson = "stuck-needs-person";
    public const string ContinuesAlone = "continues-alone";
    public const string NotATurnEnd = "not-a-turn-end";
    public const string CannotTell = "cannot-tell";

    /// <summary>The six words the Wingman may answer with. Anything else rejects the whole answer.</summary>
    public static readonly IReadOnlyList<string> WingmanVerdicts = new[]
    {
        NeededYou,
        Finished,
        ContinuesAlone,
        StuckRecoverable,
        StuckNeedsPerson,
        CannotTell,
    };

    /// <summary>The six plus the detector's seventh: the whole shared vocabulary, which is what the
    /// labelling tool's list must equal.</summary>
    public static readonly IReadOnlyList<string> AllVerdicts = new[]
    {
        NeededYou,
        Finished,
        StuckRecoverable,
        StuckNeedsPerson,
        ContinuesAlone,
        NotATurnEnd,
        CannotTell,
    };

    /// <summary>The verdicts that say a person is needed. These keep a stop red.</summary>
    public static readonly IReadOnlyList<string> NeedsAPerson = new[] { NeededYou, StuckNeedsPerson };

    /// <summary>The verdicts that drop a stop to a calm colour. A calm verdict is the one that can go
    /// wrong quietly, which is why every rule in the contract leans away from it.</summary>
    public static readonly IReadOnlyList<string> Calm = new[] { Finished, ContinuesAlone };

    /// <summary>The meaning of each verdict word, copied from the labelling tool.</summary>
    public static readonly IReadOnlyDictionary<string, string> VerdictMeanings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NeededYou] =
                "The session was genuinely waiting on a person and could not go on without one. This is the "
                + "verdict the whole needs-me judgement exists to predict. A permission prompt, a question asked "
                + "of the owner, a choice only a human can make.",
            [Finished] =
                "The turn ended, nothing is needed from a person, and the session has nothing further to do - "
                + "the work is complete or it is idle by design. Waking somebody for this is the false positive "
                + "that makes an assistant tiring. If the session intends to carry on by itself, that is "
                + "continues-alone, not this.",
            [StuckRecoverable] =
                "The session stopped on a transient fault - a network error, a rate limit, a dropped "
                + "connection - and would have carried on if something had typed continue. This is what the "
                + "session supervisor exists to catch, so a corpus of these is how its miss rate gets counted.",
            [StuckNeedsPerson] =
                "The session is stopped in a way no automatic retry fixes: a real error in the work, an "
                + "exhausted context, a wrong turn that needs a decision. Typing continue here is not recovery, "
                + "it is noise.",
            [ContinuesAlone] =
                "The turn ended, nothing is needed from a person, and the session will carry on by itself - it "
                + "said so, or it is plainly mid-task with its next step already decided. Distinct from finished "
                + "because the two behave differently AFTERWARDS: a finished session staying quiet is correct, "
                + "while one that said it would continue and then stayed quiet is stuck. Same answer to 'wake the "
                + "owner?', different answer to 'should anything have happened next?'.",
            [NotATurnEnd] =
                "The boundary fired while the session was still working - this was never the end of a turn. A "
                + "detector error, not a session state. Counting these is how the ten-second quiet rule gets "
                + "judged, and they must never be mixed in with real turn ends.",
            [CannotTell] =
                "The record does not contain enough to say. An unreadable screen, a missing conversation, a "
                + "turn whose meaning is genuinely ambiguous. THIS IS A REAL ANSWER AND NOT A FAILURE TO ANSWER: "
                + "a reviewer forced to pick a side on a record that does not support one is how a corpus fills "
                + "with confident nonsense. It is also a finding in its own right - a turn we cannot judge is a "
                + "turn no judgement should have been trusted on either.",
        };

    // ---------------------------------------------------------------- the other closed lists

    /// <summary>How sure the judge is. Ambiguous is accepted and never demotes a red stop to a calm
    /// one - it is a fact about the answer, not a colour.</summary>
    public static readonly IReadOnlyList<string> Confidences = new[] { "high", "ambiguous" };

    /// <summary>What the person answers WITH. "reply" is typed words; "keys" is a selection in a
    /// picker that typed words cannot answer. This decides whether the activation route appends
    /// Enter, so an unrecognised word is rejected rather than guessed.</summary>
    public static readonly IReadOnlyList<string> AnswerVias = new[] { "reply", "keys" };

    /// <summary>Whether a menu takes one choice or several.</summary>
    public static readonly IReadOnlyList<string> SelectionModes = new[] { "single", "multiple" };

    /// <summary>What answering costs. There is no safe default here: an unknown or absent word is
    /// rejected, because defaulting to "none" would be the contract quietly telling the owner that an
    /// irreversible action is free.</summary>
    public static readonly IReadOnlyList<string> Risks =
        new[] { "none", "irreversible", "standing-grant", "spends-money" };

    public const string RiskNone = "none";

    /// <summary>
    /// The two kinds of "finished" (owner ruling, 2026-09-15): "done", the agent says the work is complete, and
    /// "report", the agent only informs the owner and asks nothing. Required on a finished answer and refused on
    /// every other verdict. Not a verdict word: the six words are what the corpus was graded on, so the kind rides
    /// beside the verdict rather than splitting it.
    /// </summary>
    public static readonly IReadOnlyList<string> FinishedKinds = new[] { "done", "report" };

    /// <summary>True when the word is one of the six the Wingman may answer with.</summary>
    public static bool IsWingmanVerdict(string? word)
        => word is not null && WingmanVerdicts.Contains(word, StringComparer.Ordinal);

    /// <summary>True when this verdict drops the stop to a calm colour.</summary>
    public static bool IsCalm(string? word)
        => word is not null && Calm.Contains(word, StringComparer.Ordinal);
}
