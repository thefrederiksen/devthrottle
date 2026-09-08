using System.Globalization;
using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The Python numerics pinned to Python's own answers. Every expected value below was printed by the
/// reference interpreter (3.11.6) - round(x, n), "%.2f" % x, statistics.median, difflib.SequenceMatcher and
/// str.lower() - over the shares, hours and minutes the W36 document holds and over the binary-midpoint
/// cases where a scaled rounding differs from an exact one.
/// </summary>
public sealed class PyNumbersTests
{
    private static double Parse(string repr) => double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture);

    // (Python repr of x, digits, Python repr of round(x, digits))
    private static readonly (string X, int Digits, string Expected)[] RoundCases =
    {
        ("0.3333333333333333", 4, "0.3333"), ("0.6666666666666666", 4, "0.6667"), ("0.16666666666666666", 4, "0.1667"), ("0.8333333333333334", 4, "0.8333"),
        ("0.14285714285714285", 4, "0.1429"), ("0.2857142857142857", 4, "0.2857"), ("0.42857142857142855", 4, "0.4286"), ("0.1111111111111111", 4, "0.1111"),
        ("0.4444444444444444", 4, "0.4444"), ("0.09090909090909091", 4, "0.0909"), ("0.08333333333333333", 4, "0.0833"), ("0.4166666666666667", 4, "0.4167"),
        ("0.5833333333333334", 4, "0.5833"), ("0.07692307692307693", 4, "0.0769"), ("0.13333333333333333", 4, "0.1333"), ("0.4666666666666667", 4, "0.4667"),
        ("0.6470588235294118", 4, "0.6471"), ("0.05263157894736842", 4, "0.0526"), ("0.047619047619047616", 4, "0.0476"), ("0.09523809523809523", 4, "0.0952"),
        ("0.043478260869565216", 4, "0.0435"), ("0.034482758620689655", 4, "0.0345"), ("0.3548387096774194", 4, "0.3548"), ("0.02702702702702703", 4, "0.027"),
        ("0.4634146341463415", 4, "0.4634"), ("0.023255813953488372", 4, "0.0233"), ("0.02127659574468085", 4, "0.0213"), ("0.02040816326530612", 4, "0.0204"),
        ("0.018867924528301886", 4, "0.0189"), ("0.01694915254237288", 4, "0.0169"), ("0.01639344262295082", 4, "0.0164"), ("0.014925373134328358", 4, "0.0149"),
        ("0.014084507042253521", 4, "0.0141"), ("0.0136986301369863", 4, "0.0137"), ("0.012658227848101266", 4, "0.0127"), ("0.012048192771084338", 4, "0.012"),
        ("0.011235955056179775", 4, "0.0112"), ("0.010309278350515464", 4, "0.0103"), ("0.3069306930693069", 4, "0.3069"), ("0.03336864406779661", 4, "0.0334"),
        ("0.09057203389830508", 4, "0.0906"), ("0.2865466101694915", 4, "0.2865"), ("0.011652542372881356", 4, "0.0117"), ("0.02754237288135593", 4, "0.0275"),
        ("0.5259533898305084", 4, "0.526"), ("0.7631133671742809", 4, "0.7631"), ("0.2182741116751269", 4, "0.2183"), ("0.18274111675126903", 4, "0.1827"),
        ("0.47377326565143824", 4, "0.4738"), ("0.010090393104898045", 4, "0.0101"), ("0.125", 4, "0.125"), ("0.375", 4, "0.375"), ("0.625", 4, "0.625"),
        ("0.875", 4, "0.875"), ("0.0625", 4, "0.0625"), ("0.5625", 4, "0.5625"), ("0.03125", 4, "0.0312"), ("0.015625", 4, "0.0156"), ("0.0078125", 4, "0.0078"),
        ("0.00390625", 4, "0.0039"), ("0.0001", 4, "0.0001"), ("5e-05", 4, "0.0001"), ("0.00015", 4, "0.0001"), ("0.00025", 4, "0.0003"), ("0.00035", 4, "0.0003"),
        ("0.00045", 4, "0.0004"), ("0.00055", 4, "0.0006"), ("0.00065", 4, "0.0006"), ("0.00075", 4, "0.0008"), ("0.00085", 4, "0.0008"), ("0.00095", 4, "0.0009"),
        ("0.000125", 4, "0.0001"), ("0.000375", 4, "0.0004"), ("0.000625", 4, "0.0006"), ("0.000875", 4, "0.0009"), ("0.5", 4, "0.5"), ("0.25", 4, "0.25"),
        ("0.75", 4, "0.75"), ("0.0", 4, "0.0"), ("2.675", 4, "2.675"), ("1.0005", 4, "1.0005"), ("1.00005", 4, "1.0001"), ("1234.56785", 4, "1234.5678"),
        ("59.95", 4, "59.95"), ("890.3", 4, "890.3"), ("50.6", 4, "50.6"), ("555.0", 4, "555.0"), ("7.4", 4, "7.4"), ("6.9", 4, "6.9"), ("985.473", 4, "985.473"),
        ("1634.501", 4, "1634.501"), ("58.84", 4, "58.84"), ("31.273", 4, "31.273"), ("1103.318", 4, "1103.318"), ("1390.671", 4, "1390.671"),
        ("231150.5", 4, "231150.5"), ("691960.5", 4, "691960.5"),
        ("1634.1676666666665", 3, "1634.168"), ("1.6666666666666667", 3, "1.667"), ("1.0", 3, "1.0"), ("1.5", 3, "1.5"), ("0.016805555555555556", 3, "0.017"),
        ("0.0002777777777777778", 3, "0.0"), ("1.3888888888888888e-07", 3, "0.0"), ("4.1666666666666667e-07", 3, "0.0"), ("0.0012777777777777776", 3, "0.001"),
        ("0.003375", 3, "0.003"), ("0.0034027777777777776", 3, "0.003"), ("0.0034305555555555556", 3, "0.003"), ("0.0034583333333333332", 3, "0.003"),
        ("0.00016666666666666666", 3, "0.0"), ("0.0006805555555555556", 3, "0.001"), ("0.0007083333333333333", 3, "0.001"), ("0.8433333333333334", 3, "0.843"),
        ("1.0004166666666667", 3, "1.0"), ("1390.6709999999998", 3, "1390.671"),
        ("50.6", 1, "50.6"), ("1.0", 1, "1.0"), ("1.5", 1, "1.5"), ("2.5", 1, "2.5"), ("0.05", 1, "0.1"), ("0.15", 1, "0.1"), ("0.25", 1, "0.2"), ("0.35", 1, "0.3"),
        ("0.45", 1, "0.5"), ("0.55", 1, "0.6"), ("0.65", 1, "0.7"), ("0.75", 1, "0.8"), ("0.85", 1, "0.8"), ("0.95", 1, "0.9"), ("1.05", 1, "1.1"), ("1.15", 1, "1.1"),
        ("1.25", 1, "1.2"), ("1.35", 1, "1.4"), ("1.45", 1, "1.4"), ("1.55", 1, "1.6"), ("1.65", 1, "1.6"), ("6.9", 1, "6.9"), ("20.576116666666667", 1, "20.6"),
        ("0.05000001666666667", 1, "0.1"), ("0.14999998333333334", 1, "0.1"), ("0.4500000666666667", 1, "0.5"),
        ("1.3333333333333333", 4, "1.3333"), ("1.4285714285714286", 4, "1.4286"), ("1.1818181818181819", 4, "1.1818"), ("2.25", 4, "2.25"),
        ("1.1666666666666667", 4, "1.1667"), ("3.142857142857143", 4, "3.1429"), ("2.3", 4, "2.3"),
    };

    [Fact]
    public void Round_is_pythons_round_on_the_exact_binary_value()
    {
        foreach (var (x, digits, expected) in RoundCases)
            Assert.True(expected == PythonFloat.Repr(PyNumbers.Round(Parse(x), digits)),
                "round(" + x + ", " + digits + "): Python " + expected + ", port " + PythonFloat.Repr(PyNumbers.Round(Parse(x), digits)));
    }

    [Fact]
    public void Round_keeps_a_share_that_a_scaled_rounding_would_move()
    {
        // The two directions of the midpoint rule on values that are NOT exact midpoints in binary.
        Assert.Equal(0.0001, PyNumbers.Round(0.00015, 4));
        Assert.Equal(0.0003, PyNumbers.Round(0.00025, 4));
        Assert.Equal(0.0312, PyNumbers.Round(0.03125, 4));   // an exact midpoint: half to even
        Assert.Equal(0.1, PyNumbers.Round(0.05, 1));
        Assert.Equal(0.1, PyNumbers.Round(0.15, 1));
    }

    // (Python repr of x, "%.2f" % (x * 100.0), percent_text(x))
    private static readonly (string X, string Fixed, string Percent)[] FixedCases =
    {
        ("0.9091", "90.91", "90.91"), ("0.5", "50.00", "50"), ("0.03", "3.00", "3"), ("0.0001", "0.01", "0.01"), ("5e-05", "0.01", "0.01"),
        ("0.005", "0.50", "0.5"), ("0.0125", "1.25", "1.25"), ("0.045", "4.50", "4.5"), ("0.055", "5.50", "5.5"), ("0.1", "10.00", "10"),
        ("0.25", "25.00", "25"), ("1.0", "100.00", "100"), ("0.0", "0.00", "0"), ("0.3333", "33.33", "33.33"), ("0.6667", "66.67", "66.67"),
        ("0.0722", "7.22", "7.22"), ("0.12345", "12.35", "12.35"), ("0.99995", "100.00", "100"), ("0.999949", "99.99", "99.99"),
        ("0.6666666666666666", "66.67", "66.67"), ("0.3333333333333333", "33.33", "33.33"), ("0.14285714285714285", "14.29", "14.29"),
        ("0.00015", "0.01", "0.01"), ("0.00025", "0.03", "0.03"), ("0.00035", "0.03", "0.03"), ("0.00045", "0.04", "0.04"),
    };

    [Fact]
    public void FormatFixed_and_PercentText_are_pythons()
    {
        foreach (var (x, fixedText, percent) in FixedCases)
        {
            Assert.Equal(fixedText, PyNumbers.FormatFixed(Parse(x) * 100.0, 2));
            Assert.Equal(percent, Metrics.PercentText(Parse(x)));
        }
    }

    [Fact]
    public void BaselineOf_is_the_statistics_median_rounded_and_made_whole_when_whole()
    {
        var cases = new (object?[] Priors, string Expected)[]
        {
            (new object?[] { 1L, 2L, 3L, 100L }, "2.5"), (new object?[] { 1L, 0L, 3L, 100L }, "2"), (new object?[] { 0L, 2L, 3L }, "2"),
            (new object?[] { 1.0, 0.0 }, "0.5"), (new object?[] { 3L, 4L }, "3.5"), (new object?[] { 2L, 5L, 7L }, "5"), (new object?[] { 0.5, 1L }, "0.75"),
            (new object?[] { 1L, 1.5 }, "1.25"), (new object?[] { 0L, 0L, 0L, 1L }, "0"), (new object?[] { 2.5 }, "2.5"), (new object?[] { 1L, 2L }, "1.5"),
            (new object?[] { 0.1, 0.2, 0.3, 0.4 }, "0.25"), (new object?[] { 0.1234, 0.5678 }, "0.3456"),
        };
        foreach (var (priors, expected) in cases)
            Assert.Equal(expected, PyNumbers.Str(Metrics.BaselineOf(0L, priors)));
        // A missing key in a prior dict counts as zero; a non-number prior does not count; no numbers is no baseline.
        Assert.Null(Metrics.BaselineOf(1L, new object?[] { null, "x", true }));
        var current = new Dictionary<string, object?> { ["a"] = 4L, ["b"] = 0.5 };
        var priors2 = new object?[] { new Dictionary<string, object?> { ["a"] = 2L }, new Dictionary<string, object?> { ["a"] = 6L, ["b"] = 0.25 } };
        var baseline = Assert.IsType<Dictionary<string, object?>>(Metrics.BaselineOf(current, priors2));
        Assert.Equal(4L, Assert.IsType<long>(baseline["a"]));
        Assert.Equal(0.125, Assert.IsType<double>(baseline["b"]));   // (0 + 0.25) / 2
        Assert.Null(Metrics.BaselineOf(new List<object?> { 1L }, new object?[] { new List<object?> { 2L } }));
        Assert.Null(Metrics.BaselineOf(true, new object?[] { true }));
    }

    [Fact]
    public void ChangedWordCount_is_difflibs()
    {
        var cases = new (string Raw, string Cleaned, int Expected)[]
        {
            ("a b c", "a b c", 0), ("a b c", "a x c", 1), ("a b c d", "a c", 2), ("one two three", "one two three four", 1), ("the cat sat", "cat sat down", 2),
            ("hello world", "world hello", 2), ("a b a b", "b a b a", 2), ("um so I think we should um go", "So I think we should go.", 4),
            ("x y z x y z x y z", "x y z q x y z", 4), ("", "a b", 2), ("a b", "", 2), ("a b c a b c", "a b c", 3), ("repeat repeat repeat", "repeat", 2),
            ("alpha beta gamma delta", "delta gamma beta alpha", 6), ("one", "one two three four five six", 5),
        };
        foreach (var (raw, cleaned, expected) in cases)
            Assert.True(expected == PyDifflib.ChangedWordCount(raw, cleaned), raw + " -> " + cleaned + ": Python " + expected + ", port " + PyDifflib.ChangedWordCount(raw, cleaned));
    }

    [Fact]
    public void Lower_is_pythons_lower_with_the_dotted_I_and_the_final_sigma()
    {
        Assert.Equal("i\u0307stanbul", PyText.Lower("\u0130stanbul"));
        Assert.Equal(9, PyText.Lower("\u0130stanbul").Length);
        Assert.Equal("\u03c3\u03b1\u03c2", PyText.Lower("\u03a3\u0391\u03a3"));
        Assert.Equal("\u03b4 abc", PyText.Lower("\u0394 ABC"));
        Assert.Equal("a\u03c2.", PyText.Lower("A\u03a3."));
        Assert.Equal("\u03c3", PyText.Lower("\u03a3"));
        Assert.Equal("caf\u00e9 quoted", PyText.Lower("CAF\u00c9 Quoted"));
    }

    [Fact]
    public void TotalSeconds_is_microseconds_over_a_million_and_refuses_finer_ticks()
    {
        Assert.Equal(3092.595156, PyNumbers.TotalSeconds(TimeSpan.FromTicks(30925951560)));
        Assert.Equal(60.0, PyNumbers.TotalSeconds(TimeSpan.FromMinutes(1)));
        Assert.Throws<MentorDataException>(() => PyNumbers.TotalSeconds(TimeSpan.FromTicks(15)));
    }

    [Fact]
    public void Str_is_pythons_str_of_a_number()
    {
        Assert.Equal("0.0588", PyNumbers.Str(0.0588));
        Assert.Equal("0.0", PyNumbers.Str(0.0));
        Assert.Equal("900000", PyNumbers.Str(900000L));
        Assert.Equal("None", PyNumbers.Str(null));
    }
}
