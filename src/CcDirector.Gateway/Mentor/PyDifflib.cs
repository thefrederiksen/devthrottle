namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The port of <c>difflib.SequenceMatcher(None, a, b, autojunk=False)</c> as far as the metrics use it:
/// <see cref="ChangedWordCount"/> is the reference's <c>word_diff_count</c>, the sum over every opcode that is
/// not <c>equal</c> of the longer side's length. The matching-block search is ported step for step - the
/// same longest-match choice, the same tie-breaks (the earliest start in a, then in b), the same stack
/// order of the remaining ranges - because a different choice among equal-length blocks can change the
/// count and the reference's number is the oracle. With no junk function and autojunk off the junk sets are
/// empty, and the junk-extension loops of the original are no-ops that are left out.
/// </summary>
internal static class PyDifflib
{
    public static int ChangedWordCount(string raw, string cleaned)
    {
        var a = PyText.Split(raw);
        var b = PyText.Split(cleaned);
        var changed = 0;
        foreach (var (tag, i1, i2, j1, j2) in Opcodes(a, b))
            if (tag != "equal") changed += Math.Max(i2 - i1, j2 - j1);
        return changed;
    }

    /// <summary>SequenceMatcher.get_opcodes().</summary>
    public static List<(string Tag, int I1, int I2, int J1, int J2)> Opcodes(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var i = 0;
        var j = 0;
        var answer = new List<(string, int, int, int, int)>();
        foreach (var (ai, bj, size) in MatchingBlocks(a, b))
        {
            var tag = "";
            if (i < ai && j < bj) tag = "replace";
            else if (i < ai) tag = "delete";
            else if (j < bj) tag = "insert";
            if (tag.Length > 0) answer.Add((tag, i, ai, j, bj));
            i = ai + size;
            j = bj + size;
            if (size > 0) answer.Add(("equal", ai, i, bj, j));
        }
        return answer;
    }

    /// <summary>SequenceMatcher.get_matching_blocks(): the non-adjacent matching blocks in order, ending with (len(a), len(b), 0).</summary>
    public static List<(int I, int J, int Size)> MatchingBlocks(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < b.Count; index++)
        {
            if (!b2j.TryGetValue(b[index], out var indices)) b2j[b[index]] = indices = new List<int>();
            indices.Add(index);
        }
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Count, 0, b.Count));
        var blocks = new List<(int I, int J, int Size)>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k > 0)
            {
                blocks.Add((i, j, k));
                if (alo < i && blo < j) queue.Push((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
            }
        }
        blocks.Sort((x, y) =>
        {
            var c = x.I.CompareTo(y.I);
            if (c != 0) return c;
            c = x.J.CompareTo(y.J);
            return c != 0 ? c : x.Size.CompareTo(y.Size);
        });
        var i1 = 0;
        var j1 = 0;
        var k1 = 0;
        var nonAdjacent = new List<(int, int, int)>();
        foreach (var (i2, j2, k2) in blocks)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
            {
                k1 += k2;
            }
            else
            {
                if (k1 > 0) nonAdjacent.Add((i1, j1, k1));
                (i1, j1, k1) = (i2, j2, k2);
            }
        }
        if (k1 > 0) nonAdjacent.Add((i1, j1, k1));
        nonAdjacent.Add((a.Count, b.Count, 0));
        return nonAdjacent;
    }

    /// <summary>SequenceMatcher.find_longest_match with empty junk sets.</summary>
    private static (int I, int J, int Size) FindLongestMatch(IReadOnlyList<string> a, IReadOnlyList<string> b, Dictionary<string, List<int>> b2j,
        int alo, int ahi, int blo, int bhi)
    {
        var besti = alo;
        var bestj = blo;
        var bestsize = 0;
        var j2len = new Dictionary<int, int>();
        var nothing = new List<int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            foreach (var j in b2j.TryGetValue(a[i], out var indices) ? indices : nothing)
            {
                if (j < blo) continue;
                if (j >= bhi) break;
                var k = (j2len.TryGetValue(j - 1, out var previous) ? previous : 0) + 1;
                newj2len[j] = k;
                if (k > bestsize)
                {
                    besti = i - k + 1;
                    bestj = j - k + 1;
                    bestsize = k;
                }
            }
            j2len = newj2len;
        }
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--;
            bestj--;
            bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return (besti, bestj, bestsize);
    }
}
