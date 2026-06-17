using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace CcDirector.TrayUi;

/// <summary>
/// A OneDrive-style tray flyout: a borderless, shadowed panel that slides up at the bottom-right of
/// the screen (above the taskbar) when the user LEFT-CLICKS the tray icon. It replaces the legacy
/// right-click context menu with live status + real action buttons + a toggle. Auto-closes when it
/// loses focus (click away) or on Escape. Built entirely in code so it drops into each app's existing
/// FluentTheme with no AXAML / resource wiring. Shared by the Launcher and Gateway trays so they look
/// and behave identically.
/// </summary>
public sealed class TrayFlyout : Window
{
    /// <summary>
    /// Close the flyout when it loses focus (click-away), like OneDrive. Default true; set false only
    /// for previews/tests that need the panel to stay on screen without holding foreground focus.
    /// </summary>
    public bool AutoCloseOnDeactivate { get; set; } = true;

    // Palette (dark, matches the apps' existing dark surfaces).
    private static readonly Color Surface = Color.Parse("#1F2024");
    private static readonly Color Hairline = Color.Parse("#2E3138");
    private static readonly Color TextStrong = Color.Parse("#F0F1F3");
    private static readonly Color TextMid = Color.Parse("#CBD1DA");
    private static readonly Color TextDim = Color.Parse("#8B939E");
    private static readonly Color BtnBg = Color.Parse("#2A2C31");
    private static readonly Color BtnHover = Color.Parse("#34373D");
    private static readonly Color BtnPressed = Color.Parse("#3C4046");

    public TrayFlyout(TrayFlyoutModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        SizeToContent = SizeToContent.Height;
        Width = 336;
        RequestedThemeVariant = ThemeVariant.Dark;
        Opacity = 0; // revealed in Opened, once positioned, to avoid a position flash

        AddStyles(model.Accent);
        Content = BuildRoot(model);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => { if (AutoCloseOnDeactivate) Close(); };   // click away => close, like OneDrive
        Opened += (_, _) => { PositionBottomRight(); Opacity = 1; };
    }

    // ---- layout -----------------------------------------------------------

