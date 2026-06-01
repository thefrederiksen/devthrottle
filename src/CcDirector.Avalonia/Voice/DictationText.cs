namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Pure, UI-free helpers for assembling dictation transcript text. Kept separate
/// from <see cref="SpeakDialog"/> so the accumulation behaviour can be unit-tested
/// without spinning up an Avalonia window.
/// </summary>
public static class DictationText
{
    /// <summary>
    /// Joins two transcript fragments with exactly one separating space, unless
    /// either side already supplies the boundary whitespace. This underpins the
    /// "Resume appends new speech to the (edited) transcript" behaviour: the
    /// left side is whatever the user currently has in the review box and the
    /// right side is the freshly cleaned segment, so the user's edits are never
    /// rewritten - only extended.
    /// </summary>
    public static string Join(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right ?? "";
        if (string.IsNullOrEmpty(right)) return left;
        var leftEndsWithSpace = char.IsWhiteSpace(left[^1]);
        var rightStartsWithSpace = char.IsWhiteSpace(right[0]);
        if (leftEndsWithSpace || rightStartsWithSpace) return left + right;
        return left + " " + right;
    }
}
