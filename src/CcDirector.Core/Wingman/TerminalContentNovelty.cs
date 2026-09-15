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
    /// length is within max(8, length / 2) of it, inclusive at both ends, which is the band the
    /// harness uses.
    ///
    /// AT 0.80 THE BAND IS SPEED AND NOTHING ELSE, and that is worth knowing before anyone moves
    /// the threshold. The ratio can never exceed 2 * min(lengths) / (sum of lengths), so a settled
    /// key longer than length + max(8, length / 2) tops out strictly below 0.80, and one shorter
    /// than length - max(8, length / 2) tops out below 0.67. Every row the band skips was already
    /// unreachable, so skipping it changes no verdict - <c>The_length_band_only_skips_rows_that_
    /// could_never_reach_the_threshold</c> holds that arithmetic. LOWER the threshold much past
    /// 0.80 and that stops being true: the band would start deciding things, which is not what it
    /// is for.
    ///
    /// Orientation matters and is pinned to the harness: the settled key is sequence A and the
    /// candidate key is sequence B, because <see cref="SequenceRatio"/> is not symmetric.
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
/// Which content candidate decides whether a settled session opens a turn, or
/// <see cref="Off"/> for today's rule, where any byte opens it. Off is the shipped default:
/// turning the rule on is the owner's decision and he wants the shadow numbers first.
/// </summary>
internal enum TurnContentRule
{
    /// <summary>Any byte at a settled session opens a turn. What the product does today.</summary>
    Off,

    /// <summary>The row rule. Answers with the row that appeared, which is worth having in a log.</summary>
    Row,

    /// <summary>The size rule. Cheaper, no marker list, but it cannot say what appeared.</summary>
    Size,
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

/// <summary>
/// A faithful port of Python's difflib SequenceMatcher, restricted to what the scoring harness
/// behind this work actually uses: no junk predicate, the default popularity heuristic, and the
/// ratio.
///
/// WHY A PORT AND NOT SOMETHING EQUIVALENT. The corpus numbers this rule is judged against were
/// produced by Python's algorithm, and a longest-common-subsequence ratio is NOT that algorithm
/// even though both return "how similar, from zero to one". Measured on twenty thousand short
/// pairs the two disagree on 28 percent, worst case 0.15 against 0.67. A near-miss at a 0.80
/// threshold is a different verdict, so an equivalent-looking implementation would score
/// differently from the published numbers while every hand-written test still passed.
///
/// THE ALGORITHM. Find the longest block of B that matches a block of A, seeded through an index
/// of where each element of B occurs; then recurse into the region left of that block and the
/// region right of it. The ratio is 2 * (total matched elements) / (length of A + length of B).
/// Ties go to the EARLIEST block in both sequences, which is what makes the answer stable.
///
/// TWO PYTHON BEHAVIOURS THAT LOOK LIKE DETAILS AND ARE NOT:
///
///   1. It is not symmetric. The index is built over B, so the ratio of A against B and the ratio
///      of B against A can differ. The harness sets sequence two first and sequence one second,
///      which makes the SETTLED row A and the CANDIDATE row B, and this port keeps that
///      orientation.
///   2. The popularity heuristic ("autojunk") only engages once B has 200 OR MORE elements. Below
///      that it does nothing at all, so for terminal-row keys - the only thing the row rule
///      compares - it is almost always inert. At or above 200, any element of B occurring strictly
///      more than (length of B) / 100 + 1 times is dropped from the index, so it can no longer SEED
///      a match, although a match that reaches it can still extend over it. For an eighty-column
///      terminal a key never reaches 200 characters; on a very wide terminal it can, and there this
///      port does exactly what Python does rather than quietly diverging from the numbers. It is
///      turned off in exactly one place, and this file says so there: the size rule aligns whole
///      ROWS, where a row that repeats is signal rather than noise.
///
/// Python's junk-extension passes are not reproduced because they cannot fire: no junk predicate
/// is supplied, so the junk set is empty. Popular elements are NOT junk - Python keeps that
/// distinction and so does this.
/// </summary>
internal sealed class PythonSequenceMatcher
{
    /// <summary>Python applies the popularity heuristic only from this length of B upwards.</summary>
    internal const int AutoJunkMinimumLength = 200;