    private Control BuildRoot(TrayFlyoutModel m)
    {
        var stack = new StackPanel { Spacing = 0 };

        // Header: icon + app name + status dot
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (m.Icon is not null)
        {
            var img = new Image { Source = m.Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(img, 0);
            header.Children.Add(img);
        }
        var name = new TextBlock { Text = m.AppName, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush(TextStrong), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var dot = new Ellipse { Width = 9, Height = 9, Fill = Brush(StatusColor(m.Status)), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dot, 2);
        header.Children.Add(dot);
        stack.Children.Add(header);

        // Status title + optional detail
        var statusBlock = new StackPanel { Spacing = 1, Margin = new Thickness(0, 12, 0, 0) };
        statusBlock.Children.Add(new TextBlock { Text = m.StatusTitle, FontSize = 12.5, Foreground = Brush(TextMid), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(m.StatusDetail))
            statusBlock.Children.Add(new TextBlock { Text = m.StatusDetail, FontSize = 11, Foreground = Brush(TextDim), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(statusBlock);

        // Status rows
        if (m.Rows.Count > 0)
        {
            var rows = new StackPanel { Spacing = 5, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var r in m.Rows)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
                var l = new TextBlock { Text = r.Label, FontSize = 11.5, Foreground = Brush(TextDim) };
                var v = new TextBlock { Text = r.Value, FontSize = 11.5, Foreground = Brush(TextMid), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
                g.Children.Add(l); g.Children.Add(v);
                rows.Children.Add(g);
            }
            stack.Children.Add(rows);
        }

        // Actions
        if (m.Actions.Count > 0)
        {
            stack.Children.Add(Separator());
            var actions = new StackPanel { Spacing = 7 };
            foreach (var a in m.Actions)
                actions.Children.Add(ActionButton(a, m.Accent));
            stack.Children.Add(actions);
        }

        // Toggle
        if (m.Toggle is { } t)
        {
            stack.Children.Add(Separator());
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock { Text = t.Label, FontSize = 12.5, Foreground = Brush(TextMid), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);
            var sw = new ToggleSwitch { IsChecked = t.IsOn, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            sw.IsCheckedChanged += (_, _) => t.OnChanged(sw.IsChecked == true);
            Grid.SetColumn(sw, 1);
            g.Children.Add(label); g.Children.Add(sw);
            stack.Children.Add(g);
        }

        // Quit (quiet footer)
        if (m.OnQuit is { } quit)
        {
            stack.Children.Add(Separator());
            var q = new Button { Content = "Quit", HorizontalAlignment = HorizontalAlignment.Right };
            q.Classes.Add("flyoutQuit");
            q.Click += (_, _) => { Close(); quit(); };
            stack.Children.Add(q);
        }

        return new Border
        {
            Background = Brush(Surface),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush(Hairline),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(14), // room for the shadow inside the transparent window
            BoxShadow = BoxShadows.Parse("0 10 30 0 #90000000"),
            Child = stack,
        };
    }

    private Button ActionButton(FlyoutAction a, Color accent)
    {
        var b = new Button { Content = a.Text };
        b.Classes.Add("flyoutBtn");
        if (a.Primary)
        {
            b.Classes.Add("primary");
            b.Background = Brush(accent);
            b.Foreground = Brushes.White;
        }
        b.Click += (_, _) => { Close(); a.OnClick(); };
        return b;
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Background = Brush(Hairline),
        Margin = new Thickness(0, 12, 0, 12),
    };

    // ---- styling ----------------------------------------------------------

    private void AddStyles(Color accent)
    {
        Styles.Add(BtnStyle(null, BtnBg, stretch: true));
        Styles.Add(BtnStyle(":pointerover", BtnHover, stretch: true));
        Styles.Add(BtnStyle(":pressed", BtnPressed, stretch: true));

        // Primary: keep its inline accent background; dim slightly on hover/press.
        Styles.Add(OpacityStyle("primary", ":pointerover", 0.90));
        Styles.Add(OpacityStyle("primary", ":pressed", 0.82));

        // Quit: borderless text button, reddens on hover.
        var quit = new Style(x => x.OfType<Button>().Class("flyoutQuit"));
        quit.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        quit.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(TextDim)));
        quit.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        quit.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 11.5));
        quit.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 5)));
        Styles.Add(quit);
        var quitHover = new Style(x => x.OfType<Button>().Class("flyoutQuit").Class(":pointerover"));
        quitHover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        quitHover.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(Color.Parse("#E06C75"))));
        Styles.Add(quitHover);
    }

    private static Style BtnStyle(string? pseudo, Color bg, bool stretch)
    {
        var style = pseudo is null
            ? new Style(x => x.OfType<Button>().Class("flyoutBtn"))
            : new Style(x => x.OfType<Button>().Class("flyoutBtn").Class(pseudo));
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brush(bg)));
        style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(TextStrong)));
        style.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7)));
        style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 9)));
        style.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 12.5));
        if (stretch)
        {
            style.Setters.Add(new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        }
        return style;
    }

    private static Style OpacityStyle(string cls, string pseudo, double opacity)
    {
        var style = new Style(x => x.OfType<Button>().Class(cls).Class(pseudo));
        style.Setters.Add(new Setter(Visual.OpacityProperty, opacity));
        return style;
    }

    // ---- helpers ----------------------------------------------------------

    private static SolidColorBrush Brush(Color c) => new(c);

    private static Color StatusColor(StatusLevel s) => s switch
    {
        StatusLevel.Ok => Color.Parse("#3FB950"),
        StatusLevel.Warn => Color.Parse("#D29922"),
        StatusLevel.Error => Color.Parse("#F85149"),
        _ => Color.Parse("#3FB950"),
    };

    private void PositionBottomRight()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        var wa = screen.WorkingArea;            // physical px, excludes the taskbar
        var scale = screen.Scaling;
        var wPx = (int)Math.Ceiling(ClientSize.Width * scale);
        var hPx = (int)Math.Ceiling(ClientSize.Height * scale);
        var margin = (int)Math.Round(6 * scale);
        Position = new PixelPoint(
            wa.X + wa.Width - wPx - margin,
            wa.Y + wa.Height - hPx - margin);
    }
}
