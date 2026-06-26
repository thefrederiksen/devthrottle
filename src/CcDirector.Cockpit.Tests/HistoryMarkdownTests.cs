using CcDirector.Cockpit.Services;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Tests for <see cref="HistoryMarkdown"/> - the #739 Markdown rendering. Confirms headings, lists,
/// bold, and fenced code render formatted; plain text stays clean; raw HTML is inert (DisableHtml);
/// and anchors are rewritten to open in a new tab (#740).
/// </summary>
public class HistoryMarkdownTests
{
    [Fact]
    public void ToHtml_Heading_RendersHeadingTag()
    {
        var html = HistoryMarkdown.ToHtml("# Title");
        Assert.Contains("<h1", html);
        Assert.Contains("Title", html);
    }

    [Fact]
    public void ToHtml_BulletList_RendersListItems()
    {
        var html = HistoryMarkdown.ToHtml("- one\n- two");
        Assert.Contains("<ul", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<li>two</li>", html);
    }

    [Fact]
    public void ToHtml_Bold_RendersStrong()
    {
        var html = HistoryMarkdown.ToHtml("this is **bold** text");
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void ToHtml_FencedCode_RendersPreCode()
    {
        var html = HistoryMarkdown.ToHtml("```\nvar x = 1;\n```");
        Assert.Contains("<pre>", html);
        Assert.Contains("<code", html);
        Assert.Contains("var x = 1;", html);
    }

    [Fact]
    public void ToHtml_PlainText_HasNoStrayFormatting()
    {
        var html = HistoryMarkdown.ToHtml("just a normal sentence.");
        Assert.Contains("just a normal sentence.", html);
        Assert.DoesNotContain("<h1", html);
        Assert.DoesNotContain("<strong>", html);
    }

    [Fact]
    public void ToHtml_RawHtml_IsInertNotExecuted()
    {
        // DisableHtml escapes the angle brackets so the tag is shown, never run.
        var html = HistoryMarkdown.ToHtml("danger <script>alert(1)</script> end");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void ToHtml_Url_AnchorOpensInNewTab()
    {
        var html = HistoryMarkdown.ToHtml("see https://example.com for more");
        Assert.Contains("<a", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.Contains("https://example.com", html);
    }

    [Fact]
    public void ToHtml_Empty_ReturnsEmpty()
    {
        Assert.Equal("", HistoryMarkdown.ToHtml(""));
        Assert.Equal("", HistoryMarkdown.ToHtml(null));
    }
}
