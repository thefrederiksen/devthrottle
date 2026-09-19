using CcDirector.Reclaim.Reporting;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The shape every line of output takes. A path can hold a comma and a quotation mark, so a row that
/// wrote one plainly would break every reader of it.
/// </summary>
public class AxiOutputTests
{
    [Fact]
    public void Value_OrdinaryText_IsWrittenAsItStands()
    {
        Assert.Equal("D:\\repos\\thing", AxiOutput.Value("D:\\repos\\thing"));
    }

    /// <summary>
    /// A path with a comma in it has to be quoted, and inside quotes a backslash is written twice, so
    /// that a reader can take the path back out exactly as it went in.
    /// </summary>
    [Fact]
    public void Value_TextHoldingAComma_IsQuotedAndItsBackslashesAreEscaped()
    {
        Assert.Equal("\"D:\\\\one, two\"", AxiOutput.Value("D:\\one, two"));
    }

    [Fact]
    public void Value_TextHoldingADoubleQuote_IsQuotedAndTheQuoteIsEscaped()
    {
        Assert.Equal("\"say \\\"hello\\\"\"", AxiOutput.Value("say \"hello\""));
    }

    [Fact]
    public void Value_TextThatIsNotAscii_IsWrittenAsAnEscapeSoEveryLineStaysAscii()
    {
        var rendered = AxiOutput.Value("caf\u00e9");

        Assert.Equal("\"caf\\u00e9\"", rendered);
        Assert.All(rendered, character => Assert.InRange(character, (char)0x20, (char)0x7E));
    }

    [Fact]
    public void Value_TextWithSpaceAtTheEnds_IsQuotedSoTheSpaceSurvives()
    {
        Assert.Equal("\" padded \"", AxiOutput.Value(" padded "));
    }

    [Fact]
    public void Value_Nothing_IsWrittenAsNothingAtAll()
    {
        Assert.Equal(string.Empty, AxiOutput.Value((string?)null));
    }

    [Fact]
    public void Value_TheEmptyString_IsWrittenAsAPairOfQuotesSoItIsNotMistakenForNothing()
    {
        Assert.Equal("\"\"", AxiOutput.Value(string.Empty));
    }

    [Fact]
    public void List_WithRows_IsAHeaderAndOneIndentedRowEach()
    {
        var lines = AxiOutput.List(
            "folders",
            ["path", "bytes"],
            [
                [AxiOutput.Value("D:\\one"), AxiOutput.Value(10L)],
                [AxiOutput.Value("D:\\two"), AxiOutput.Value(20L)]
            ]);

        Assert.Equal(
            new[] { "folders[2]{path,bytes}:", "  D:\\one,10", "  D:\\two,20" },
            lines.ToArray());
    }

    /// <summary>
    /// An empty list prints its header with a nought in it. Printing nothing at all would leave a
    /// reader unable to tell an empty answer from a command that never ran.
    /// </summary>
    [Fact]
    public void List_WithNoRows_IsStillAHeaderSayingNought()
    {
        var lines = AxiOutput.List("folders", ["path"], []);

        Assert.Equal(new[] { "folders[0]{path}:" }, lines.ToArray());
    }

    [Fact]
    public void List_ARowWithTheWrongNumberOfValues_Throws()
    {
        Assert.Throws<ArgumentException>(() => AxiOutput.List(
            "folders",
            ["path", "bytes"],
            [[AxiOutput.Value("D:\\one")]]));
    }

    [Fact]
    public void List_ANameThatWouldSplitTheHeaderLine_Throws()
    {
        Assert.Throws<ArgumentException>(() => AxiOutput.List("folders and things", ["path"], []));
    }

    [Fact]
    public void Help_WithCommands_IsAHeaderAndOneIndentedCommandEach()
    {
        var lines = AxiOutput.Help(["cc-cleanup-storage scan \"<folder>\""]);

        Assert.Equal(
            new[] { "help[1]:", "  cc-cleanup-storage scan \"<folder>\"" },
            lines.ToArray());
    }

    [Fact]
    public void Help_WithNoCommands_ThrowsRatherThanPrintingAnEmptyOffer()
    {
        Assert.Throws<ArgumentException>(() => AxiOutput.Help([]));
    }
}