    private readonly CharSequence _b;
    private readonly Dictionary<char, List<int>> _b2j;
    private Dictionary<char, int>? _fullBCount;

    /// <param name="b">
    /// The SECOND sequence - the candidate row's key. The index is built over it, so it is the one
    /// that is reused across many comparisons.
    /// </param>
    internal PythonSequenceMatcher(string b, bool autoJunk = true)
    {
        _b = new CharSequence(b ?? "");
        _b2j = BuildIndex(_b, autoJunk, EqualityComparer<char>.Default);
    }

    /// <summary>
    /// The exact ratio of <paramref name="a"/> against the sequence this matcher was built on.
    /// </summary>
    internal double RatioAgainst(string a)
    {
        var seqA = new CharSequence(a ?? "");
        int total = seqA.Count + _b.Count;
        if (total == 0) return 1.0;
        return 2.0 * MatchedCount(seqA, _b, _b2j, EqualityComparer<char>.Default) / total;
    }

    /// <summary>
    /// Is the ratio at least <paramref name="threshold"/>? Identical in outcome to comparing
    /// <see cref="RatioAgainst"/> against the threshold - the two shortcuts below are Python's own
    /// upper bounds on the ratio, so a sequence they reject could never have reached it - and it is
    /// the shape the harness uses.
    /// </summary>
    internal bool RatioAtLeast(string a, double threshold)
    {
        var seqA = new CharSequence(a ?? "");
        int total = seqA.Count + _b.Count;
        if (total == 0) return 1.0 >= threshold;

        // The bound from the lengths alone.
        if (2.0 * Math.Min(seqA.Count, _b.Count) / total < threshold) return false;

        // The bound from the multiset intersection, ignoring order.
        if (2.0 * QuickMatchCount(seqA) / total < threshold) return false;

        return 2.0 * MatchedCount(seqA, _b, _b2j, EqualityComparer<char>.Default) / total >= threshold;
    }

    /// <summary>One matching block: where it starts in each sequence, and how long it is.</summary>
    internal readonly record struct Block(int AStart, int BStart, int Length);

    /// <summary>
    /// The matching blocks between two sequences of rows. Used by the size rule, which aligns whole
    /// rows rather than characters.
    /// </summary>
    internal static List<Block> MatchingBlocks(
        IReadOnlyList<string> a, IReadOnlyList<string> b, bool autoJunk)
    {
        var index = BuildIndex(b, autoJunk, StringComparer.Ordinal);
        var blocks = new List<Block>();
        CollectBlocks(a, b, index, StringComparer.Ordinal, blocks);
        return blocks;
    }

    // ------------------------------------------------------------------------------------------

    private static Dictionary<T, List<int>> BuildIndex<T>(
        IReadOnlyList<T> b, bool autoJunk, IEqualityComparer<T> comparer) where T : notnull
    {
        var index = new Dictionary<T, List<int>>(comparer);
        for (int j = 0; j < b.Count; j++)
        {
            if (!index.TryGetValue(b[j], out var at))
            {
                at = new List<int>();
                index[b[j]] = at;
            }
            at.Add(j);
        }

        // Python purges POPULAR elements from the index once the sequence is long enough. They are
        // not junk: a match can still extend over them, it just cannot start on them.
        if (autoJunk && b.Count >= AutoJunkMinimumLength)
        {
            int ntest = b.Count / 100 + 1;
            List<T>? popular = null;
            foreach (var pair in index)
            {
                if (pair.Value.Count > ntest) (popular ??= new List<T>()).Add(pair.Key);
            }
            if (popular is not null)
            {
                foreach (var elt in popular) index.Remove(elt);
            }
        }

        return index;
    }

    private static int MatchedCount<T>(
        IReadOnlyList<T> a, IReadOnlyList<T> b, Dictionary<T, List<int>> index,
        IEqualityComparer<T> comparer) where T : notnull
    {
        var blocks = new List<Block>();
        CollectBlocks(a, b, index, comparer, blocks);
        int matched = 0;
        foreach (var block in blocks) matched += block.Length;
        return matched;
    }

