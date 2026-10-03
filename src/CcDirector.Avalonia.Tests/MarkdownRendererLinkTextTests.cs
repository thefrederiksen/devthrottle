using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CcDirector.Avalonia.Helpers;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The chat (History) view shows a link EXACTLY as the agent wrote it. It used to show the
/// detected target instead, so "file:///D:/x.html" appeared as "D:\x.html". Owner ruling,
/// 3 October 2026: a URL is never changed.
/// </summary>
public class MarkdownRendererLinkTextTests
{
    private static (string shown, IReadOnlyList<LinkSpan> links) RenderParagraph(string markdown)
    {
        var ctx = new MarkdownRenderContext { RepoPath = @"D:\repo", PathExists = _ => false };
        var root = MarkdownRenderer.Render(markdown, ctx);
        var block = root.GetLogicalDescendants().OfType<LinkTextBlock>().First();
        string shown = string.Concat(block.Inlines!.OfType<Run>().Select(r => r.Text));
        return (shown, block.Links);
    }

    [AvaloniaFact]
    public void FileUrl_IsShownAsWritten_AndOpensTheLocalFile()
    {
        const string line = "open file:///D:/notes/x.html now";

        var (shown, links) = RenderParagraph(line);

        Assert.Equal(line, shown);
        var link = Assert.Single(links);
        Assert.Equal("file:///D:/notes/x.html", shown.Substring(link.Start, link.Length));
        Assert.Equal(@"D:\notes\x.html", link.Link);
    }

    [AvaloniaFact]
    public void PathWithLineNumber_IsShownAsWritten()
    {
        const string line = @"error in D:\repo\src\file.cs:42 here";

        var (shown, links) = RenderParagraph(line);

        Assert.Equal(line, shown);
        Assert.Equal(@"D:\repo\src\file.cs", Assert.Single(links).Link);
    }
}
