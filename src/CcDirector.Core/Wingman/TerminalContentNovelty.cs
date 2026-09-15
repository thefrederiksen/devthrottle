using System.Text;

namespace CcDirector.Core.Wingman;

/// <summary>
/// One question, asked of two screens: did the conversation GAIN something, or was the same
/// screen drawn again? This is the whole of the phase-one turn-detection rule, kept pure - no
/// <c>Session</c>, no terminal buffer, no clock - so it is decided by unit tests exactly as
/// <see cref="TerminalStateDetector.TryExtractBody"/> already is.
///
/// TWO CANDIDATE RULES LIVE HERE, and neither is assumed to win. The row rule answers with the
/// row that appeared; the size rule answers with a magnitude. On the labelled corpus behind this
/// work the row rule held 94.0 percent of repaints and an independent reviewer's size threshold
/// held 97.0 percent, both opening 229 of 231 long unexplained wakes - so the corpus does not
/// settle it. Both are reachable through <see cref="ITerminalNoveltyRule"/> and the choice is
/// made on live bytes, not here.
///
/// NOTHING HERE IS PROVEN ON PRODUCTION SCREENS. The measurement behind these rules ran on saved
/// screen pairs that do not record the cursor, so it had to guess where the input box started.
/// Production has the real cursor. A rule that scores well on that corpus and badly in shadow has
/// fitted the corpus.
/// </summary>
internal static class TerminalContentNovelty
{
    /// <summary>
    /// A row needs this many letters or digits to be capable of counting as new at all. Below it
    /// the row is box drawing, a spinner glyph, or punctuation - never the conversation gaining
    /// content. Pinned to the scoring harness (minsub = 3).
    /// </summary>
    internal const int MinimumSubstance = 3;

    /// <summary>
    /// How similar a candidate row's key must be to a settled row's key before the candidate is
    /// treated as the same row redrawn rather than a new one. Pinned to the scoring harness
    /// (fuzzy = 0.80). This filter is what makes a TORN REPAINT invisible - a row that came back
    /// with a word wrapped differently or a character dropped - and it was the single largest
    /// improvement in the measurement, worth about eight points. The measurement also found 0.60
    /// through 0.80 flat on that one corpus, so this number is a defensible choice inside a flat
    /// range, not a measured optimum.
    /// </summary>
    internal const double NearDuplicateSimilarity = 0.80;

    /// <summary>
    /// A starting value for the size rule's threshold, in characters. UNVALIDATED: the reviewer who
    /// scored a size threshold at 97.0 percent did not deposit the script behind it, so the number
    /// that produced that score is not recoverable and this is not it. Work item five picks the
    /// threshold from live bytes. Do not quote this constant as a measured value.
    /// </summary>
    internal const int StartingChangedCharacterThreshold = 200;

    /// <summary>The row rule: answers with the first row that appeared.</summary>
    internal static ITerminalNoveltyRule RowRule { get; } = new RowNoveltyRule();

    /// <summary>
    /// The size rule at a given threshold: answers with a magnitude. Cheaper than the row rule and
    /// it has no marker list to keep current, but it can say a screen moved without saying what
    /// appeared - worth less in a log and worth less to the Wingman.
    /// </summary>
    internal static ITerminalNoveltyRule SizeRule(int changedCharacterThreshold) =>
        new ChangedSizeRule(changedCharacterThreshold);

