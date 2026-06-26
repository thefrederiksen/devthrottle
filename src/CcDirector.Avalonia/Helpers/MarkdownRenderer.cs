using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Terminal.Avalonia;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace CcDirector.Avalonia.Helpers;

/// <summary>
/// Optional context that turns file paths and URLs inside rendered Markdown into clickable links
/// (GitHub #735). When supplied, literal text is scanned with <see cref="LinkDetector"/> and each
/// match becomes a clickable inline that opens the shared <see cref="LinkContextMenuBuilder"/> menu -
/// the same actions the terminal offers. When null, text is rendered as plain runs (markdown only).
/// </summary>
public sealed class MarkdownRenderContext
{
    /// <summary>Repo root used to resolve relative paths, or null.</summary>
    public string? RepoPath { get; init; }

    /// <summary>Returns true when a resolved path exists; used by <see cref="LinkDetector"/> to
    /// validate relative paths and to extend absolute paths through spaces.</summary>
    public Func<string, bool>? PathExists { get; init; }

    /// <summary>Routes "View File" to the host's document viewer (resolved absolute path).</summary>
    public Action<string>? OnViewFile { get; init; }

    /// <summary>Routes a browser-launch failure message to the host.</summary>
    public Action<string>? OnBrowserError { get; init; }
}

