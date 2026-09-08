using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// <see cref="ParityJson"/> against the bytes Python's <c>json.dumps(sort_keys=True, indent=2,
/// ensure_ascii=True)</c> writes. Every expected string here was produced by that call and copied in.
/// </summary>
public sealed class ParityJsonTests
{
    [Theory]
    [InlineData(12.0, "12.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(1.5e-05, "1.5e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(123456.789, "123456.789")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.0, "0.0")]
    [InlineData(2.5e-07, "2.5e-07")]
    [InlineData(1234567890123456789.0, "1.2345678901234568e+18")]
    [InlineData(0.3333, "0.3333")]
    [InlineData(100.0, "100.0")]
    public void A_float_is_written_as_pythons_repr(double value, string expected)
        => Assert.Equal(expected, PythonFloat.Repr(value));

    [Fact]
    public void Integers_stay_integers_and_floats_stay_floats()
    {
        Assert.Equal("12", ParityJson.Pretty(12));
        Assert.Equal("12", ParityJson.Pretty(12L));
        Assert.Equal("12.0", ParityJson.Pretty(12.0));
        Assert.Equal("1e-05", ParityJson.Pretty(1e-05));
    }

    [Fact]
    public void Keys_are_sorted_and_nested_values_are_indented_two_spaces()
    {
        var value = new Dictionary<string, object?>
        {
            ["zeta"] = new List<object?> { 1, 2.0, "x" },
            ["alpha"] = new Dictionary<string, object?> { ["b"] = true, ["a"] = null },
            ["empty_list"] = new List<object?>(),
            ["empty_object"] = new Dictionary<string, object?>(),
        };
        var expected = "{\n  \"alpha\": {\n    \"a\": null,\n    \"b\": true\n  },\n  \"empty_list\": [],\n  \"empty_object\": {},\n  \"zeta\": [\n    1,\n    2.0,\n    \"x\"\n  ]\n}";
        Assert.Equal(expected, ParityJson.Pretty(value));
    }

    [Fact]
    public void Strings_are_escaped_as_ensure_ascii_does()
    {
        Assert.Equal("\"say \\\"hi\\\" \\\\ tab\\t nl\\n cr\\r bs\\b ff\\f bell\\u0007 del\\u007f\"",
            ParityJson.Pretty("say \"hi\" \\ tab\t nl\n cr\r bs\b ff\f bell\a del\x7f"));
        Assert.Equal("\"caf\\u00e9 \\u201cquoted\\u201d \\ud83d\\ude00\"", ParityJson.Pretty("caf\u00e9 \u201cquoted\u201d \U0001F600"));
    }

    [Fact]
    public void Compact_keeps_insertion_order_with_pythons_default_separators()
    {
        var value = new Dictionary<string, object?> { ["seq"] = 1, ["tool"] = "cite", ["args"] = new Dictionary<string, object?> { ["at"] = "x" }, ["ok"] = true, ["list"] = new List<object?> { 1, 2 } };
        Assert.Equal("{\"seq\": 1, \"tool\": \"cite\", \"args\": {\"at\": \"x\"}, \"ok\": true, \"list\": [1, 2]}", ParityJson.Compact(value));
        Assert.Equal("{}", ParityJson.Compact(new Dictionary<string, object?>()));
        Assert.Equal("[]", ParityJson.Compact(new List<object?>()));
    }

    [Fact]
    public void A_type_the_port_did_not_decide_is_refused()
    {
        Assert.Throws<MentorDataException>(() => ParityJson.Pretty(new DateTime(2026, 1, 1)));
        Assert.Throws<MentorDataException>(() => ParityJson.Pretty(1.5m));
    }
}
