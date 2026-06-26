using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace CcDirector.Avalonia.Helpers;

/// <summary>
/// Renders a Markdown string into a native Avalonia control tree (GitHub #734). Unlike
/// <see cref="MarkdownHtmlRenderer"/> (which produces HTML for a WebView2), this builds plain
/// Avalonia controls so it is cheap enough to use once per History bubble and so individual text
/// runs can later be made clickable (GitHub #735). It reuses the same Markdig parser the rest of
/// the app already depends on - no second Markdown library is introduced.
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
    /// </summary>
    public static Control Render(string? markdown)
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
            var control = RenderBlock(block, BaseFontSize);
            if (control != null)
                root.Children.Add(control);
        }

        return root.Children.Count == 0 ? PlainParagraph(markdown) : root;
    }

    private static StackPanel NewStack() => new() { Spacing = 6 };

    private static Control? RenderBlock(Block block, double baseSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return RenderHeading(heading);

            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph, baseSize);

            case FencedCodeBlock fenced:
                return RenderCodeBlock(GetCodeText(fenced));

            case CodeBlock code: // indented code block
                return RenderCodeBlock(GetCodeText(code));

            case ListBlock list:
                return RenderList(list, baseSize);

            case QuoteBlock quote:
                return RenderQuote(quote, baseSize);

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

    private static Control RenderHeading(HeadingBlock heading)
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
            AppendInlines(heading.Inline, tb.Inlines!, BaseFontSize);
        return tb;
    }

    private static Control RenderParagraph(ParagraphBlock paragraph, double baseSize)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = baseSize,
            Foreground = BodyBrush,
        };
        if (paragraph.Inline != null)
            AppendInlines(paragraph.Inline, tb.Inlines!, baseSize);
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

    private static Control RenderList(ListBlock list, double baseSize)
    {
        var stack = NewStack();
        stack.Spacing = 2;
        int index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
                continue;

            string bullet = list.IsOrdered ? index + "." : "•"; // "•"
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
                var c = RenderBlock(sub, baseSize);
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

    private static Control RenderQuote(QuoteBlock quote, double baseSize)
    {
        var inner = NewStack();
        foreach (var sub in quote)
        {
            var c = RenderBlock(sub, baseSize);
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

    private static void AppendInlines(ContainerInline container, InlineCollection target, double baseSize)
    {
        foreach (var inline in container)
            AppendInline(inline, target, baseSize, FontWeight.Normal, FontStyle.Normal);
    }

    private static void AppendInline(MdInline inline, InlineCollection target, double baseSize,
        FontWeight weight, FontStyle style)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(StyledRun(literal.Content.ToString(), baseSize, weight, style));
                break;

            case EmphasisInline emphasis:
            {
                // DelimiterCount 2 (or **) = bold; 1 = italic. Handle nesting by inheriting.
                var w = emphasis.DelimiterCount >= 2 ? FontWeight.Bold : weight;
                var s = emphasis.DelimiterCount == 1 ? FontStyle.Italic : style;
                foreach (var child in emphasis)
                    AppendInline(child, target, baseSize, w, s);
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
                // Markdown hyperlink. Rendered as styled text here; click handling is added by #735.
                var label = LinkLabel(link);
                target.Add(new Run(label) { Foreground = LinkBrush, FontWeight = weight, FontStyle = style });
                break;
            }

            case AutolinkInline auto:
                target.Add(new Run(auto.Url) { Foreground = LinkBrush });
                break;

            case ContainerInline nested:
                foreach (var child in nested)
                    AppendInline(child, target, baseSize, weight, style);
                break;

            default:
                // Unknown inline: emit its plain text if it has any.
                var text = inline.ToString();
                if (!string.IsNullOrEmpty(text))
                    target.Add(StyledRun(text, baseSize, weight, style));
                break;
        }
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