/// <summary>
/// Renders a Markdown string into a native Avalonia control tree (GitHub #734). Unlike
/// <see cref="MarkdownHtmlRenderer"/> (which produces HTML for a WebView2), this builds plain
/// Avalonia controls so it is cheap enough to use once per History bubble and so individual text
/// runs can be made clickable (GitHub #735). It reuses the same Markdig parser the rest of the app
/// already depends on - no second Markdown library is introduced.
///
/// The supported subset matches what agent output actually uses: headings, paragraphs, bold /
/// italic / inline code, fenced and indented code blocks, ordered and unordered lists, block
/// quotes, and thematic breaks. Anything unrecognized falls back to its plain text.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly FontFamily Mono = new("Cascadia Mono,Cascadia Code,Consolas,Courier New,monospace");
    private static readonly IBrush BodyBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly IBrush HeadingBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly IBrush CodeBlockBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
    private static readonly IBrush CodeBlockBorder = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly IBrush InlineCodeBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly IBrush QuoteBar = new SolidColorBrush(Color.FromRgb(0x4E, 0x4E, 0x4E));
    private static readonly IBrush QuoteText = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xA1, 0xFF));

    private const double BaseFontSize = 13;

    /// <summary>
    /// Render <paramref name="markdown"/> into a single Avalonia control (a vertical stack of
    /// blocks). Never returns null; an empty or unparseable input becomes a plain text block.
    /// When <paramref name="linkContext"/> is supplied, paths and URLs become clickable.
    /// </summary>
    public static Control Render(string? markdown, MarkdownRenderContext? linkContext = null)
    {
        if (string.IsNullOrEmpty(markdown))
            return PlainParagraph(string.Empty);

        MarkdownDocument doc;
        try
        {
            doc = Markdig.Markdown.Parse(markdown, Pipeline);
        }
        catch
        {
            // Parsing should not fail, but never let a malformed body break the bubble.
            return PlainParagraph(markdown);
        }

        var root = NewStack();
        foreach (var block in doc)
        {
            var control = RenderBlock(block, BaseFontSize, linkContext);
            if (control != null)
                root.Children.Add(control);
        }

        return root.Children.Count == 0 ? PlainParagraph(markdown) : root;
    }

    private static StackPanel NewStack() => new() { Spacing = 6 };

    private static Control? RenderBlock(Block block, double baseSize, MarkdownRenderContext? ctx)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return RenderHeading(heading, ctx);

            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph, baseSize, ctx);

            case FencedCodeBlock fenced:
                return RenderCodeBlock(GetCodeText(fenced));

            case CodeBlock code: // indented code block
                return RenderCodeBlock(GetCodeText(code));

            case ListBlock list:
                return RenderList(list, baseSize, ctx);

            case QuoteBlock quote:
                return RenderQuote(quote, baseSize, ctx);

            case ThematicBreakBlock:
                return new Border
                {
                    Height = 1,
                    Background = QuoteBar,
                    Margin = new Thickness(0, 6, 0, 6),
                };

            case LeafBlock leaf:
                // Unknown leaf (e.g. an HTML block): render its raw text so nothing is dropped.
                return PlainParagraph(GetCodeText(leaf));

            default:
                return null;
        }
    }

    private static Control RenderHeading(HeadingBlock heading, MarkdownRenderContext? ctx)
    {
        double size = heading.Level switch
        {
            1 => BaseFontSize * 1.55,
            2 => BaseFontSize * 1.35,
            3 => BaseFontSize * 1.18,
            4 => BaseFontSize * 1.08,
            _ => BaseFontSize * 1.02,
        };

        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = FontWeight.Bold,
            Foreground = HeadingBrush,
            Margin = new Thickness(0, 2, 0, 0),
        };
        if (heading.Inline != null)
            AppendInlines(heading.Inline, tb.Inlines!, BaseFontSize, ctx);
        return tb;
    }

    private static Control RenderParagraph(ParagraphBlock paragraph, double baseSize, MarkdownRenderContext? ctx)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = baseSize,
            Foreground = BodyBrush,
        };
        if (paragraph.Inline != null)
            AppendInlines(paragraph.Inline, tb.Inlines!, baseSize, ctx);
        return tb;
    }

    private static Control RenderCodeBlock(string code)
    {
        return new Border
        {
            Background = CodeBlockBg,
            BorderBrush = CodeBlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = new SelectableTextBlock
            {
                Text = code,
                FontFamily = Mono,
                FontSize = BaseFontSize - 0.5,
                Foreground = BodyBrush,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static Control RenderList(ListBlock list, double baseSize, MarkdownRenderContext? ctx)
    {
        var stack = NewStack();
        stack.Spacing = 2;
        int index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
                continue;

            string bullet = list.IsOrdered ? index + "." : "-";
            index++;

            var bulletText = new TextBlock
            {
                Text = bullet,
                FontSize = baseSize,
                Foreground = BodyBrush,
                Margin = new Thickness(0, 0, 6, 0),
                MinWidth = list.IsOrdered ? 18 : 12,
                TextAlignment = TextAlignment.Right,
            };

            var content = NewStack();
            content.Spacing = 2;
            foreach (var sub in item)
            {
                var c = RenderBlock(sub, baseSize, ctx);
                if (c != null)
                    content.Children.Add(c);
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(bulletText, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(bulletText);
            row.Children.Add(content);

            var rowHost = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            rowHost.Children.Add(row);
            stack.Children.Add(rowHost);
        }

        return stack;
    }

    private static Control RenderQuote(QuoteBlock quote, double baseSize, MarkdownRenderContext? ctx)
    {
        var inner = NewStack();
        foreach (var sub in quote)
        {
            var c = RenderBlock(sub, baseSize, ctx);
            if (c != null)
            {
                if (c is TextBlock tb)
                    tb.Foreground = QuoteText;
                inner.Children.Add(c);
            }
        }

        return new Border
        {
            BorderBrush = QuoteBar,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(2, 2, 0, 2),
            Child = inner,
        };
    }

    private static TextBlock PlainParagraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = BaseFontSize,
        Foreground = BodyBrush,
    };

    // ---- inline rendering ----

    private static void AppendInlines(ContainerInline container, InlineCollection target, double baseSize,
        MarkdownRenderContext? ctx)
    {
        foreach (var inline in container)
            AppendInline(inline, target, baseSize, FontWeight.Normal, FontStyle.Normal, ctx);
    }

    private static void AppendInline(MdInline inline, InlineCollection target, double baseSize,
        FontWeight weight, FontStyle style, MarkdownRenderContext? ctx)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendText(literal.Content.ToString(), target, baseSize, weight, style, ctx);
                break;

            case EmphasisInline emphasis:
            {
                // DelimiterCount 2 (or **) = bold; 1 = italic. Handle nesting by inheriting.
                var w = emphasis.DelimiterCount >= 2 ? FontWeight.Bold : weight;
                var s = emphasis.DelimiterCount == 1 ? FontStyle.Italic : style;
                foreach (var child in emphasis)
                    AppendInline(child, target, baseSize, w, s, ctx);
                break;
            }

            case CodeInline code:
            {
                var run = new Run(code.Content)
                {
                    FontFamily = Mono,
                    FontSize = baseSize - 0.5,
                    Background = InlineCodeBg,
                    Foreground = BodyBrush,
                };
                target.Add(run);
                break;
            }

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case LinkInline link when !link.IsImage:
            {
                var label = LinkLabel(link);
                var url = link.Url ?? string.Empty;
                if (ctx != null && url.Length > 0)
                {
                    var type = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? LinkDetector.LinkType.Url
                        : LinkDetector.LinkType.Path;
                    target.Add(ClickableLink(label, url, type, baseSize, weight, style, ctx));
                }
                else
                {
                    target.Add(new Run(label) { Foreground = LinkBrush, FontWeight = weight, FontStyle = style });
                }
                break;
            }

            case AutolinkInline auto:
            {
                if (ctx != null)
                    target.Add(ClickableLink(auto.Url, auto.Url, LinkDetector.LinkType.Url, baseSize, weight, style, ctx));
                else
                    target.Add(new Run(auto.Url) { Foreground = LinkBrush });
                break;
            }

            case ContainerInline nested:
                foreach (var child in nested)
                    AppendInline(child, target, baseSize, weight, style, ctx);
                break;

            default:
                // Unknown inline: emit its plain text if it has any.
                var text = inline.ToString();
                if (!string.IsNullOrEmpty(text))
                    AppendText(text, target, baseSize, weight, style, ctx);
                break;
        }
    }

    /// <summary>
    /// Append a plain text run, OR - when a link context is present - scan the text for file paths
    /// and URLs (via <see cref="LinkDetector"/>) and split it into plain runs plus clickable links.
    /// </summary>
    private static void AppendText(string text, InlineCollection target, double baseSize,
        FontWeight weight, FontStyle style, MarkdownRenderContext? ctx)
    {
        if (ctx == null || string.IsNullOrEmpty(text))
        {
            target.Add(StyledRun(text, baseSize, weight, style));
            return;
        }

        List<LinkDetector.LinkMatch> matches;
        try
        {
            matches = LinkDetector.FindAllLinkMatches(text, ctx.RepoPath, ctx.PathExists);
        }
        catch
        {
            matches = new List<LinkDetector.LinkMatch>();
        }

        if (matches.Count == 0)
        {
            target.Add(StyledRun(text, baseSize, weight, style));
            return;
        }

        matches.Sort((a, b) => a.StartCol.CompareTo(b.StartCol));

        int pos = 0;
        foreach (var m in matches)
        {
            if (m.StartCol < pos || m.EndCol > text.Length || m.EndCol <= m.StartCol)
                continue; // overlap or out-of-range guard
            if (m.StartCol > pos)
                target.Add(StyledRun(text.Substring(pos, m.StartCol - pos), baseSize, weight, style));
            target.Add(ClickableLink(m.Text, m.Text, m.Type, baseSize, weight, style, ctx));
            pos = m.EndCol;
        }
        if (pos < text.Length)
            target.Add(StyledRun(text.Substring(pos), baseSize, weight, style));
    }

    /// <summary>
    /// Build a clickable link inline: underlined blue text with a hand cursor that, on left click,
    /// opens the shared terminal-equivalent context menu for the link.
    /// </summary>
    private static MdInlineHost ClickableLink(string display, string link, LinkDetector.LinkType type,
        double baseSize, FontWeight weight, FontStyle style, MarkdownRenderContext ctx)
    {
        var tb = new TextBlock
        {
            Text = display,
            Foreground = LinkBrush,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            FontWeight = weight,
            FontStyle = style,
            FontSize = baseSize,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Open(Control owner)
        {
            var menu = LinkContextMenuBuilder.Build(new LinkMenuContext
            {
                Link = link,
                Type = type,
                RepoPath = ctx.RepoPath,
                Owner = owner,
                OnViewFile = ctx.OnViewFile,
                OnBrowserError = ctx.OnBrowserError,
            });
            menu.Open(owner);
        }

        tb.PointerPressed += (_, e) =>
        {
            // Left click opens the menu (mirrors the terminal); also handle right click for parity.
            var props = e.GetCurrentPoint(tb).Properties;
            if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
                return;
            e.Handled = true;
            Open(tb);
        };

        return new MdInlineHost(tb);
    }

    private static Run StyledRun(string text, double size, FontWeight weight, FontStyle style)
        => new(text) { FontSize = size, FontWeight = weight, FontStyle = style, Foreground = BodyBrush };

    private static string LinkLabel(LinkInline link)
    {
        var sb = new StringBuilder();
        foreach (var child in link)
        {
            if (child is LiteralInline lit)
                sb.Append(lit.Content.ToString());
            else
                sb.Append(child.ToString());
        }
        var label = sb.ToString();
        return label.Length > 0 ? label : (link.Url ?? string.Empty);
    }

    private static string GetCodeText(LeafBlock leaf)
    {
        var sb = new StringBuilder();
        var lines = leaf.Lines.Lines;
        for (int i = 0; i < leaf.Lines.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(lines[i].Slice.ToString());
        }
        return sb.ToString();
    }
}

/// <summary>
/// An <see cref="InlineUIContainer"/> that hosts a clickable link control inside flowing text.
/// A thin named subclass keeps the call sites readable and the baseline alignment in one place.
/// </summary>
internal sealed class MdInlineHost : InlineUIContainer
{
    public MdInlineHost(Control child) : base(child)
    {
        BaselineAlignment = BaselineAlignment.TextBottom;
    }
}
