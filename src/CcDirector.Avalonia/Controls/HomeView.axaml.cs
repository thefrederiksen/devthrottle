using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Home;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

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

        // The heading must match what is actually listed. When the only thing not green is setup
        // still running, "NEEDS ATTENTION" is false and alarming - nothing needs the user at all.
        var onlyInProgress = !gatewayError
            && status.Checks.Any(c => c.Level == HomeCheckLevel.Busy)
            && status.Checks.All(c => c.Level is HomeCheckLevel.Ok or HomeCheckLevel.Busy);
        HomeProblemsHeading.Text = onlyInProgress ? "FINISHING SETUP" : "NEEDS ATTENTION";
        HomeProblemsHeading.Foreground = Brush.Parse(onlyInProgress ? "#7FB2E5" : "#F0B848");

        // Gateway trouble is surfaced here too (its glanceable light is in the rail), so the
        // status screen is a single place to see and fix everything that is wrong.
        // Error report decision (issue #3675): NOT reported here. This row re-displays the Gateway connection's own
        // failure, which the connection reports where it happens (GatewayConnectionPanel, the monitor's FAILED lines).
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
            // Error report decision (issue #3675): a red (Bad) row is a failure the user sees, so it is reported here,
            // on every render - the reporter's fingerprint and rate limit fold the repaints into one row with a count.
            // Amber (Warn) and blue (Busy) rows are not failures: a warning and work still running.
            var detail = check.Level == HomeCheckLevel.Bad
                ? ShownError.Report("home screen", $"pass the check \"{check.Title}\"", check.Detail)
                : check.Detail;
            HomeStatusRows.Children.Add(BuildProblemRow(
                check.Title, detail, fix is null ? null : "Fix", fix,
                warn: check.Level == HomeCheckLevel.Warn,
                busy: check.Level == HomeCheckLevel.Busy));
        }
    }

    /// <summary>
    /// A single check card: icon + title + detail, with an optional fix button. Three looks - red for
    /// a failure, amber for a warning, and calm blue with a progress glyph for work still running
    /// (<paramref name="busy"/>), which is not a problem and never offers a fix.
    /// </summary>
    private Control BuildProblemRow(string title, string detail, string? fixLabel, Action? onFix, bool warn = false, bool busy = false)
    {
        var accent = busy ? "#7FB2E5" : warn ? "#F0B848" : "#E05656";

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
            Text = busy ? "..." : warn ? "!" : "X",
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
            Background = Brush.Parse(busy ? "#171d24" : warn ? "#241f17" : "#241a1a"),
            BorderBrush = Brush.Parse(busy ? "#26384a" : warn ? "#4a3f22" : "#4a2a2a"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(7),
            Padding = new global::Avalonia.Thickness(14, 12),
            Child = dock,
        };
    }

    /// <summary>
    /// Show the tools check in a working state while a rebuild runs - either the one-click Fix or the
    /// automatic first-launch setup (<paramref name="firstRunSetup"/>), which is the expected state on
    /// a new machine and must never read as a failure. MainWindow pushes live progress text here; a
    /// normal status refresh restores the screen when it finishes. No fix button: it is already running.
    /// </summary>
    public void SetToolsRepairing(string detail, bool firstRunSetup = false)
    {
        HomeBusyText.IsVisible = false;
        HomeAllClear.IsVisible = false;
        HomeProblems.IsVisible = true;
        HomeProblemsHeading.Text = firstRunSetup ? "FINISHING SETUP" : "NEEDS ATTENTION";
        HomeProblemsHeading.Foreground = Brush.Parse(firstRunSetup ? "#7FB2E5" : "#F0B848");
        HomeStatusRows.Children.Clear();
        HomeStatusRows.Children.Add(BuildProblemRow(
            HomeStatusBuilder.ToolsRowTitle,
            firstRunSetup ? $"Finishing setup - {detail}" : $"Repairing - {detail}",
            null, null, busy: true));
    }
}
