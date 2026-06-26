using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
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
/// match is drawn as styled (underlined) text whose clicks open the shared
/// <see cref="LinkContextMenuBuilder"/> menu - the same actions the terminal offers. When null, text
/// is rendered as plain runs (markdown only).
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

/// <summary>One clickable link inside a rendered text block, identified by its character range in
/// the block's flattened text so a click can be resolved by hit-testing (GitHub #735).</summary>
internal sealed record LinkSpan(int Start, int Length, string Link, LinkDetector.LinkType Type);

/// <summary>
/// Renders a Markdown string into a native Avalonia control tree (GitHub #734). Unlike
/// <see cref="MarkdownHtmlRenderer"/> (which produces HTML for a WebView2), this builds plain
/// Avalonia controls so it is cheap enough to use once per History bubble. Links (GitHub #735) are
/// drawn as ordinary styled text - never embedded controls - so the text measures its height
/// correctly; clicks are resolved by hit-testing the pointer against the text layout (the same
/// technique the terminal uses), which keeps the scroll container's content height accurate.
/// It reuses the same Markdig parser the rest of the app already depends on - no second Markdown
/// library is introduced.
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

        var tb = new LinkTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = FontWeight.Bold,
            Foreground = HeadingBrush,
            Margin = new Thickness(0, 2, 0, 0),
        };
        if (heading.Inline != null)
            BuildInlines(heading.Inline, tb, BaseFontSize, ctx);
        return tb;
    }

    private static Control RenderParagraph(ParagraphBlock paragraph, double baseSize, MarkdownRenderContext? ctx)
    {
        var tb = new LinkTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = baseSize,
            Foreground = BodyBrush,
        };
        if (paragraph.Inline != null)
            BuildInlines(paragraph.Inline, tb, baseSize, ctx);
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

    /// <summary>Build the inline runs for a block into <paramref name="tb"/>, collecting clickable
    /// link ranges, then wire the block for click/hover hit-testing if it has any links.</summary>
    private static void BuildInlines(ContainerInline container, LinkTextBlock tb, double baseSize,
        MarkdownRenderContext? ctx)
    {
        var build = new InlineBuild(tb.Inlines!, baseSize, ctx);
        foreach (var inline in container)
            AppendInline(inline, FontWeight.Normal, FontStyle.Normal, build);
        if (ctx != null && build.Links.Count > 0)
            tb.SetLinks(build.Links, ctx);
    }

    private static void AppendInline(MdInline inline, FontWeight weight, FontStyle style, InlineBuild build)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendText(literal.Content.ToString(), weight, style, build);
                break;

            case EmphasisInline emphasis:
            {
                // DelimiterCount 2 (or **) = bold; 1 = italic. Handle nesting by inheriting.
                var w = emphasis.DelimiterCount >= 2 ? FontWeight.Bold : weight;
                var s = emphasis.DelimiterCount == 1 ? FontStyle.Italic : style;
                foreach (var child in emphasis)
                    AppendInline(child, w, s, build);
                break;
            }

            case CodeInline code:
                AddRun(build, new Run(code.Content)
                {
                    FontFamily = Mono,
                    FontSize = build.BaseSize - 0.5,
                    Background = InlineCodeBg,
                    Foreground = BodyBrush,
                }, code.Content.Length);
                break;

            case LineBreakInline:
                build.Target.Add(new LineBreak());
                build.CharPos += 1; // a hard line break counts as one character in the text layout
                break;

            case LinkInline link when !link.IsImage:
            {
                var label = LinkLabel(link);
                var url = link.Url ?? string.Empty;
                if (build.Ctx != null && url.Length > 0)
                {
                    var type = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? LinkDetector.LinkType.Url
                        : LinkDetector.LinkType.Path;
                    AppendLink(label, url, type, weight, style, build);
                }
                else
                {
                    AddRun(build, new Run(label) { Foreground = LinkBrush, FontWeight = weight, FontStyle = style }, label.Length);
                }
                break;
            }

            case AutolinkInline auto:
                if (build.Ctx != null)
                    AppendLink(auto.Url, auto.Url, LinkDetector.LinkType.Url, weight, style, build);
                else
                    AddRun(build, new Run(auto.Url) { Foreground = LinkBrush }, auto.Url.Length);
                break;

            case ContainerInline nested:
                foreach (var child in nested)
                    AppendInline(child, weight, style, build);
                break;

            default:
                // Unknown inline: emit its plain text if it has any.
                var text = inline.ToString();
                if (!string.IsNullOrEmpty(text))
                    AppendText(text, weight, style, build);
                break;
        }
    }

    /// <summary>
    /// Append plain text. When a link context is present, scan it for file paths and URLs (via
    /// <see cref="LinkDetector"/>) and split it into plain runs plus styled clickable-link runs.
    /// </summary>
    private static void AppendText(string text, FontWeight weight, FontStyle style, InlineBuild build)
    {
        if (build.Ctx == null || string.IsNullOrEmpty(text))
        {
            AddRun(build, StyledRun(text, build.BaseSize, weight, style), text.Length);
            return;
        }

        List<LinkDetector.LinkMatch> matches;
        try
        {
            matches = LinkDetector.FindAllLinkMatches(text, build.Ctx.RepoPath, build.Ctx.PathExists);
        }
        catch
        {
            matches = new List<LinkDetector.LinkMatch>();
        }

        if (matches.Count == 0)
        {
            AddRun(build, StyledRun(text, build.BaseSize, weight, style), text.Length);
            return;
        }

        matches.Sort((a, b) => a.StartCol.CompareTo(b.StartCol));

        int pos = 0;
        foreach (var m in matches)
        {
            if (m.StartCol < pos || m.EndCol > text.Length || m.EndCol <= m.StartCol)
                continue; // overlap or out-of-range guard
            if (m.StartCol > pos)
                AddRun(build, StyledRun(text.Substring(pos, m.StartCol - pos), build.BaseSize, weight, style), m.StartCol - pos);
            AppendLink(m.Text, m.Text, m.Type, weight, style, build);
            pos = m.EndCol;
        }
        if (pos < text.Length)
            AddRun(build, StyledRun(text.Substring(pos), build.BaseSize, weight, style), text.Length - pos);
    }

    /// <summary>Add a styled (underlined, blue) link run and record its character range so a click
    /// over it can be resolved by hit-testing.</summary>
    private static void AppendLink(string display, string link, LinkDetector.LinkType type,
        FontWeight weight, FontStyle style, InlineBuild build)
    {
        build.Links.Add(new LinkSpan(build.CharPos, display.Length, link, type));
        AddRun(build, new Run(display)
        {
            Foreground = LinkBrush,
            FontWeight = weight,
            FontStyle = style,
            FontSize = build.BaseSize,
            TextDecorations = TextDecorations.Underline,
        }, display.Length);
    }

    /// <summary>Add a run to the block and advance the character cursor that link ranges index into.</summary>
    private static void AddRun(InlineBuild build, Run run, int length)
    {
        build.Target.Add(run);
        build.CharPos += length;
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

    /// <summary>Mutable state threaded through inline building: the target inline collection, the
    /// running character offset (for link ranges), and the collected link spans.</summary>
    private sealed class InlineBuild
    {
        public InlineBuild(InlineCollection target, double baseSize, MarkdownRenderContext? ctx)
        {
            Target = target;
            BaseSize = baseSize;
            Ctx = ctx;
        }

        public InlineCollection Target { get; }
        public double BaseSize { get; }
        public MarkdownRenderContext? Ctx { get; }
        public int CharPos { get; set; }
        public List<LinkSpan> Links { get; } = new();
    }
}

