using System.Globalization;

namespace CcDirector.Reclaim.Rules;

/// <summary>
/// One control on a rule's answer: something the rule counted while reaching it, and whether the
/// answer means anything when that count is nought.
/// </summary>
/// <param name="Name">
/// What was counted, in plain words with hyphens between them: "records-read",
/// "records-found-on-disk", "candidates-examined".
/// </param>
/// <param name="Count">How many.</param>
/// <param name="MustNotBeEmpty">
/// True when a count of nought means the rule could not do its work rather than that there is
/// nothing to remove. The two look identical from the outside, and only one of them is safe to act
/// on, so the rule says which it is instead of leaving the reader to guess.
/// </param>
public sealed record RuleControl(string Name, long Count, bool MustNotBeEmpty)
{
    /// <summary>The control as a report line writes it.</summary>
    public string Line => $"{Name}: {Count.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Whether a rule's answer can be believed.</summary>
public enum RuleVerdict
{
    /// <summary>The rule did its work and its answer can be acted on.</summary>
    Ok,

    /// <summary>
    /// The rule is a broken instrument and its answer means nothing. A rule that compares two lists
    /// and finds one of them empty lands here, and it says BROKEN rather than "nothing to remove",
    /// because a comparison against an empty list finds nothing every time and looks exactly like a
    /// clean disk.
    /// </summary>
    Broken
}
