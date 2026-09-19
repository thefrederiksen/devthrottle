using CcDirector.Reclaim.Reporting;

namespace CcDirector.Reclaim.Rules;

/// <summary>One thing a rule proved disposable.</summary>
/// <param name="Path">The full path of the item.</param>
/// <param name="Bytes">
/// The bytes it occupies on this disk, as the rule measured them. Nought is a measurement, not a
/// missing one: a folder can genuinely hold nothing.
/// </param>
/// <param name="LastWrittenUtc">When it was last written, which is what the rule's age gate judges.</param>
/// <param name="Why">
/// Why this particular item passed the rule, in one finished sentence. The rule says why it is safe
/// in general; this says why this one qualified.
/// </param>
public sealed record ReclaimCandidate(string Path, long Bytes, DateTimeOffset LastWrittenUtc, string Why);

/// <summary>
/// What one rule found, and everything a reader needs to decide whether to act on it.
///
/// A recommendation that says only "17 gigabytes can be freed" is not one. Every field below is part
/// of the answer: which rule, what proof it holds, what would go, why that is safe, what is lost, how
/// to get it back, and the controls that say whether the rule could do its work at all. The engine
/// writes the finished sentences and nothing that renders them works any of it out again - critical
/// rule 7 in CLAUDE.md.
/// </summary>
public sealed record RuleFinding
{
    /// <summary>The rule's name, as a person reads it.</summary>
    public required string RuleName { get; init; }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public required string RuleId { get; init; }

    /// <summary>Which of the three proofs this rule holds.</summary>
    public required ProofKind Proof { get; init; }

    /// <summary>Whether the rule's answer can be believed.</summary>
    public required RuleVerdict Verdict { get; init; }

    /// <summary>Why the rule is a broken instrument, in one finished sentence, or null when it is not.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>What this rule removes, in one line.</summary>
    public required string WhatItRemoves { get; init; }

    /// <summary>Why removing it is safe, in one line, naming the proof rather than asserting it.</summary>
    public required string WhyItIsSafe { get; init; }

    /// <summary>What is lost by removing it, in one line. Where the answer is not known it says so.</summary>
    public required string WhatIsLost { get; init; }

    /// <summary>How to get it back, in one line. Where there is no way back it says that plainly.</summary>
    public required string HowToGetItBack { get; init; }

    /// <summary>
    /// How old an item must be before this rule will touch it, in days. Nought means the rule has no
    /// age gate, which only an item that cannot be in use may have.
    /// </summary>
    public required int AgeGateDays { get; init; }

    /// <summary>True when acting on this rule needs an administrator.</summary>
    public required bool NeedsAdministrator { get; init; }

    /// <summary>
    /// The exact command the owner runs, for a rule whose proof is that the owner has its own cleanup
    /// command, or that needs an administrator; null when neither applies. This tool never raises
    /// itself and never runs somebody else's command behind the reader's back: it prints the command.
    /// </summary>
    public required string? CommandToRun { get; init; }

    /// <summary>Everything the rule counted while reaching its answer.</summary>
    public required IReadOnlyList<RuleControl> Controls { get; init; }

    /// <summary>
    /// What the rule proved disposable. Empty on a broken rule, because a broken rule's answer means
    /// nothing and must never be read as an empty one.
    /// </summary>
    public required IReadOnlyList<ReclaimCandidate> Candidates { get; init; }

    /// <summary>The bytes those candidates hold, nought when there are none or when the rule is broken.</summary>
    public long CandidateBytes => Verdict == RuleVerdict.Ok ? Candidates.Sum(candidate => candidate.Bytes) : 0L;

    /// <summary>The finished sentences this rule contributes to a report, printed as they stand.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// Build the finished sentences for a rule that did its work, or for one that could not.
    /// </summary>
    /// <param name="finding">The finding, with every field but its lines already set.</param>
    public static IReadOnlyList<string> Describe(RuleFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var lines = new List<string>
        {
            $"rule: {finding.RuleId}",
            $"name: {finding.RuleName}",
            $"proof: {ProofWords(finding.Proof)}"
        };

        if (finding.Verdict == RuleVerdict.Broken)
        {
            lines.Add("verdict: broken");
            lines.Add($"broken-reason: {finding.BrokenReason}");
        }
        else
        {
            lines.Add("verdict: ok");
            lines.Add($"items: {finding.Candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"reclaimable: {SizeText.Exact(finding.CandidateBytes)}");
        }

        lines.Add($"removes: {finding.WhatItRemoves}");
        lines.Add($"safe-because: {finding.WhyItIsSafe}");
        lines.Add($"what-is-lost: {finding.WhatIsLost}");
        lines.Add($"how-to-get-it-back: {finding.HowToGetItBack}");
        lines.Add(finding.AgeGateDays > 0
            ? $"age-gate: {finding.AgeGateDays.ToString(System.Globalization.CultureInfo.InvariantCulture)} days"
            : "age-gate: none");
        lines.Add($"needs-administrator: {(finding.NeedsAdministrator ? "yes" : "no")}");

        if (finding.CommandToRun is not null)
            lines.Add($"command: {finding.CommandToRun}");

        foreach (var control in finding.Controls)
            lines.Add($"control-{control.Line}");

        return lines;
    }

    /// <summary>The proof kind in the words a report prints.</summary>
    /// <param name="proof">The proof kind.</param>
    public static string ProofWords(ProofKind proof) => proof switch
    {
        ProofKind.SystemRecord => "a record the system keeps says nothing needs it",
        ProofKind.OwnersOwnCommand => "the tool that made the data has its own command to clear it",
        ProofKind.WeMadeIt => "we made it, by an exact name, and it is old and closed",
        _ => throw new ArgumentOutOfRangeException(nameof(proof), proof, "There is no such proof kind.")
    };
}