    // ------------------------------------------------------------------------------------------
    // The row rule
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Did <paramref name="currentBody"/> gain a row that <paramref name="settledBody"/> did not
    /// have? A row counts as new only when ALL FOUR of these hold, tested in this order because
    /// each is cheaper than the next:
    ///
    ///   1. it carries at least <see cref="MinimumSubstance"/> letters or digits;
    ///   2. it contains none of <paramref name="chromeMarkers"/> - the agent's interface talking
    ///      about itself (update notice, shortcut bar, context hint, interrupt hint) rather than
    ///      about the work;
    ///   3. its KEY is absent from the settled screen, where the key is the row reduced to
    ///      lower-case LETTERS only, with every non-alphanumeric character and then every digit
    ///      removed - which is what makes a ticking clock invisible;
    ///   4. its key is not a near-duplicate of any settled row's key at
    ///      <see cref="NearDuplicateSimilarity"/>, compared only against settled keys of comparable
    ///      length - which is what makes a torn repaint invisible.
    ///
    /// Blank rows take part in nothing: they are neither candidates nor settled keys.
    /// </summary>
    /// <param name="firstNewRow">
    /// The first row that satisfied all four conditions, verbatim, so a wrong decision can be read
    /// back out of the log. Null when nothing was gained.
    /// </param>
    internal static bool GainedContent(
        IReadOnlyList<string> settledBody,
        IReadOnlyList<string> currentBody,
        IReadOnlyCollection<string> chromeMarkers,
        out string? firstNewRow)
    {
        firstNewRow = null;
        if (currentBody is null || currentBody.Count == 0)
            return false;

        var settledKeys = KeysOf(settledBody);
        var settledKeySet = new HashSet<string>(settledKeys, StringComparer.Ordinal);

        foreach (var row in currentBody)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            if (Substance(row) < MinimumSubstance) continue;
            if (CarriesChrome(row, chromeMarkers)) continue;

            var key = Key(row);
            if (settledKeySet.Contains(key)) continue;
            if (IsNearDuplicateOfSettled(key, settledKeys)) continue;

            firstNewRow = row;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Condition four on its own. The candidate key is compared ONLY against settled keys whose
    /// length is within max(8, length / 2) of it, inclusive at both ends: a key far shorter or far
    /// longer cannot reach the similarity threshold, and skipping it keeps the cost down on a full
    /// screen. Orientation matters and is pinned to the harness: the settled key is sequence A and
    /// the candidate key is sequence B, because <see cref="SequenceRatio"/> is not symmetric.
    /// </summary>
    internal static bool IsNearDuplicateOfSettled(string key, IReadOnlyList<string> settledKeys)
    {
        int band = Math.Max(8, key.Length / 2);
        int lo = key.Length - band, hi = key.Length + band;

        var matcher = new PythonSequenceMatcher(key);
        foreach (var settled in settledKeys)
        {
            if (settled.Length < lo || settled.Length > hi) continue;
            if (matcher.RatioAtLeast(settled, NearDuplicateSimilarity)) return true;
        }
        return false;
    }

    /// <summary>
    /// The comparison key for a row: every non-alphanumeric character removed, then lower-cased,
    /// then every digit removed - so what is left is lower-case letters only. A clock ticking from
    /// 12:04 to 12:05, a token counter, a percentage, a spinner glyph and a box-drawing border all
    /// reduce to the same key as the row they replaced, which is exactly the point.
    /// </summary>
    internal static string Key(string row)
    {
        if (string.IsNullOrEmpty(row)) return "";
        var sb = new StringBuilder(row.Length);
        foreach (char c in row)
        {
            // ASCII only, deliberately: the harness this is pinned to strips on [^0-9A-Za-z], so a
            // non-ASCII letter is removed there and must be removed here too.
            if (c >= 'a' && c <= 'z') sb.Append(c);
            else if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
            // digits, and everything that is not an ASCII letter: dropped
        }
        return sb.ToString();
    }

    /// <summary>
    /// A row's "substance": how many ASCII letters and digits it carries. Box drawing, spinner
    /// glyphs and punctuation score zero, which is what condition one is for.
    /// </summary>
    internal static int Substance(string row)
    {
        if (string.IsNullOrEmpty(row)) return 0;
        int n = 0;
        foreach (char c in row)
        {
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) n++;
        }
        return n;
    }

