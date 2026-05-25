using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class PromptContentParserTests
{
    [Theory]
    [InlineData("D:/Pictures/shot.png")]
    [InlineData("D:\\Pictures\\shot.PNG")]
    [InlineData("/home/user/diagram.jpeg")]
    [InlineData("screenshot.jpg")]
    [InlineData("\"C:\\Path With Spaces\\image.webp\"")]
    public void LooksLikeImagePath_ValidPaths_ReturnsTrue(string line)
    {
        Assert.True(PromptContentParser.LooksLikeImagePath(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("please look at the diagram.png and tell me")]
    [InlineData("notes.txt")]
    [InlineData("C:/folder/report.pdf")]
    [InlineData("just some text")]
    public void LooksLikeImagePath_NonImageOrProse_ReturnsFalse(string line)
    {
        Assert.False(PromptContentParser.LooksLikeImagePath(line));
    }

    [Fact]
    public void Parse_Empty_ReturnsNoSegments()
    {
        Assert.Empty(PromptContentParser.Parse(""));
        Assert.Empty(PromptContentParser.Parse(null));
    }

    [Fact]
    public void Parse_PlainText_ReturnsSingleTextSegment()
    {
        var segments = PromptContentParser.Parse("fix the login bug\nit fails on submit");

        Assert.Single(segments);
        Assert.Equal(PromptSegmentKind.Text, segments[0].Kind);
        Assert.Equal("fix the login bug\nit fails on submit", segments[0].Content);
    }

    [Fact]
    public void Parse_TextThenImage_SplitsIntoTwoSegments()
    {
        var text = "here is the bug\nD:/Pictures/error.png";

        var segments = PromptContentParser.Parse(text);

        Assert.Equal(2, segments.Count);
        Assert.Equal(PromptSegmentKind.Text, segments[0].Kind);
        Assert.Equal("here is the bug", segments[0].Content);
        Assert.Equal(PromptSegmentKind.Image, segments[1].Kind);
        Assert.Equal("D:/Pictures/error.png", segments[1].Content);
    }

    [Fact]
    public void Parse_ImageBetweenText_ProducesThreeSegmentsInOrder()
    {
        var text = "before\nD:/a/one.png\nafter";

        var segments = PromptContentParser.Parse(text);

        Assert.Equal(3, segments.Count);
        Assert.Equal(PromptSegmentKind.Text, segments[0].Kind);
        Assert.Equal("before", segments[0].Content);
        Assert.Equal(PromptSegmentKind.Image, segments[1].Kind);
        Assert.Equal("D:/a/one.png", segments[1].Content);
        Assert.Equal(PromptSegmentKind.Text, segments[2].Kind);
        Assert.Equal("after", segments[2].Content);
    }

    [Fact]
    public void Parse_QuotedImagePath_StripsQuotes()
    {
        var segments = PromptContentParser.Parse("\"C:\\My Pics\\a.jpg\"");

        Assert.Single(segments);
        Assert.Equal(PromptSegmentKind.Image, segments[0].Kind);
        Assert.Equal("C:\\My Pics\\a.jpg", segments[0].Content);
    }

    [Fact]
    public void Parse_MultipleConsecutiveImages_EachIsOwnSegment()
    {
        var text = "D:/a/one.png\nD:/a/two.jpg";

        var segments = PromptContentParser.Parse(text);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(PromptSegmentKind.Image, s.Kind));
    }
}
