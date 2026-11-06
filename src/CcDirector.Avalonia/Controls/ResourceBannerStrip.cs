using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Machine;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The resource warning banners under the toolbar (the Resource Monitor mission, issue #2811, section 5e): at
/// most one per resource, each naming the biggest user, each with "Show resources" and "Dismiss".
///
/// IT DRAWS THE LIST IT IS HANDED. Which banners are up, and what they say, is decided in Core by
/// <see cref="ResourceBannerBoard"/> and <see cref="ResourceBannerFold"/>; this control only lays them out and
/// reports the two clicks. Collapsed entirely when there is nothing to show, so it costs no toolbar height.
/// </summary>
public sealed class ResourceBannerStrip : StackPanel
{
    public ResourceBannerStrip()
    {
        Orientation = Orientation.Vertical;
        Spacing = 0;
        IsVisible = false;
    }

    /// <summary>Raised when "Show resources" is clicked on a banner.</summary>
    public event EventHandler<ResourceKind>? ShowResourcesRequested;

    /// <summary>Raised when "Dismiss" is clicked on a banner.</summary>
    public event EventHandler<ResourceKind>? DismissRequested;

    /// <summary>The banners last rendered, for tests.</summary>
    public IReadOnlyList<ResourceBannerView> Banners { get; private set; } = Array.Empty<ResourceBannerView>();

    /// <summary>Draws the banners. Call on the user-interface thread.</summary>
    public void Render(IReadOnlyList<ResourceBannerView> banners)
    {
        ArgumentNullException.ThrowIfNull(banners);
        Banners = banners;
        Children.Clear();
        foreach (var banner in banners) Children.Add(Row(banner));
        IsVisible = banners.Count > 0;
        FileLog.Write($"[ResourceBannerStrip] Render: {banners.Count} banner(s)");
    }

    private Control Row(ResourceBannerView banner)
    {
        var red = banner.Level == ResourceLevel.Critical;
        var text = Brush(red ? "#FFC4C4" : "#E8DCC8");

        var words = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        words.Inlines!.Add(new Run(banner.Headline) { FontWeight = FontWeight.Bold });
        words.Inlines.Add(new Run(" " + banner.Body));

        var show = Link("Show resources", text);
        show.Click += (_, _) =>
        {
            FileLog.Write($"[ResourceBannerStrip] Show resources clicked: {banner.Kind}");
            ShowResourcesRequested?.Invoke(this, banner.Kind);
        };

        var dismiss = Link("Dismiss", text);
        dismiss.Click += (_, _) =>
        {
            FileLog.Write($"[ResourceBannerStrip] Dismiss clicked: {banner.Kind}");
            DismissRequested?.Invoke(this, banner.Kind);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        actions.Children.Add(show);
        actions.Children.Add(new TextBlock { Text = "-", Foreground = text, VerticalAlignment = VerticalAlignment.Center });
        actions.Children.Add(dismiss);
        DockPanel.SetDock(actions, Dock.Right);

        var layout = new DockPanel();
        layout.Children.Add(actions);
        layout.Children.Add(words);

        var row = new Border
        {
            Background = Brush(red ? "#3A1010" : "#2B2316"),
            BorderBrush = Brush(red ? "#F44336" : "#F59E0B"),
            BorderThickness = new Thickness(4, 0, 0, 1),
            Padding = new Thickness(12, 6),
            Child = layout,
        };
        AutomationProperties.SetName(row, $"{banner.Headline} {banner.Body}");
        return row;
    }

    private static Button Link(string label, IBrush foreground) => new()
    {
        Content = new TextBlock { Text = label, TextDecorations = TextDecorations.Underline },
        Foreground = foreground,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(2, 0),
        FontSize = 12,
        Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
