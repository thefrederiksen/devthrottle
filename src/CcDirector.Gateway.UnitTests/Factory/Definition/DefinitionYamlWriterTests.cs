using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE EXPORT'S YAML WRITER (Factory Definition, phase 3). Every value it writes must be read back by the definition's
/// own parser as exactly that text - not a number, not true or false, not nothing, not a trimmed or re-wrapped string -
/// wherever it sits: after a key, as a list item, or as the first key of a list item. Each case below is a value YAML
/// would otherwise read differently.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class DefinitionYamlWriterTests
{
    public static readonly TheoryData<string> Values = new()
    {
        "", "Boss", "SOREN_NORTH", "Europe/Copenhagen", "claude-opus-5-5", "Find and sell", "Morning round.",
        "true", "False", "null", "~", "123", "0x1F", "0o17", "1e3", ".5", "-1", ".inf", ".NaN", "yes",
        "a: b", "a:b", "x #y", "# hash", "- dash", "-", "?", "@at", "`tick", "{x}", "[x]", "*star", "&amp", "!bang",
        "%pct", "|pipe", ">gt", "'single", "\"double\"", "back\\slash", "0 8 * * 1-5", " leading", "trailing ",
        "two  spaces", "tab\there", "\u00C6r\u00F8 \u00E6\u00F8\u00E5", "emoji \U0001F600 pair",
        "one line\n", "two\nlines", "two\nlines\n", "two\nlines\n\n\n", "a\n\nb\n", "  indented\nfirst\n",
        "first\n  indented\n", "\nstarts empty\n", "a\n   \nb\n", "crlf\r\nline\r\n", "\n", "\n\n",
        "control\u0001char", "bom\uFEFFinside", "ends with colon:", "line\n- looks like a list\n",
        "key: value\nother: value\n",
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void A_VALUE_AFTER_A_KEY_READS_BACK_AS_EXACTLY_THAT_TEXT(string value)
    {
        var y = new DefinitionYamlWriter().Scalar(0, "brief", value).Scalar(0, "after", "x").ToString();

        var map = Parse(y);

        AssertText(value, map.Find("brief")!.Value, y);
        AssertText("x", map.Find("after")!.Value, y);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void A_VALUE_NESTED_IN_A_LIST_ITEM_READS_BACK_AS_EXACTLY_THAT_TEXT(string value)
    {
        var y = new DefinitionYamlWriter()
            .Open(0, "starts")
            .Item(1, "cron", value)
            .Scalar(2, "prompt", value)
            .Item(1, "cron", "second")
            .List(0, "rules", new[] { value, "base" })
            .ToString();

        var map = Parse(y);

        var starts = Assert.IsType<DefinitionList>(map.Find("starts")!.Value);
        Assert.Equal(2, starts.Items.Count);
        var first = Assert.IsType<DefinitionMap>(starts.Items[0]);
        AssertText(value, first.Find("cron")!.Value, y);
        AssertText(value, first.Find("prompt")!.Value, y);
        AssertText("second", Assert.IsType<DefinitionMap>(starts.Items[1]).Find("cron")!.Value, y);
        var rules = Assert.IsType<DefinitionList>(map.Find("rules")!.Value);
        AssertText(value, rules.Items[0], y);
        AssertText("base", rules.Items[1], y);
    }

    [Fact]
    public void A_BRIEF_OF_SEVERAL_LINES_IS_WRITTEN_AS_A_LITERAL_BLOCK_A_PERSON_CAN_EDIT()
    {
        var y = new DefinitionYamlWriter().Scalar(0, "brief", "Run the factory.\nRead what the writer made.\n").ToString();

        Assert.Equal("brief: |\n  Run the factory.\n  Read what the writer made.\n", y);
    }

    [Fact]
    public void THE_SAME_VALUES_GIVE_THE_SAME_BYTES_AND_EVERY_LINE_ENDS_IN_ONE_LINE_FEED()
    {
        static string Write() => new DefinitionYamlWriter()
            .Scalar(0, "factory", "devthrottle").Open(0, "defaults").Scalar(1, "machine", "SOREN_NORTH")
            .Boolean(1, "flag", false).Scalar(0, "brief", "a\r\nb\n").ToString();

        Assert.Equal(Write(), Write());
        Assert.DoesNotContain("\r", Write().Replace("\\r", ""));
        Assert.EndsWith("\n", Write());
    }

    [Fact]
    public void AN_EMPTY_LIST_IS_NOT_WRITTEN_AT_ALL()
    {
        Assert.Equal("seat: boss\n", new DefinitionYamlWriter().Scalar(0, "seat", "boss").List(0, "rules", Array.Empty<string>()).ToString());
    }

    private static DefinitionMap Parse(string yaml)
    {
        var (root, error) = DefinitionDocument.ParseYaml("test.yaml", yaml);
        Assert.True(error is null, $"the writer's YAML did not parse: {error?.Message}\n{yaml}");
        return Assert.IsType<DefinitionMap>(root);
    }

    private static void AssertText(string expected, DefinitionNode node, string yaml)
    {
        var scalar = Assert.IsType<DefinitionScalar>(node);
        Assert.True(scalar.Kind == DefinitionScalarKind.Text, $"read as {scalar.Kind}, not text:\n{yaml}");
        Assert.True(expected == scalar.Value, $"wrote [{Escape(expected)}] and read back [{Escape(scalar.Value)}]:\n{yaml}");
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