    /// <summary>
    /// Python's get_matching_blocks, minus the adjacent-block merge and the terminating sentinel,
    /// neither of which changes the total matched count that the ratio is built from.
    /// </summary>
    private static void CollectBlocks<T>(
        IReadOnlyList<T> a, IReadOnlyList<T> b, Dictionary<T, List<int>> index,
        IEqualityComparer<T> comparer, List<Block> into) where T : notnull
    {
        var queue = new Stack<(int ALo, int AHi, int BLo, int BHi)>();
        queue.Push((0, a.Count, 0, b.Count));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var match = FindLongestMatch(a, b, index, comparer, alo, ahi, blo, bhi);
            if (match.Length == 0) continue;

            into.Add(match);
            if (alo < match.AStart && blo < match.BStart)
                queue.Push((alo, match.AStart, blo, match.BStart));
            if (match.AStart + match.Length < ahi && match.BStart + match.Length < bhi)
                queue.Push((match.AStart + match.Length, ahi, match.BStart + match.Length, bhi));
        }
    }

    /// <summary>
    /// Python's find_longest_match. The rolling map from "index in B" to "length of the run ending
    /// there" is what makes this linear in the number of index hits rather than quadratic, and the
    /// strict greater-than on the best size is what makes ties go to the earliest block.
    /// </summary>
    private static Block FindLongestMatch<T>(
        IReadOnlyList<T> a, IReadOnlyList<T> b, Dictionary<T, List<int>> index,
        IEqualityComparer<T> comparer, int alo, int ahi, int blo, int bhi) where T : notnull
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var runEndingAt = new Dictionary<int, int>();

        for (int i = alo; i < ahi; i++)
        {
            var next = new Dictionary<int, int>();
            if (index.TryGetValue(a[i], out var positions))
            {
                foreach (int j in positions)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    int k = (runEndingAt.TryGetValue(j - 1, out var prior) ? prior : 0) + 1;
                    next[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            runEndingAt = next;
        }

        // Extend the block over elements the index does not carry - the popular ones the heuristic
        // dropped. Python then runs the same two loops again over JUNK elements; with no junk
        // predicate supplied the junk set is empty, so those passes cannot fire and are not here.
        while (besti > alo && bestj > blo && comparer.Equals(a[besti - 1], b[bestj - 1]))
        {
            besti--; bestj--; bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi
               && comparer.Equals(a[besti + bestsize], b[bestj + bestsize]))
        {
            bestsize++;
        }

        return new Block(besti, bestj, bestsize);
    }

    /// <summary>The numerator of Python's quick_ratio: the multiset intersection, order ignored.</summary>
    private int QuickMatchCount(CharSequence a)
    {
        if (_fullBCount is null)
        {
            var counts = new Dictionary<char, int>();
            for (int j = 0; j < _b.Count; j++)
            {
                counts.TryGetValue(_b[j], out var n);
                counts[_b[j]] = n + 1;
            }
            _fullBCount = counts;
        }

        var available = new Dictionary<char, int>();
        int matches = 0;
        for (int i = 0; i < a.Count; i++)
        {
            char c = a[i];
            int remaining;
            if (available.TryGetValue(c, out var left)) remaining = left;
            else remaining = _fullBCount.TryGetValue(c, out var total) ? total : 0;
            available[c] = remaining - 1;
            if (remaining > 0) matches++;
        }
        return matches;
    }

    /// <summary>
    /// A string as a read-only list of characters. System.String does not implement
    /// IReadOnlyList of char, and this avoids copying the string in order to compare it.
    /// </summary>
    private sealed class CharSequence : IReadOnlyList<char>
    {
        private readonly string _s;
        internal CharSequence(string s) => _s = s;
        public char this[int index] => _s[index];
        public int Count => _s.Length;
        public IEnumerator<char> GetEnumerator() => _s.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _s.GetEnumerator();
    }
}
