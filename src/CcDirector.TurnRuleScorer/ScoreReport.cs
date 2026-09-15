namespace CcDirector.TurnRuleScorer;

/// <summary>
/// Why a pinned pair could not be scored. Every one of these is a CORPUS MISS and is reported
/// rather than scored: a screen that no longer matches the hash the manifest pinned is not
/// evidence, and quietly scoring it would turn a moving corpus back into the thing the pin exists
/// to prevent.
/// </summary>
internal enum CorpusMissKind
{
    /// <summary>The manifest names a screen file that is not under the screen root.</summary>
    ScreenFileMissing,

    /// <summary>The file is there and its bytes hash to something other than the pinned value.</summary>
    ScreenHashMismatch,

    /// <summary>The manifest pins no hash for a screen it names, so nothing can be verified.</summary>
    NoPinnedHash,

    /// <summary>The file is there, hashes correctly, and will not parse as a screen.</summary>
    ScreenUnreadable,
}

internal sealed record CorpusMiss(CorpusMissKind Kind, string Session, string RelativePath, string Detail);

/// <summary>One candidate's answer over one class of pairs, for one agent.</summary>
/// <param name="Candidate">The rule's own <c>Name</c>, taken from the rule object rather than typed here.</param>
/// <param name="Label">The manifest's behaviour class - never a cause.</param>
/// <param name="Agent">The agent string as the manifest recorded it.</param>
/// <param name="Pairs">How many scorable pairs fell in this class for this agent.</param>
/// <param name="Opened">How many of them the candidate said had gained content.</param>
internal sealed record Tally(string Candidate, string Label, string Agent, int Pairs, int Opened);

/// <summary>
/// How the body split - the guess this whole measurement rests on - turned out across the screens
/// actually read. Counted over SCREENS, not pairs, because the guess is made once per screen.
/// </summary>
/// <param name="Screens">Total screens read and split.</param>
/// <param name="NoAnchor">Screens with no prompt-like row: the whole screen became the body.</param>
/// <param name="SingleAnchor">Screens with exactly one: the only unambiguous case.</param>
/// <param name="SeveralAnchors">Screens with more than one: the last was taken, the rest ignored.</param>
internal sealed record BodySplitCensus(int Screens, int NoAnchor, int SingleAnchor, int SeveralAnchors)
{
    /// <summary>Every screen where the guess had to choose or had nothing to go on.</summary>
    internal int Ambiguous => NoAnchor + SeveralAnchors;
}

/// <summary>
/// How many pairs carried a screen with NO ROWS AT ALL, for one agent and one class.
///
/// THIS IS THE ABSENCE OF EVIDENCE, NOT A VERDICT, and it has to be printed beside the scores
/// because it does not look like absence in a table. A pair whose screens are both empty scores as
/// "gained nothing" for every candidate and as "opened" for the old byte rule, which reads exactly
/// like a rule suppressing a real reply. In this corpus it is not a rounding detail: it is the
/// WHOLE of two agents' populations.
/// </summary>
internal sealed record EmptyScreenPairs(string Agent, string Label, int Pairs);

/// <summary>Everything one run of the scorer established. Printing is a separate concern.</summary>
internal sealed record ScoreReport(
    string ManifestPath,
    string ScreenRoot,
    int Wakes,
    int PairsInManifest,
    int PairsScored,
    IReadOnlyList<CorpusMiss> Misses,
    BodySplitCensus BodySplit,
    int EmptyScreens,
    IReadOnlyList<EmptyScreenPairs> PairsTouchingAnEmptyScreen,
    IReadOnlyList<string> UnknownDrivers,
    IReadOnlyList<Tally> Tallies);
