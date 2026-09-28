using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// ONE SPELLING FOR A FACTORY, AND ONE FOR A NOTE (Factory Memory mission, phase 2 review, findings 1 and 2).
///
/// Why this exists rather than each caller trimming as it sees fit. Membership compares factory ids WITHOUT
/// regard to case - a session of <c>website-factory</c> may edit a trigger that says <c>Website-Factory</c> -
/// while the memory is keyed on the exact string. So without one rule, those two spellings are ONE factory for
/// access and TWO memories for storage: each session reads "its factory's memory", gets an answer, and never
/// learns that half the factory's lessons are in the other partition. That is the lost lesson this mission
/// exists to end, arriving silently and from both directions, and no test would have caught it because every
/// test spells the factory one way.
///
/// The rule is the one the factory map already uses - lower-case letters and digits joined by hyphens - because
/// a factory id is the same thing in both places and two rules would drift.
///
/// A NOTE'S NAME follows the same alphabet, and that is not tidiness either. The Director writes ONE FILE PER
/// NOTE into a session's folder before the agent starts, and a failed write is a hard stop: the session does not
/// start at all. A note named <c>aux</c> or <c>a:b</c> cannot become a file on Windows, so one such write by one
/// agent would stop every later session of that factory until a person found it and deleted it. A name that
/// decodes to <c>..\..\something</c> would be a path outside the folder written with the Director's rights. And
/// <c>Domains</c> beside <c>domains</c> is two notes to the database and one file on disk, so an agent would
/// start a run silently missing one of them. Folding case and refusing the rest costs one check here.
/// </summary>
public static partial class FactoryNames
{
    /// <summary>The longest a factory id may be, matching the factory map's own rule.</summary>
    public const int MaxFactoryLength = 64;

    /// <summary>The longest a note's name may be. Shorter than a factory id on purpose: it becomes a file name.</summary>
    public const int MaxNoteNameLength = 64;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();

    /// <summary>
    /// Fold <paramref name="raw"/> to the one spelling and say whether it is allowed. Case is folded rather than
    /// refused, because a person typing <c>Website-Factory</c> in the Cockpit means the factory they can see, and
    /// refusing them over a capital letter would be pedantry; anything else is refused, because it would become a
    /// second factory or a file name nobody can write.
    /// </summary>
    public static bool TryFactory(string? raw, out string factory, out string? refusal)
        => Try(raw, MaxFactoryLength, "factory", out factory, out refusal);

    /// <summary>
    /// The names Windows keeps for devices. They pass the alphabet rule - they are lower-case letters - and they
    /// cannot be files, which for a note is worse than being refused: the Director writes one file per note before
    /// an agent starts, so a single note called <c>aux</c> would stop every later session of that factory on every
    /// Windows machine until a person found it and deleted it. One agent's write would close the factory.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Fold a note's name to the one spelling and say whether it is allowed.</summary>
    public static bool TryNoteName(string? raw, out string name, out string? refusal)
    {
        if (!Try(raw, MaxNoteNameLength, "note name", out name, out refusal)) return false;
        if (Reserved.Contains(name))
        {
            refusal = $"'{name}' is a name Windows keeps for a device, so it cannot become a file; " +
                      "a note is written as one before an agent starts, so call it something else";
            return false;
        }
        return true;
    }

    private static bool Try(string? raw, int max, string what, out string folded, out string? refusal)
    {
        folded = (raw ?? "").Trim().ToLowerInvariant();
        refusal = null;
        if (folded.Length == 0)
        {
            refusal = $"a {what} is needed";
            return false;
        }
        if (folded.Length > max)
        {
            refusal = $"a {what} takes at most {max} characters, and '{folded}' is {folded.Length}";
            return false;
        }
        if (!Pattern().IsMatch(folded))
        {
            refusal = $"a {what} is lower-case letters and digits joined by single hyphens, so '{raw}' cannot be one";
            return false;
        }
        return true;
    }
}
