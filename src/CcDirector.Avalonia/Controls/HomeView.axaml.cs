using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Home;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Status screen shown in the main content area when no session is selected (or on demand
/// via View &gt; Status). The window chrome stays visible around it; the gateway indicator
/// lives at the bottom of the session rail, not here. The screen is quiet when everything
/// is healthy ("All systems go") and lists only the failing checks when something needs
/// attention. MainWindow owns the services and pushes state in; the view raises events back
/// for the few actions it offers.
/// </summary>
public partial class HomeView : UserControl
{
    public event EventHandler? NewSessionRequested;
    public event EventHandler? OpenToolsRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? GatewayClicked;
    public event EventHandler? RepairToolsRequested;

    public HomeView()
    {
        InitializeComponent();
    }

    private void HomeNewSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[HomeView] New Session requested");
        NewSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Paint the version footer (e.g. "v0.6.23 (e6f6af8)").</summary>
    public void SetVersion(string display) => HomeVersionText.Text = display;

    /// <summary>Show the "Checking..." placeholder while the async readiness probe runs.</summary>
    public void SetBusy()
    {
        HomeBusyText.IsVisible = true;
        HomeAllClear.IsVisible = false;
        HomeProblems.IsVisible = false;
        HomeStatusRows.Children.Clear();
    }

    /// <summary>
    /// Render the status. When <paramref name="healthy"/> the screen is quiet (all-clear line
    /// + <paramref name="allClearSummary"/>); otherwise it lists only the failing checks, with
    /// a gateway row first when <paramref name="gatewayError"/> is set.
    /// </summary>
    public void SetStatus(HomeStatus status, bool healthy, bool gatewayError, string allClearSummary)
    {
        HomeBusyText.IsVisible = false;
        HomeStatusRows.Children.Clear();

        if (healthy)
        {
            HomeAllClear.IsVisible = true;
            HomeProblems.IsVisible = false;
            HomeAllClearSub.Text = allClearSummary;
            return;
        }

        HomeAllClear.IsVisible = false;
        HomeProblems.IsVisible = true;

        // Gateway trouble is surfaced here too (its glanceable light is in the rail), so the
        // status screen is a single place to see and fix everything that is wrong.
        if (gatewayError)
            HomeStatusRows.Children.Add(BuildProblemRow(
                "Gateway not connected",
                "The Gateway is unreachable or unverified.",
                "Reconnect",
                () => GatewayClicked?.Invoke(this, EventArgs.Empty)));

        foreach (var check in status.Checks)
        {
            if (check.Level == HomeCheckLevel.Ok) continue;
            Action? fix = check.Action switch
            {
                HomeCheckAction.OpenTools => () => OpenToolsRequested?.Invoke(this, EventArgs.Empty),
                HomeCheckAction.OpenSettings => () => OpenSettingsRequested?.Invoke(this, EventArgs.Empty),
                HomeCheckAction.RepairTools => () => RepairToolsRequested?.Invoke(this, EventArgs.Empty),
                _ => null,
            };
            HomeStatusRows.Children.Add(BuildProblemRow(
                check.Title, check.Detail, fix is null ? null : "Fix", fix,
                warn: check.Level == HomeCheckLevel.Warn));
        }
    }

    /// <summary>A single failing-check card: icon + title + detail, with an optional fix button.</summary>
    private Control BuildProblemRow(string title, string detail, string? fixLabel, Action? onFix, bool warn = false)
    {
        var accent = warn ? "#F0B848" : "#E05656";

        var dock = new DockPanel();

        if (fixLabel is not null && onFix is not null)
        {
            var fix = new Button
            {
                Content = fixLabel,
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
            fix.Click += (_, _) => onFix();
            DockPanel.SetDock(fix, Dock.Right);
            dock.Children.Add(fix);
        }

        var icon = new TextBlock
        {
            Text = warn ? "!" : "X",
            Foreground = Brush.Parse(accent),
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush.Parse("#EDEDED"),
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = Brush.Parse("#999999"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        dock.Children.Add(text);

        return new Border
        {
            Background = Brush.Parse(warn ? "#241f17" : "#241a1a"),
            BorderBrush = Brush.Parse(warn ? "#4a3f22" : "#4a2a2a"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(7),
            Padding = new global::Avalonia.Thickness(14, 12),
            Child = dock,
        };
    }

    /// <summary>
    /// Show the cc-* tools check in a "repairing" state while the one-click Fix rebuild runs.
    /// MainWindow pushes live progress text here; a normal status refresh restores the screen
    /// when it finishes. Reuses the problem-card style with no fix button (the repair is running).
    /// </summary>
    public void SetToolsRepairing(string detail)
    {
        HomeBusyText.IsVisible = false;
        HomeAllClear.IsVisible = false;
        HomeProblems.IsVisible = true;
        HomeStatusRows.Children.Clear();
        HomeStatusRows.Children.Add(BuildProblemRow(
            "cc-* tools", $"Repairing... {detail}", null, null, warn: true));
    }
}