/// <summary>
/// A <see cref="TextBlock"/> whose links are ordinary styled text (so the block measures its height
/// correctly) and become clickable by hit-testing the pointer against the text layout (GitHub #735).
/// This is the measurement-safe alternative to embedding a control per link, and mirrors how the
/// terminal resolves link clicks by position.
/// </summary>
internal sealed class LinkTextBlock : TextBlock
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private List<LinkSpan>? _links;
    private MarkdownRenderContext? _ctx;

    public void SetLinks(List<LinkSpan> links, MarkdownRenderContext ctx)
    {
        _links = links;
        _ctx = ctx;
    }

    private LinkSpan? HitTest(Point point)
    {
        if (_links is null)
            return null;
        var layout = TextLayout;
        if (layout is null)
            return null;

        var hit = layout.HitTestPoint(point);
        if (!hit.IsInside)
            return null;

        var pos = hit.TextPosition;
        foreach (var span in _links)
        {
            if (pos >= span.Start && pos < span.Start + span.Length)
                return span;
        }
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // Hand cursor over a link, default elsewhere - the same affordance the terminal gives.
        Cursor = HitTest(e.GetPosition(this)) is not null ? HandCursor : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_ctx is null)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
            return;

        var span = HitTest(e.GetPosition(this));
        if (span is null)
            return;

        e.Handled = true;
        var menu = LinkContextMenuBuilder.Build(new LinkMenuContext
        {
            Link = span.Link,
            Type = span.Type,
            RepoPath = _ctx.RepoPath,
            Owner = this,
            OnViewFile = _ctx.OnViewFile,
            OnBrowserError = _ctx.OnBrowserError,
        });
        menu.Open(this);
    }
}
