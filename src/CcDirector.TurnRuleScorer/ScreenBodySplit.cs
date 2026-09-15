using System.Text.RegularExpressions;

namespace CcDirector.TurnRuleScorer;

/// <summary>
/// THE GUESS, AND IT CONTROLS THE RESULT. Say this before any number this tool prints is read.
///
/// The shipped detector splits a screen at the REAL CURSOR: everything strictly above the cursor
/// row is the conversation body, everything at and below it is the composer and the footer
/// (<c>TerminalStateDetector.TryExtractBodyRows</c>). A saved turn-review screen records no cursor,
/// so this scorer has to find the input box by looking at the text - it takes the LAST row that
/// begins with a prompt glyph and treats everything above that row as the body.
///
/// That is the same guess the Python harness behind the published measurement made, and it is
/// reproduced here character for character rather than improved on, because the whole value of this
/// tool is that it scores the SAME evidence the same way. Improving the guess would make the
/// numbers incomparable without making them true.
///
/// It is load-bearing and it is often wrong. A screen with no prompt-like row at all gets its whole
/// height treated as body, footer included; a screen with several gets the last one, which can cut
/// away a real reply that happened to start with a chevron. Of the 8,650 screens in the earlier
/// measurement only 1,674 had exactly one prompt-like row. This tool counts the same three cases
/// and prints them, so the size of the guess is visible beside the scores rather than buried.
/// </summary>
internal static class ScreenBodySplit
{
    /// <summary>
    /// A row that looks like the agent's input box: optional leading whitespace, then a greater-than
    /// sign, a heavy right-pointing angle quotation mark (U+276F), or a black right-pointing
    /// triangle (U+25B6). Pinned to the harness expression <c>^\s*[&gt;\u276F\u25B6]</c>.
    /// </summary>
    private static readonly Regex PromptLike = new(@"^\s*[>\u276F\u25B6]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>How many rows of this screen look like the input box.</summary>
    internal static int PromptLikeRowCount(IReadOnlyList<string> rows)
    {
        int found = 0;
        foreach (var row in rows)
        {
            if (PromptLike.IsMatch(row)) found++;
        }
        return found;
    }

    /// <summary>
    /// The body, on the harness's rule: the rows strictly above the LAST prompt-like row, or the
    /// whole screen when there is no prompt-like row at all.
    /// </summary>
    internal static IReadOnlyList<string> Body(IReadOnlyList<string> rows)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (PromptLike.IsMatch(rows[i]))
            {
                var body = new string[i];
                for (int j = 0; j < i; j++) body[j] = rows[j];
                return body;
            }
        }
        return rows;
    }

    /// <summary>
    /// How confident the split is on one screen. There is no fourth case: a screen either offered
    /// no anchor, exactly one, or several.
    /// </summary>
    internal enum Confidence
    {
        /// <summary>No prompt-like row. The whole screen is treated as body, footer and all.</summary>
        NoAnchor,

        /// <summary>Exactly one prompt-like row. The only case where the guess is not guessing.</summary>
        SingleAnchor,

        /// <summary>Several prompt-like rows. The last one is taken and the others are ignored.</summary>
        SeveralAnchors,
    }

    internal static Confidence ConfidenceOf(IReadOnlyList<string> rows) =>
        PromptLikeRowCount(rows) switch
        {
            0 => Confidence.NoAnchor,
            1 => Confidence.SingleAnchor,
            _ => Confidence.SeveralAnchors,
        };
}
