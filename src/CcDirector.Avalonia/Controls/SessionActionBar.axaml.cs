using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The per-session action button row: Stop / Interrupt / Clear context / History,
/// shown or hidden from the active session's agent-driver capability flags
/// (docs/plans/director-drivers.md). Self-contained: MainWindow only calls
/// <see cref="Configure"/> when the active session changes.
/// </summary>
public partial class SessionActionBar : UserControl
{
    private Session? _session;
    private SessionManager? _sessionManager;

    public SessionActionBar()
    {
        InitializeComponent();
    }

    /// <summary>Bind the bar to the active session (null hides every button).</summary>
    public void Configure(SessionManager sessionManager, Session? session)
    {
        _sessionManager = sessionManager;
        _session = session;
        var caps = session?.Driver.Capabilities ?? DriverCapabilities.None;
        BtnStopTurn.IsVisible = caps.HasFlag(DriverCapabilities.Cancel);
        BtnInterrupt.IsVisible = caps.HasFlag(DriverCapabilities.Interrupt);
        BtnClearContext.IsVisible = caps.HasFlag(DriverCapabilities.ClearContext);
        BtnHistory.IsVisible = caps.HasFlag(DriverCapabilities.History);
        ActionStatus.Text = "";
    }

    // Entry-point handlers per CodingStyle: try-catch lives here, the Session throws.

    private async void BtnStopTurn_Click(object? sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            FileLog.Write($"[SessionActionBar] Stop clicked: session={session.Id}");
            await session.CancelTurnAsync();
            ShowStatus("turn stopped");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionActionBar] Stop FAILED: {ex.Message}");
            ShowStatus($"stop failed: {ex.Message}");
        }
    }

    private async void BtnInterrupt_Click(object? sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            FileLog.Write($"[SessionActionBar] Interrupt clicked: session={session.Id}");
            await session.InterruptAsync();
            ShowStatus("interrupted");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionActionBar] Interrupt FAILED: {ex.Message}");
            ShowStatus($"interrupt failed: {ex.Message}");
        }
    }

    private async void BtnClearContext_Click(object? sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            FileLog.Write($"[SessionActionBar] Clear context clicked: session={session.Id}");
            ShowStatus("clearing context...");
            var newId = await session.ClearContextAsync();
            if (newId is not null)
                _sessionManager?.RelinkClaudeSession(session.Id, newId);
            ShowStatus(newId is null ? "context cleared" : $"context cleared (transcript {Shorten(newId)})");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionActionBar] Clear context FAILED: {ex.Message}");
            ShowStatus($"clear failed: {ex.Message}");
        }
    }

    private async void BtnHistory_Click(object? sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            FileLog.Write($"[SessionActionBar] History clicked: session={session.Id}");
            await session.ShowHistoryAsync();
            ShowStatus("history picker opened (Esc closes)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionActionBar] History FAILED: {ex.Message}");
            ShowStatus($"history failed: {ex.Message}");
        }
    }

    private void ShowStatus(string text)
    {
        ActionStatus.Text = text;
        // Fade the note out after a few seconds; the bar is not a log.
        var shown = text;
        DispatcherTimer.RunOnce(() =>
        {
            if (ActionStatus.Text == shown)
                ActionStatus.Text = "";
        }, TimeSpan.FromSeconds(5));
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;
}
