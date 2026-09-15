using System.IO;
using System.Text.Json;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The near-duplicate filter in <see cref="TerminalContentNovelty"/> is a threshold on Python's
/// difflib SequenceMatcher ratio, and the corpus numbers the whole phase is judged against were
/// produced by that exact algorithm. A similarity function that merely LOOKS equivalent is not
/// good enough: a longest-common-subsequence ratio returns a different number for most pairs, so
/// substituting one would silently stop reproducing the published scores while every hand-written
/// test still passed.
///
/// So this file does two things. It pins a handful of ratios as literals taken from Python, which
/// is what a reader can check by eye. And it replays a generated fixture of 325 pairs, which is
/// what actually catches a drift: a longest-common-subsequence implementation disagrees with
/// difflib on over half of randomly generated pairs, so it cannot survive the fixture even once.
/// </summary>
public sealed class SequenceRatioPinnedToPythonTests
{
    private const double Exact = 1e-12;

    /// <summary>
    /// Values read straight out of CPython 3.11.6:
    ///   SequenceMatcher(); set_seq2(b); set_seq1(a); ratio()
    /// which is the orientation the scoring harness uses - settled row as A, candidate row as B.
    /// </summary>
    [Theory]
    [InlineData("abcd", "abce", 0.75)]
    [InlineData("hello world", "hello there", 0.6363636363636364)]
    [InlineData("thequickbrownfox", "thequickbrownfoxx", 0.9696969696969697)]
    [InlineData("checkingforupdates", "updateinstalled", 0.42424242424242425)]
    [InlineData("contextleft", "contextleft", 1.0)]
    [InlineData("abc", "xyz", 0.0)]
    public void Ratio_matches_python_exactly(string a, string b, double expected)
    {
        Assert.Equal(expected, TerminalContentNovelty.SequenceRatio(a, b), Exact);
    }

    [Fact]
    public void Ratio_is_the_difflib_algorithm_and_not_a_longest_common_subsequence_ratio()
    {
        // These two strings are the reason this whole file exists. Python's difflib scores them at
        // 0.16, because it commits to the single longest matching block and then recurses only
        // into what is left and right of it. A longest-common-subsequence ratio scores the same
        // pair at 0.64, because it is free to pick matching characters from anywhere. Both are
        // defensible definitions of "how similar"; only one of them produced the corpus numbers.
        const string settled = "fdaedbaeadcf";
        const string candidate = "fedcbeaeeedfd";

        Assert.Equal(0.16, TerminalContentNovelty.SequenceRatio(settled, candidate), Exact);

        // The longest common subsequence of the two is 8 characters, so a ratio built on it would
        // be 2 * 8 / 25 = 0.64. Asserting the gap makes the substitution impossible to make
        // quietly: an implementation that returns 0.64 here fails with the number that names the
        // mistake rather than an anonymous mismatch.
        Assert.Equal(8, LongestCommonSubsequenceLength(settled, candidate));
        Assert.NotEqual(0.64, TerminalContentNovelty.SequenceRatio(settled, candidate), Exact);
    }

    [Fact]
    public void Ratio_is_not_symmetric_so_the_orientation_is_load_bearing()
    {
        // The index is built over the SECOND sequence, so swapping the arguments can change the
        // answer. The harness compares the settled row as A against the candidate row as B, and
        // the production rule must keep that orientation or it is scoring a different function.
        // Both values below are Python's, for the same two strings in the two orders.
        const string a = "abbbaac";
        const string b = "bccbc";

        Assert.Equal(0.5, TerminalContentNovelty.SequenceRatio(a, b), Exact);
        Assert.Equal(0.3333333333333333, TerminalContentNovelty.SequenceRatio(b, a), Exact);
    }

    [Fact]
    public void Ratio_reproduces_every_pair_in_the_python_generated_fixture()
    {
        var pairs = LoadFixture();

        // A fixture that failed to load would make this test pass by having nothing to check, so
        // the count is asserted before the pairs are.
        Assert.True(pairs.Length >= 300,
            $"The pinned ratio fixture should carry at least 300 pairs; it carried {pairs.Length}.");

        int overTheAutoJunkBoundary = 0;
        foreach (var pair in pairs)
        {
            if (pair.B.Length >= PythonSequenceMatcher.AutoJunkMinimumLength) overTheAutoJunkBoundary++;
            double actual = TerminalContentNovelty.SequenceRatio(pair.A, pair.B);
            Assert.True(System.Math.Abs(actual - pair.Ratio) < Exact,
                $"ratio mismatch: a={pair.A} b={pair.B} python={pair.Ratio} ours={actual}");
        }

        // Python's popularity heuristic only engages from 200 elements up. If the fixture stopped
        // carrying pairs that long, this test would still pass while covering none of that branch,
        // so the coverage is asserted rather than assumed.
        Assert.True(overTheAutoJunkBoundary >= 20,
            $"The fixture should exercise the popularity heuristic; only {overTheAutoJunkBoundary} " +
            "pairs reached 200 elements.");
    }

    [Fact]
    public void The_popularity_heuristic_engages_only_from_two_hundred_elements()
    {
        // Below 200 the heuristic does nothing at all, so a sequence of one repeated character
        // matches itself completely. At 200 and above, a character occurring in more than one
        // percent of the sequence is dropped from the index and can no longer SEED a match -
        // although a match that reaches it still extends over it, which is why the answer here is
        // still 1.0 and not 0. Both of these are Python's answers, not ours.
        string justUnder = new string('a', 199);
        string justOver = new string('a', 200);

        Assert.Equal(1.0, TerminalContentNovelty.SequenceRatio(justUnder, justUnder), Exact);
        Assert.Equal(1.0, TerminalContentNovelty.SequenceRatio(justOver, justOver), Exact);

        // Where it bites: with every seed purged, a sequence that shares characters but never
        // aligns at the ends scores zero instead of the high score an unpurged index would give.
        string a = new string('a', 300);
        string b = "b" + new string('a', 299);
        Assert.Equal(0.0, TerminalContentNovelty.SequenceRatio(a, b), Exact);
    }

    // ----------------------------------------------------------------------------------------

    private sealed record Pair(string A, string B, double Ratio);

    private static Pair[] LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "turn-detection",
            "difflib-ratios.json");
        Assert.True(File.Exists(path), $"The pinned ratio fixture is missing: {path}");

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var pairs = new List<Pair>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            pairs.Add(new Pair(
                element.GetProperty("a").GetString()!,
                element.GetProperty("b").GetString()!,
                element.GetProperty("ratio").GetDouble()));
        }
        return pairs.ToArray();
    }

    /// <summary>
    /// The rival definition, written out in full so the test above can name the number it is
    /// rejecting. It exists only in this test file and nothing in the product calls it.
    /// </summary>
    private static int LongestCommonSubsequenceLength(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                current[j + 1] = a[i] == b[j]
                    ? previous[j] + 1
                    : System.Math.Max(previous[j + 1], current[j]);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
