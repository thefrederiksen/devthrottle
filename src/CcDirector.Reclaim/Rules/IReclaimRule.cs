namespace CcDirector.Reclaim.Rules;

/// <summary>What a rule is asked about.</summary>
public sealed record RuleContext
{
    /// <summary>The folder the caller asked about, in its canonical full form.</summary>
    public required string ScanRootPath { get; init; }

    /// <summary>
    /// The moment the age gates are judged against, in coordinated universal time. It is given rather
    /// than read from the clock so a test can build a tree of known ages and say exactly what the
    /// answer must be, instead of asserting a range and hoping.
    /// </summary>
    public required DateTimeOffset NowUtc { get; init; }
}

/// <summary>
/// What a rule answers with, before the engine folds it into a finding.
///
/// A rule reports what it counted and what it found. It does NOT decide whether its own answer can be
/// believed: <see cref="RuleFold"/> does that, in one place, for every rule. A rule that ruled on
/// itself could forget to, and the forgetting would look exactly like a clean disk.
/// </summary>
/// <param name="Controls">Everything the rule counted while reaching its answer.</param>
/// <param name="Candidates">What it proved disposable.</param>
/// <param name="CannotRunReason">
/// Why the rule could not do its work at all, in one finished sentence, or null when it could. This
/// is for a failure the controls cannot express - a record store that would not open, a platform the
/// rule does not run on.
/// </param>
public sealed record RuleAnswer(
    IReadOnlyList<RuleControl> Controls,
    IReadOnlyList<ReclaimCandidate> Candidates,
    string? CannotRunReason = null);

/// <summary>
/// One rule: a statement that a particular kind of thing on a disk is provably disposable, and the
/// proof it rests on.
///
/// A rule never removes anything, in this phase or any other. It names, sizes and explains; removal
/// is a separate act, made later, against a fresh check. A rule also never invents a proof: it
/// chooses among the three the engine implements and can hold no other.
/// </summary>
public interface IReclaimRule
{
    /// <summary>The rule's name as an identifier a machine matches on, in lower case with hyphens.</summary>
    string Id { get; }

    /// <summary>The rule's name as a person reads it.</summary>
    string Name { get; }

    /// <summary>Which of the three proofs this rule holds.</summary>
    ProofKind Proof { get; }

    /// <summary>What this rule removes, in one line.</summary>
    string WhatItRemoves { get; }

    /// <summary>Why removing it is safe, in one line, naming the proof rather than asserting it.</summary>
    string WhyItIsSafe { get; }

    /// <summary>What is lost by removing it, in one line. Where the answer is not known it says so.</summary>
    string WhatIsLost { get; }

    /// <summary>How to get it back, in one line. Where there is no way back it says that plainly.</summary>
    string HowToGetItBack { get; }

    /// <summary>How old an item must be before this rule will touch it, in days.</summary>
    int AgeGateDays { get; }

    /// <summary>True when acting on this rule needs an administrator.</summary>
    bool NeedsAdministrator { get; }

    /// <summary>The exact command the owner runs, or null when there is none to print.</summary>
    string? CommandToRun { get; }

    /// <summary>
    /// The folder this rule looks in.
    ///
    /// A rule looks at one known place on the machine, not at whatever the caller asked about, so a
    /// caller asking about one folder must not be handed findings from somewhere else entirely: the
    /// bytes would not be inside what was asked about, and every number built by comparing the two
    /// would be meaningless. The caller runs the rules whose folder is inside the folder it asked
    /// about, and says plainly which ones it left out and where they look.
    /// </summary>
    string LooksIn { get; }

    /// <summary>
    /// Look, count, and report. It reads; it changes nothing.
    /// </summary>
    /// <param name="context">What is being asked about, and the moment age gates are judged against.</param>
    RuleAnswer Examine(RuleContext context);
}
