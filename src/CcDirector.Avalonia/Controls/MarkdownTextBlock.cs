using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CcDirector.Avalonia.Helpers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// A lightweight control that renders a Markdown string as a native Avalonia control tree
/// (GitHub #734). Bind <see cref="Markdown"/> in a data template and it rebuilds its content
/// whenever the text changes. Used by the History bubbles so agent replies show formatted
/// headings, lists, bold text, and code blocks instead of raw markup.
/// </summary>
public sealed class MarkdownTextBlock : ContentControl
{
    private static readonly IBrush FallbackBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    /// <summary>When true, render the text verbatim (selectable, wrapped) instead of as Markdown.
    /// Used for raw terminal scrollback (e.g. Gemini) where markdown parsing would mangle it.</summary>
    public static readonly StyledProperty<bool> PlainProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, bool>(nameof(Plain));

    /// <summary>Optional link context that makes file paths and URLs clickable (GitHub #735).</summary>
    public static readonly StyledProperty<MarkdownRenderContext?> LinkContextProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, MarkdownRenderContext?>(nameof(LinkContext));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool Plain
    {
        get => GetValue(PlainProperty);
        set => SetValue(PlainProperty, value);
    }

    public MarkdownRenderContext? LinkContext
    {
        get => GetValue(LinkContextProperty);
        set => SetValue(LinkContextProperty, value);
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.Rebuild());
        PlainProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.Rebuild());
        LinkContextProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.Rebuild());
    }

    private void Rebuild()
    {
        try
        {
            Content = Plain
                ? new SelectableTextBlock
                {
                    Text = Markdown,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = FallbackBrush,
                }
                : MarkdownRenderer.Render(Markdown, LinkContext);
        }
        catch (Exception ex)
        {
            // Never let a rendering failure blank the bubble - show the raw text instead.
            FileLog.Write($"[MarkdownTextBlock] Rebuild FAILED: {ex.Message}");
            Content = new TextBlock
            {
                Text = Markdown,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = FallbackBrush,
            };
        }
    }
}
