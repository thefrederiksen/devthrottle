using CcDirector.Cockpit.Services;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Tests for <see cref="SpeechText.ToPlain"/> - the Markdown-to-spoken-prose conversion used for the
/// Mobile Speak button and its on-screen caption. It keeps the words faithful while removing markup
/// that reads badly aloud (asterisks, backticks, heading hashes, table pipes, whole code blocks).
/// </summary>
public class SpeechTextTests
{
    [Fact]
    public void ToPlain_Emphasis_Stripped()
    {
        Assert.Equal("This is bold and italic text.",
            SpeechText.ToPlain("This is **bold** and *italic* text."));
    }

    [Fact]
    public void ToPlain_InlineCode_KeepsTheWord()
    {
        Assert.Equal("Call ToHtml on it.", SpeechText.ToPlain("Call `ToHtml` on it."));
    }

    [Fact]
    public void ToPlain_Link_KeepsTextDropsUrl()
    {
        Assert.Equal("See the docs.", SpeechText.ToPlain("See the [docs](https://example.com/x)."));
    }

    [Fact]
    public void ToPlain_Heading_HashesRemoved()
    {
        Assert.Equal("The part that matters", SpeechText.ToPlain("### The part that matters"));
    }

    [Fact]
    public void ToPlain_BulletList_MarkersRemoved()
    {
        Assert.Equal("first\nsecond", SpeechText.ToPlain("- first\n- second"));
    }

    [Fact]
    public void ToPlain_FencedCode_ReplacedWithPlaceholder()
    {
        var result = SpeechText.ToPlain("Here:\n```\nvar x = 1;\n```\nDone.");
        Assert.Contains("(code block)", result);
        Assert.DoesNotContain("var x = 1", result);
    }

    [Fact]
    public void ToPlain_Table_SeparatorDroppedAndPipesBecomeCommas()
    {
        var md = "| Layer | Result |\n|---|---|\n| Build | Clean |";
        var result = SpeechText.ToPlain(md);
        Assert.DoesNotContain("|", result);
        Assert.DoesNotContain("---", result);
        Assert.Contains("Layer, Result", result);
        Assert.Contains("Build, Clean", result);
    }

    [Fact]
    public void ToPlain_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", SpeechText.ToPlain(null));
        Assert.Equal("", SpeechText.ToPlain("   "));
    }
}
