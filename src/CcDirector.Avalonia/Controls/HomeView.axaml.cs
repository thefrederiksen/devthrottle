using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Home;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>One row of the home page's Recent card: a repo you can re-open in one click.</summary>
public sealed record RecentRepoItem(string Name, string RepoPath, string ColorHex, string When);

/// <summary>
/// Full-screen empty-state home page, shown by <see cref="MainWindow"/> when this
/// Director has zero sessions. It is a passive view: MainWindow owns the services and
/// pushes the gateway visual, readiness rows, tagline and version in; the view raises
/// events back for the few actions it offers (new session, gateway troubleshooter,
/// open Tools, open Settings).
/// </summary>
public partial class HomeView : UserControl
{
    public event EventHandler? NewSessionRequested;
    public event EventHandler? GatewayClicked;
    public event EventHandler? OpenToolsRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenCockpitRequested;
    public event EventHandler<string>? RecentRepoSelected;

    public HomeView()
    {
        InitializeComponent();
    }

    private void HomeOpenCockpit_Click(object? sender, RoutedEventArgs e)
        => OpenCockpitRequested?.Invoke(this, EventArgs.Empty);

    private void HomeOpenTools_Click(object? sender, RoutedEventArgs e)
        => OpenToolsRequested?.Invoke(this, EventArgs.Empty);