    /// <summary>
    /// Does the row carry one of the agent's own self-describing markers? Case-insensitive
    /// substring match, because an agent draws the same notice in whatever case it feels like.
    /// </summary>
    internal static bool CarriesChrome(string row, IReadOnlyCollection<string>? chromeMarkers)
    {
        if (chromeMarkers is null || chromeMarkers.Count == 0) return false;
        foreach (var marker in chromeMarkers)
        {
            if (string.IsNullOrEmpty(marker)) continue;
            if (row.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static List<string> KeysOf(IReadOnlyList<string>? body)
    {
        var keys = new List<string>(body?.Count ?? 0);
        if (body is null) return keys;
        foreach (var row in body)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            keys.Add(Key(row));
        }
        return keys;
    }

    // ------------------------------------------------------------------------------------------
    // The size rule
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The second candidate: how much text the screen gained, measured after aligning the two
    /// bodies ROW BY ROW, in characters.
    ///
    /// STATED, NOT REPRODUCED. The reviewer who scored a size threshold at 97.0 percent of repaints
    /// held did not deposit the script behind it, so "how much text changed" was never written down
    /// anywhere this could be copied from. This is our definition of it and it is deliberately the
    /// plainest one: align the two bodies as sequences of rows, then add up the characters of the
    /// current body's rows that no matching block covers. Alignment on ROWS rather than characters
    /// is chosen because a whole-screen character alignment costs the product of two screen sizes on
    /// every check, and because Python's popularity heuristic would purge almost every letter from a
    /// sequence that long and make the answer meaningless.
    /// </summary>
    internal static int ChangedCharacters(
        IReadOnlyList<string>? settledBody,
        IReadOnlyList<string>? currentBody)
    {
        var before = NonBlank(settledBody);
        var after = NonBlank(currentBody);

        int total = 0;
        foreach (var row in after) total += row.Length;
        if (before.Count == 0 || after.Count == 0) return total;

        // Row alignment, so the popularity heuristic is off: the elements are whole rows, and a
        // row that repeats is signal here rather than noise.
        int matchedChars = 0;
        foreach (var block in PythonSequenceMatcher.MatchingBlocks(before, after, autoJunk: false))
        {
            for (int i = 0; i < block.Length; i++) matchedChars += after[block.BStart + i].Length;
        }
        return total - matchedChars;
    }

    private static List<string> NonBlank(IReadOnlyList<string>? body)
    {
        var rows = new List<string>(body?.Count ?? 0);
        if (body is null) return rows;
        foreach (var row in body)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            rows.Add(row);
        }
        return rows;
    }

    // ------------------------------------------------------------------------------------------
    // The similarity function
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The similarity of two strings on Python's difflib SequenceMatcher ratio: find the longest
    /// matching block, recurse into what is left of it and what is right of it, and report
    /// 2 * matched / (length of A + length of B).
    ///
    /// IT MUST BE THIS ALGORITHM AND NOT A LONGEST-COMMON-SUBSEQUENCE RATIO. The two look
    /// interchangeable and are not: measured on twenty thousand short pairs they disagree on 28
    /// percent of them, worst case 0.15 against 0.67 - far wider than the 0.80 threshold this
    /// feeds. A longest-common-subsequence implementation would look right, pass a hand-written
    /// test, and quietly fail to reproduce the published corpus numbers, which would make the whole
    /// comparison in work item five meaningless. <c>SequenceRatioPinnedToPythonTests</c> pins exact
    /// values taken from Python so a drift is caught.
    ///
    /// Ratio is NOT symmetric: the longest matching block is found through B's index, so swapping
    /// the arguments can change the answer. Callers keep the harness's orientation - settled row as
    /// A, candidate row as B.
    /// </summary>
    internal static double SequenceRatio(string a, string b) =>
        new PythonSequenceMatcher(b).RatioAgainst(a);
}

/// <summary>
/// One rule that answers "did this screen gain content?". Two implementations exist and the choice
/// between them is made on live bytes, so everything that consumes a verdict consumes it through
/// here rather than naming a rule.
/// </summary>
internal interface ITerminalNoveltyRule
{
    /// <summary>Short, stable, ASCII - it is written into the log beside every verdict.</summary>
    string Name { get; }

    /// <param name="evidence">
    /// What the rule saw, for the log: the row that appeared (row rule) or the magnitude that
    /// crossed the threshold (size rule). Null when nothing was gained.
    /// </param>
    bool GainedContent(
        IReadOnlyList<string> settledBody,
        IReadOnlyList<string> currentBody,
        IReadOnlyCollection<string> chromeMarkers,
        out string? evidence);
}

/// <summary>The row rule behind <see cref="ITerminalNoveltyRule"/>.</summary>
internal sealed class RowNoveltyRule : ITerminalNoveltyRule
{
    public string Name => "row";

    public bool GainedContent(
        IReadOnlyList<string> settledBody,
        IReadOnlyList<string> currentBody,
        IReadOnlyCollection<string> chromeMarkers,
        out string? evidence)
        => TerminalContentNovelty.GainedContent(settledBody, currentBody, chromeMarkers, out evidence);
}

/// <summary>
/// The size rule behind <see cref="ITerminalNoveltyRule"/>. It ignores the chrome markers entirely -
/// that is its selling point (no list to keep current) and its weakness (an agent's own footer
/// counts as gained text once it is long enough).
/// </summary>
internal sealed class ChangedSizeRule : ITerminalNoveltyRule
{
    private readonly int _threshold;

    internal ChangedSizeRule(int changedCharacterThreshold)
    {
        if (changedCharacterThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(changedCharacterThreshold));
        _threshold = changedCharacterThreshold;
    }

    public string Name => $"size>={_threshold}";

    public bool GainedContent(
        IReadOnlyList<string> settledBody,
        IReadOnlyList<string> currentBody,
        IReadOnlyCollection<string> chromeMarkers,
        out string? evidence)
    {
        _ = chromeMarkers;
        int changed = TerminalContentNovelty.ChangedCharacters(settledBody, currentBody);
        if (changed < _threshold)
        {
            evidence = null;
            return false;
        }
        evidence = $"changed {changed} characters";
        return true;
    }
}
