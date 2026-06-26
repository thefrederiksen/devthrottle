using CcDirector.Cockpit.Services;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Tests for <see cref="HistoryLinks"/> - the #740 link extraction. Confirms URLs and absolute
/// host paths are detected (via the shared LinkDetector), relative paths are NOT guessed in the
/// browser context (no existence check), and duplicates collapse.
/// </summary>
public class HistoryLinksTests
{
    [Fact]
    public void Extract_Url_IsDetectedAsUrl()
    {
        var links = HistoryLinks.Extract("open https://example.com/page now");
        var link = Assert.Single(links);
        Assert.True(link.IsUrl);
        Assert.Equal("https://example.com/page", link.Text);
    }

    [Fact]
    public void Extract_AbsoluteWindowsPath_IsDetectedAsPath()
    {
        var links = HistoryLinks.Extract(@"edited C:\Repos\app\Program.cs today");
        Assert.Contains(links, l => !l.IsUrl && l.Text.Contains("Program.cs"));
    }

    [Fact]
    public void Extract_RelativePath_IsNotGuessed()
    {
        // No repo root and no existence check in the browser context, so a bare relative path is
        // deliberately not surfaced (avoids false positives).
        var links = HistoryLinks.Extract("see src/app/Program.cs");
        Assert.DoesNotContain(links, l => !l.IsUrl);
    }

    [Fact]
    public void Extract_Duplicates_Collapse()
    {
        var links = HistoryLinks.Extract("https://example.com and again https://example.com");
        Assert.Single(links);
    }

    [Fact]
    public void Extract_MultiLine_FindsAcrossLines()
    {
        var links = HistoryLinks.Extract("line one https://a.test\nline two https://b.test");
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.True(l.IsUrl));
    }

    [Fact]
    public void Extract_Empty_ReturnsEmpty()
    {
        Assert.Empty(HistoryLinks.Extract(""));
        Assert.Empty(HistoryLinks.Extract(null));
    }
}