    private void HomeOpenSettings_Click(object? sender, RoutedEventArgs e)
        => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void HomeNewSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[HomeView] New Session requested");
        NewSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HomeGatewayCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        FileLog.Write("[HomeView] Gateway card clicked");
        GatewayClicked?.Invoke(this, EventArgs.Empty);
    }

    private void HomeGatewayFix_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[HomeView] Gateway Fix-it clicked");
        GatewayClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Switch the tagline between the healthy and the setup wording.</summary>
    public void SetTagline(string text) => HomeTagline.Text = text;

    /// <summary>Paint the version footer (e.g. "v0.6.23 (e6f6af8)").</summary>
    public void SetVersion(string display) => HomeVersionText.Text = display;

    /// <summary>
    /// Paint the gateway card from values MainWindow already computes for the sidebar
    /// indicator, so both surfaces always agree. <paramref name="showFix"/> reveals the
    /// "Fix it" affordance for the error states only.
    /// </summary>
    public void SetGateway(string accentHex, string backgroundHex, string borderHex,
                           string label, string sub, bool showFix)
    {
        HomeGatewayRing.BorderBrush = Brush.Parse(accentHex);
        HomeGatewayCard.Background = Brush.Parse(backgroundHex);
        HomeGatewayCard.BorderBrush = Brush.Parse(borderHex);
        HomeGatewayLabel.Text = label;
        HomeGatewayLabel.Foreground = Brush.Parse(accentHex);
        HomeGatewaySub.Text = sub;
        HomeGatewayFix.IsVisible = showFix;
    }

    /// <summary>
    /// Render the readiness rows. <paramref name="healthy"/> drives the card header
    /// (ALL GREEN vs N of M ready) and its colour.
    /// </summary>
    public void SetStatus(HomeStatus status, bool healthy)
    {
        HomeStatusHeader.Text = healthy ? "SYSTEM STATUS" : "READINESS";
        HomeStatusSummary.Text = healthy ? "ALL GREEN" : $"{status.ReadyCount} of {status.TotalCount} ready";
        HomeStatusSummary.Foreground = Brush.Parse(healthy ? "#4CAF50" : "#F0B848");

        HomeStatusRows.Children.Clear();
        var first = true;
        foreach (var check in status.Checks)
        {
            HomeStatusRows.Children.Add(BuildRow(check, first));
            first = false;
        }
    }

    /// <summary>Show a single "Checking..." placeholder row while the async probe runs.</summary>
    public void SetBusy()
    {
        HomeStatusHeader.Text = "SYSTEM STATUS";
        HomeStatusSummary.Text = "Checking...";
        HomeStatusSummary.Foreground = Brush.Parse("#888888");
        HomeStatusRows.Children.Clear();
    }

    /// <summary>
    /// Render the Recent card. Each row re-opens that repo in one click. Empty history
    /// shows a quiet placeholder rather than an empty card.
    /// </summary>
    public void SetRecent(IReadOnlyList<RecentRepoItem> recent)
    {
        HomeRecentRows.Children.Clear();

        if (recent.Count == 0)
        {
            HomeRecentRows.Children.Add(new TextBlock
            {
                Text = "No recent repositories yet",
                Foreground = Brush.Parse("#666666"),
                FontSize = 12,
                Margin = new global::Avalonia.Thickness(0, 7, 0, 0),
            });
            return;
        }

        var first = true;
        foreach (var item in recent)
        {
            HomeRecentRows.Children.Add(BuildRecentRow(item, first));
            first = false;
        }
    }

    private Control BuildRecentRow(RecentRepoItem item, bool first)
    {
        var swatch = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new global::Avalonia.CornerRadius(2),
            Background = Brush.Parse(item.ColorHex),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(0, 0, 9, 0),
        };
        DockPanel.SetDock(swatch, Dock.Left);

        var when = new TextBlock
        {
            Text = item.When,
            Foreground = Brush.Parse("#555555"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(when, Dock.Right);

        var name = new TextBlock
        {
            Text = item.Name,
            Foreground = Brush.Parse("#CFCFCF"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var row = new DockPanel();
        row.Children.Add(swatch);
        row.Children.Add(when);
        row.Children.Add(name);

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new global::Avalonia.Thickness(0),
            Padding = new global::Avalonia.Thickness(0, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = row,
            BorderBrush = Brush.Parse("#343434"),
        };
        // Hairline above every row except the first.
        if (!first)
            button.BorderThickness = new global::Avalonia.Thickness(0, 1, 0, 0);
        ToolTip.SetTip(button, $"Start a Claude session in {item.RepoPath}");
        button.Click += (_, _) =>
        {
            FileLog.Write($"[HomeView] Recent repo selected: {item.RepoPath}");
            RecentRepoSelected?.Invoke(this, item.RepoPath);
        };
        return button;
    }

    private Control BuildRow(HomeCheck check, bool first)
    {
        var (glyph, color) = check.Level switch
        {
            HomeCheckLevel.Ok => ("OK", "#4CAF50"),
            HomeCheckLevel.Warn => ("!", "#F0B848"),
            _ => ("X", "#E05656"),
        };

        var dock = new DockPanel { Margin = new global::Avalonia.Thickness(0, 8, 0, 8) };

        if (check.Action != HomeCheckAction.None)
        {
            var fix = new Button
            {
                Content = "Fix it",
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent,
                BorderBrush = Brush.Parse("#2563EB"),
                BorderThickness = new global::Avalonia.Thickness(1),
                Foreground = Brush.Parse("#3B82F6"),
                CornerRadius = new global::Avalonia.CornerRadius(5),
                Padding = new global::Avalonia.Thickness(12, 4),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var action = check.Action;
            fix.Click += (_, _) => RaiseAction(action);
            DockPanel.SetDock(fix, Dock.Right);
            dock.Children.Add(fix);
        }

        var icon = new TextBlock
        {
            Text = glyph,
            Foreground = Brush.Parse(color),
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        var title = new TextBlock
        {
            Text = check.Title,
            Foreground = Brush.Parse("#DDDDDD"),
            FontSize = 13,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(title, Dock.Left);
        dock.Children.Add(title);

        var detail = new TextBlock
        {
            Text = check.Detail,
            Foreground = Brush.Parse("#888888"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dock.Children.Add(detail);

        // Hairline separator above every row except the first.
        return new Border
        {
            BorderBrush = Brush.Parse("#343434"),
            BorderThickness = new global::Avalonia.Thickness(0, first ? 0 : 1, 0, 0),
            Child = dock,
        };
    }

    private void RaiseAction(HomeCheckAction action)
    {
        FileLog.Write($"[HomeView] Fix-it action: {action}");
        switch (action)
        {
            case HomeCheckAction.OpenTools:
                OpenToolsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case HomeCheckAction.OpenSettings:
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
