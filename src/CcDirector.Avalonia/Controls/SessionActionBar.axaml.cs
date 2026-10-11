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
using CcDirector.Core.ErrorReports;

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
        BtnCompact.IsVisible = caps.HasFlag(DriverCapabilities.CompactContext);
        BtnClearContext.IsVisible = caps.HasFlag(DriverCapabilities.ClearContext);
        BtnHistory.IsVisible = caps.HasFlag(DriverCapabilities.History);
        // A session switch clears any transcribing lock left over from the outgoing session; the
        // caller re-applies the incoming session's state right after Configure.
        SetTranscribingLock(false);
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

    /// <summary>
    /// Lock the non-safety actions while the active session is transcribing a dictated utterance in
    /// the background (the Speak-Send fire-and-forget window). Clear context and History are disabled
    /// so the operator cannot mutate a session that is about to receive its dictated prompt. Stop and
    /// Interrupt stay live on purpose - the brakes are never taken away, even mid-transcription.
    /// </summary>
    public void SetTranscribingLock(bool locked)
    {
        BtnCompact.IsEnabled = !locked;
        BtnClearContext.IsEnabled = !locked;
        BtnHistory.IsEnabled = !locked;
        TranscribingLabel.IsVisible = locked;
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
            // Only Claude has a preassigned ClaudeSessionId; Codex and pi locate by repo path and
            // have none, so don't gate the gauge on it - the driver returns null if it can't resolve.
            var sid = session.ClaudeSessionId ?? "";
            var repoPath = session.RepoPath;
            // EffectiveLaunchArgs carries the launched --model ([1m]) even when it came from the
            // configured default; ClaudeArgs alone is null in that case (#803).
            var launchArgs = session.EffectiveLaunchArgs ?? session.ClaudeArgs;
            var usage = await Task.Run(() => session.Driver.ReadContextUsage(sid, repoPath, launchArgs));

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

    // Stop and Interrupt (issue #1107, item 8). The mildest two of the nine and the most-clicked in the
    // application - and they are pressed at the exact moment the user is already frustrated, which is why
    // "nothing appears to happen" matters here even though a repeated interrupt is harmless. The status
    // line used to be written only AFTER the await, so for the whole call there was no sign the click had
    // registered at all. It now says so before the work starts.

    private async void BtnStopTurn_Click(object? sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || sender is not Control button) return;

        await BusyAction.RunAsync(button, "session action bar", "stop the session's turn", async () =>
        {
            FileLog.Write($"[SessionActionBar] Stop clicked: session={session.Id}");
            ShowStatus("stopping...");
            await session.CancelTurnAsync();
            ShowStatus("turn stopped");
        }, "Stopping...", onFailure: message => ShowStatus($"stop failed: {message}"));
    }

    private async void BtnInterrupt_Click(object? sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || sender is not Control button) return;

        await BusyAction.RunAsync(button, "session action bar", "interrupt the session", async () =>
        {
            FileLog.Write($"[SessionActionBar] Interrupt clicked: session={session.Id}");
            ShowStatus("interrupting...");
            await session.InterruptAsync();
            ShowStatus("interrupted");
        }, "Interrupting...", onFailure: message => ShowStatus($"interrupt failed: {message}"));
    }

    /// <summary>
    /// The question asked before a context clear. Kept as constants so the desktop and the Cockpit say the
    /// SAME thing about the same action - the wording matches
    /// <c>apps/cockpit/src/sessions/SessionActionBar.tsx</c>.
    ///
    /// It states what SURVIVES as well as what is lost. "Clear" on its own reads to some people as "kill my
    /// session"; the useful fact is that the process keeps running and only the conversation goes.
    /// </summary>
    internal const string ClearContextConfirmTitle = "Clear this session's context?";

    internal const string ClearContextConfirmMessage =
        "This resets the conversation in place. The running process keeps going, but the agent loses the " +
        "current conversation. This cannot be undone.";

    /// <summary>
    /// The question asked before a compaction (issue #2167), matching the Cockpit's wording.
    ///
    /// It deliberately reads NOTHING like the clear question above. Clearing destroys the conversation and
    /// says so; compaction preserves it, which is the entire reason it exists. Two confirmations worded
    /// alike would teach one reflex for two opposite outcomes - and the danger runs the wrong way: somebody
    /// who learns to click through the harmless one is being trained on the destructive one.
    /// </summary>
    internal const string CompactContextConfirmTitle = "Compact this session's context?";

    internal const string CompactContextConfirmMessage =
        "This summarizes the conversation so far, freeing room in the context window. The session keeps " +
        "what it has learned, and stays where it is - nothing is sent to it. It can take a minute or two.";

    /// <summary>Whether the Compact button is showing - the capability gate, readable by tests.</summary>
    internal bool CompactButtonVisible => BtnCompact.IsVisible;

    /// <summary>
    /// How the bar asks before a destructive verb. Null in the running application, where it opens the real
    /// modal <see cref="ConfirmDialog"/>. Tests replace it: a modal cannot be answered in a headless run, so
    /// without this seam the decision - the whole point of issue #2169 - could only be verified by hand.
    /// </summary>
    internal Func<string, string, Task<bool>>? ConfirmOverride { get; set; }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (ConfirmOverride is not null)
            return await ConfirmOverride(title, message);

        var owner = TopLevel.GetTopLevel(this) as Window
            ?? throw new InvalidOperationException(
                "[SessionActionBar] No owner window, so the confirmation cannot be shown. Refusing to run a " +
                "destructive action unasked.");
        return await new ConfirmDialog(title, message, confirmLabel).ShowDialog<bool?>(owner) == true;
    }

    /// <summary>
    /// Issue #1107, item 3: A LABEL IS NOT A GUARD. This button set its content to "Compacting..." but was
    /// never disabled, so three clicks bought three compactions - and because compaction is an expensive
    /// model operation, that one cost real money. The label made it worse rather than better: it looked
    /// like a working state, so it read as though re-entrancy had been considered and handled.
    ///
    /// The guard now sits on the click, which also means a second click cannot stack a second confirmation
    /// dialog on top of the first.
    /// </summary>
    private async void BtnCompact_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        await BusyAction.RunAsync(button, "session action bar", "compact the session's context", CompactContextWithConfirmationAsync, "Compacting...",
            onFailure: message => ShowStatus($"compact failed: {message}"));
    }

    /// <summary>
    /// Ask, then compact (issue #2167). This is the safe answer to a context gauge that has gone red - it
    /// summarizes the conversation and carries on, where Clear context beside it would throw the
    /// conversation away.
    ///
    /// It compacts and stops there - no follow-up prompt. A person clicking this has a composer in front of
    /// them and can say what happens next; compact-AND-CONTINUE is a separate verb on the command line, where
    /// an agent rescuing a stuck session has nobody to type the next prompt.
    ///
    /// The button shows a working state for the duration and the status line reports the outcome VERBATIM
    /// from the session, because only the session knows whether the compaction was watched to completion or
    /// merely submitted. Composing a cheerful message here is how "compacted" ends up on screen for a
    /// compaction nobody observed.
    ///
    /// Internal rather than private so the decision is testable, exactly as the clear path is.
    /// </summary>
    internal async Task CompactContextWithConfirmationAsync()
    {
        var session = _session;
        if (session is null) return;

        bool confirmed;
        try
        {
            confirmed = await ConfirmAsync(CompactContextConfirmTitle, CompactContextConfirmMessage, "Compact");
        }
        catch (Exception ex)
        {
            ShowStatus(ShownError.Report("session action bar", "ask before compacting the session's context", $"compact failed: {ex.Message}", ex));
            return;
        }

        if (!confirmed)
        {
            FileLog.Write($"[SessionActionBar] Compact declined at the confirmation: session={session.Id}");
            return;
        }

        // The button's working state and its restoration belong to the caller's BusyAction wrapper, which
        // also DISABLES it - the label juggling that used to live here was never a guard.
        try
        {
            FileLog.Write($"[SessionActionBar] Compact confirmed: session={session.Id}");
            ShowStatus("compacting...");
            // Compact ONLY - no follow-up. See the Cockpit's note: the person is sitting here with a
            // composer, so what happens next is theirs to decide. Compact-and-continue is the command
            // line's verb, for an agent rescuing a session with nobody at its keyboard.
            var outcome = await session.CompactContextAsync(continuePrompt: null);
            ShowStatus(outcome.Detail);
        }
        catch (Exception ex)
        {
            ShowStatus(ShownError.Report("session action bar", "compact the session's context", $"compact failed: {ex.Message}", ex));
        }
    }

    private async void BtnClearContext_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        await BusyAction.RunAsync(button, "session action bar", "clear the session's context", ClearContextWithConfirmationAsync, "Clearing...",
            onFailure: message => ShowStatus($"clear failed: {message}"));
    }

    /// <summary>
    /// Ask, then clear (issue #2169). Clearing destroys the session's whole conversation and cannot be
    /// undone, and this button sits inches from Interrupt and beside a context gauge that turns red -
    /// exactly when somebody reaches for a button. It used to fire on the first click; the Cockpit has asked
    /// since issue #1244, so the same action behaved differently depending on which surface you were looking
    /// at, and the surface with a real mouse was the unguarded one.
    ///
    /// Internal rather than private so the decision is testable: a test can decline the confirmation and
    /// assert that NOTHING was submitted to the driver.
    /// </summary>
    internal async Task ClearContextWithConfirmationAsync()
    {
        var session = _session;
        if (session is null) return;

        bool confirmed;
        try
        {
            confirmed = await ConfirmAsync(ClearContextConfirmTitle, ClearContextConfirmMessage, "Clear context");
        }
        catch (Exception ex)
        {
            // A confirmation that cannot be shown is not permission to proceed.
            ShowStatus(ShownError.Report("session action bar", "ask before clearing the session's context", $"clear failed: {ex.Message}", ex));
            return;
        }

        if (!confirmed)
        {
            FileLog.Write($"[SessionActionBar] Clear context declined at the confirmation: session={session.Id}");
            return;
        }

        try
        {
            FileLog.Write($"[SessionActionBar] Clear context confirmed: session={session.Id}");
            ShowStatus("clearing context...");
            var newId = await session.ClearContextAsync();
            if (newId is not null)
                _sessionManager?.RelinkClaudeSession(session.Id, newId);
            ShowStatus(newId is null ? "context cleared" : $"context cleared (transcript {Shorten(newId)})");
        }
        catch (Exception ex)
        {
            ShowStatus(ShownError.Report("session action bar", "clear the session's context", $"clear failed: {ex.Message}", ex));
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
            ShowStatus(ShownError.Report("session action bar", "open the session's history picker", $"history failed: {ex.Message}", ex));
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
