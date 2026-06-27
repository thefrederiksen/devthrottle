using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The per-session action button row: Stop / Interrupt / Clear context / History,
/// shown or hidden from the active session's agent-driver capability flags
/// (docs/plans/director-drivers.md). Self-contained: MainWindow only calls
/// <see cref="Configure"/> when the active session changes.
///
/// When the session's driver declares <see cref="DriverCapabilities.ContextUsage"/> the bar also
/// shows a live context gauge (issue #799), refreshed by a low-frequency background poll that reads
/// the transcript off the user-interface thread.
/// </summary>
public partial class SessionActionBar : UserControl
{
    /// <summary>Pixel width of the gauge track the fill is scaled against (matches the axaml).</summary>
    private const double GaugeTrackWidth = 64.0;

    /// <summary>How often the context gauge re-reads usage (settled at the low-frequency end of the
    /// 3-5s band - responsive without churning the disk).</summary>
    private static readonly TimeSpan ContextPollInterval = TimeSpan.FromSeconds(4);

    // Band fill brushes (frozen-equivalent immutable brushes), reused across refreshes.
    private static readonly IBrush NeutralFill = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush AmberFill = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush RedFill = new SolidColorBrush(Color.Parse("#EF4444"));

    private Session? _session;
    private SessionManager? _sessionManager;
    private readonly DispatcherTimer _contextTimer;

    public SessionActionBar()
    {
        InitializeComponent();
        _contextTimer = new DispatcherTimer { Interval = ContextPollInterval };
        _contextTimer.Tick += (_, _) => RefreshContextGauge();
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

        // Context gauge: capability-gated, polled while this session is the active one.
        _contextTimer.Stop();
        var hasContextGauge = caps.HasFlag(DriverCapabilities.ContextUsage);
        ContextGaugePanel.IsVisible = hasContextGauge;
        RenderContextGauge(null);
        if (hasContextGauge)
        {
            RefreshContextGauge();      // immediate first read so the gauge is not blank for ~4s
            _contextTimer.Start();
        }
    }

    /// <summary>Timer-callback boundary (and the immediate kick from <see cref="Configure"/>):
    /// reads context usage off the user-interface thread, then renders on it. async void is correct
    /// here - this is a timer callback, the documented exception to the rule.</summary>
    private async void RefreshContextGauge()
    {
        var session = _session;
        if (session is null || !ContextGaugePanel.IsVisible)
            return;

        try
        {
            var sid = session.ClaudeSessionId;
            if (string.IsNullOrEmpty(sid))
            {
                RenderContextGauge(null);   // not linked to a transcript yet
                return;
            }

            var repoPath = session.RepoPath;
            var usage = await Task.Run(() => session.Driver.ReadContextUsage(sid, repoPath));

            // The active session may have changed while the read ran; ignore a stale result.
            if (!ReferenceEquals(_session, session))
                return;

            RenderContextGauge(usage);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionActionBar] RefreshContextGauge FAILED: session={session.Id} {ex.Message}");
        }
    }

    /// <summary>Paint the gauge bar and label from a reading (null = not available yet).</summary>
    private void RenderContextGauge(ContextUsageDto? usage)
    {
        if (usage is null)
        {
            ContextGaugeText.Text = "ctx --";
            ContextGaugeFill.Width = 0;
            ContextGaugeFill.Background = NeutralFill;
            return;
        }

        ContextGaugeText.Text = ContextGauge.FormatLabel(usage);
        ContextGaugeFill.Background = BrushForBand(ContextGauge.SelectBand(usage.PercentUsed));
        ContextGaugeFill.Width = usage.PercentUsed is { } pct
            ? Math.Clamp(pct, 0.0, 100.0) / 100.0 * GaugeTrackWidth
            : 0;
    }

    private static IBrush BrushForBand(ContextUsageBand band) => band switch
    {
        ContextUsageBand.Red => RedFill,
        ContextUsageBand.Amber => AmberFill,
        _ => NeutralFill,
    };

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
