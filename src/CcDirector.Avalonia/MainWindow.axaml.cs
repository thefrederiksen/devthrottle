using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Home;
using CcDirector.Core.Network;
using CcDirector.Core.Sessions;
using CcDirector.Core.Settings;
using CcDirector.Core.Skills;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;
using FileViewerControls = CcDirector.Avalonia.Controls;

namespace CcDirector.Avalonia;

// ==================== VIEW MODELS ====================

public class QueueItemViewModel
{
    public Guid Id { get; init; }
    public string Index { get; init; } = "";
    public string Preview { get; init; } = "";
    public string FullText { get; init; } = "";
}

public class ScreenshotViewModel
{
    public string FilePath { get; }
    public string FileName { get; }
    public string TimeLabel { get; }
    public Bitmap? Thumbnail { get; }

    public ScreenshotViewModel(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        TimeLabel = File.GetLastWriteTime(filePath).ToString("MMM d, h:mm tt");

        try
        {
            using var stream = File.OpenRead(filePath);
            Thumbnail = new Bitmap(stream);
        }
        catch
        {
            Thumbnail = null;
        }
    }
}

// ==================== MAIN WINDOW ====================

public partial class MainWindow : Window
{
    private SessionManager _sessionManager = null!;
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private SessionViewModel? _activeSession;

    // Slash command autocomplete
    private readonly SlashCommandProvider _slashCommandProvider = new();
    private List<SlashCommandItem> _filteredSlashCommands = new();

    // Session git status polling
    private readonly CcDirector.Core.Git.GitStatusProvider _gitStatusProvider = new();
    private global::Avalonia.Threading.DispatcherTimer? _sessionGitTimer;
    private bool _sessionGitRefreshRunning;

    // Interactive TUI mode
    private bool _isInteractiveTuiMode;

    /// <summary>
    /// Claude Code slash commands that launch interactive TUI dialogs requiring direct keyboard input.
    /// When sent from PromptInput, these are intercepted and handled via native dialogs or redirected.
    /// When typed directly in the Terminal tab's ConPTY, they still work natively.
    /// </summary>
    private static readonly HashSet<string> InteractiveTuiCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "settings",
        "status",
        "help",
        "context",
        "copy",
        "diff",
        "hooks",
        "model",
        "theme",
        "permissions", "allowed-tools",
        "resume", "continue",
        "rewind", "checkpoint",
        "export",
        "output-style",
        "memory",
        "stats",
        "plugin",
        "mcp",
        "agents",
    };

    // Terminal scrollbar state
    private bool _updatingScrollBar;

    // Right panel state
    private bool _rightPanelExpanded = true;
    private readonly ObservableCollection<QueueItemViewModel> _queueItems = new();
    private readonly ObservableCollection<ScreenshotViewModel> _screenshots = new();
    private FileSystemWatcher? _screenshotWatcher;
    private DispatcherTimer? _screenshotDebounceTimer;
    private string? _screenshotsDirectory;

    public MainWindow()
    {
        InitializeComponent();
        FileLog.Write("[MainWindow] Avalonia MainWindow initialized");

        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;

        // Register KeyDown as tunnel so it fires before AcceptsReturn consumes Ctrl+Enter
        PromptInput.AddHandler(KeyDownEvent, PromptInput_KeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Window-level Ctrl+H = open Speak dialog. Tunnel routing so the embedded
        // terminal panel does not eat the keystroke (xterm treats Ctrl+H as
        // Backspace). Gated on the prompt bar being visible -- same condition
        // that gates the Speak button itself, i.e. Terminal tab with an active
        // session.
        AddHandler(KeyDownEvent, MainWindow_KeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DropEvent, PromptInput_Drop);
        AddHandler(DragDrop.DragOverEvent, PromptInput_DragOver);

        TerminalHost.ScrollChanged += OnTerminalScrollChanged;
        TerminalHost.ViewFileRequested += OnTerminalViewFileRequested;
        TerminalHost.BrowserLaunchFailed += OnTerminalBrowserLaunchFailed;
        TerminalScrollBar.PropertyChanged += TerminalScrollBar_PropertyChanged;

        SessionList.AddHandler(DragDrop.DragOverEvent, SessionList_DragOver);
        SessionList.AddHandler(DragDrop.DropEvent, SessionList_Drop);
        SessionList.AddHandler(PointerPressedEvent, SessionList_PointerPressed, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Alpha gating: Start FIFO and Handover are alpha features, hidden by default.
        // Re-gate live when the flag is toggled in the Settings dialog.
        ApplyAlphaFeatureVisibility();
        AlphaMode.Changed += OnAlphaModeChanged;
        Closed += (_, _) => AlphaMode.Changed -= OnAlphaModeChanged;

        BuildNativeMenu();
    }

    private void OnAlphaModeChanged()
    {
        // The flag could be toggled off the UI thread (e.g. a future REST write); always hop to it.
        // BuildNativeMenu is rebuilt too because the Session menu's Start FIFO item is alpha-gated.
        Dispatcher.UIThread.Post(() =>
        {
            ApplyAlphaFeatureVisibility();
            BuildNativeMenu();
        });
    }

    /// <summary>Show or hide the alpha-gated toolbar/prompt-bar buttons per the alpha flag.</summary>
    private void ApplyAlphaFeatureVisibility()
    {
        var alpha = AlphaMode.IsEnabled;
        BtnStartFifo.IsVisible = alpha;
        BtnHandover.IsVisible = alpha;
        FileLog.Write($"[MainWindow] ApplyAlphaFeatureVisibility: alphaFeatures={alpha}");
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_activeLeftTab == "Terminal" && _activeSession != null)
            Dispatcher.UIThread.Post(() => TerminalHost.Focus());
        else
            Dispatcher.UIThread.Post(() => PromptInput.Focus());
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] MainWindow_Loaded");

        var app = (App)global::Avalonia.Application.Current!;
        _sessionManager = app.SessionManager;

        SessionList.ItemsSource = _sessions;
        SlimSessionList.ItemsSource = _sessions;
        QueueItemsList.ItemsSource = _queueItems;
        ScreenshotList.ItemsSource = _screenshots;

        // Restore the persisted sidebar collapsed state (no re-persist on restore).
        if (SidebarConfig.Collapsed)
            SetSidebarCollapsed(true, persist: false);

        // Keep group brackets/headers (issue #225) correct after any add/remove/restore.
        // Cheap flag recompute; the drop handler also calls it explicitly after a reorder.
        _sessions.CollectionChanged += (_, _) => RecomputeGroupPositions();

        // Keep the "N need you" header count instant: recompute whenever a session is
        // added/removed or ANY session's status color flips (e.g. a background session goes
        // red while you are on another). The 15s timer remains a backstop.
        _sessions.CollectionChanged += OnSessionsCollectionChanged;

        // Subscribe to session registration for ClaudeSessionId persistence
        _sessionManager.OnClaudeSessionRegistered += OnClaudeSessionRegistered;

        // Sessions created via the Control API (web Manager) need to be wrapped
        // into the Avalonia sidebar so the desktop user can interact with them too.
        _sessionManager.OnSessionCreated += OnExternalSessionCreated;

        // Sessions renamed via the Control API (PATCH /sessions/{sid}) need to refresh
        // the matching SessionViewModel and persist state.
        _sessionManager.OnSessionRenamed += OnExternalSessionRenamed;

        // Sessions killed via the Control API (DELETE /sessions/{sid} from the
        // Cockpit, the Gateway, or a session killing itself) must drop their
        // rail row too - without this the row stays behind wrapping a dead,
        // disposed session (issue #202, root cause of #193).
        _sessionManager.OnSessionRemoved += OnExternalSessionRemoved;

        // Wire source control view file event
        GitChangesView.ViewFileRequested += OnGitViewFileRequested;

        // Wire session browser resume event
        SessionBrowserView.SessionResumeRequested += OnSessionBrowserResumeRequested;

        // Wire clean view rewind event
        CleanView.RewindRequested += OnCleanViewRewindRequested;

        // The Wingman tab is a single-turn view: our prompt, then Claude's full reply,
        // with all tool/bash/thinking cards stripped out. The briefing banner above
        // supplies the title and summary.
        CleanView.ResponseOnlyMode = true;

        // Wire usage dashboard to usage service
        UsageDashboardView.SetUsageService(app.ClaudeUsageService);

        // Wire prompt input text changes for slash command autocomplete
        PromptInput.TextChanged += PromptInput_TextChanged;
        PromptInput.LostFocus += (_, _) => SlashCommandPopup.IsOpen = false;
        PromptInput.GotFocus += PromptInput_GotFocus;

        // Wire right panel tab selection to lazy-load session browser
        RightPanelTabs.SelectionChanged += RightPanelTabs_SelectionChanged;

        SetBuildInfo();
        _ = InitializeScreenshotsPanelAsync();
        // No automatic workspace picker on startup (like VS Code). Use File | Open Workspace.

        // Home page (empty-state): its actions route to the existing flows. Paint it now
        // so the very first frame at zero sessions is the home, not a blank content area.
        HomeView.NewSessionRequested += (_, _) => { FileLog.Write("[MainWindow] Home -> New Session"); _ = ShowNewSessionDialog(); };
        HomeView.OpenToolsRequested += (_, _) => BtnTools_Click(this, new RoutedEventArgs());
        HomeView.OpenSettingsRequested += (_, _) => BtnSettings_Click(this, new RoutedEventArgs());
        HomeView.OpenCockpitRequested += (_, _) => BtnCockpit_Click(this, new RoutedEventArgs());
        HomeView.GatewayClicked += (_, _) => OpenGatewayTroubleshooter();
        HomeView.RecentRepoSelected += (_, path) => { FileLog.Write($"[MainWindow] Home -> recent repo {path}"); _ = StartSessionInRepoAsync(path); };
        UpdateHomeVisibility();

        // Start session git status polling (15s interval)
        _sessionGitTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _sessionGitTimer.Tick += async (_, _) =>
        {
            foreach (var vm in _sessions) vm.RefreshTimeLabels();
            UpdateNeedsYouCount();
            await RefreshSessionGitStatusAsync();
        };
        _sessionGitTimer.Start();

        // Scheduler-leader indicator: show "LEADER" pill on the sidebar and
        // append " -- Leader" to the window title while this Director holds
        // the scheduler mutex. Polled at 5s; the underlying flag is updated
        // by the election thread so the read is just a volatile bool check.
        _schedulerLeaderTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _schedulerLeaderTimer.Tick += (_, _) => RefreshSchedulerLeaderIndicator();
        _schedulerLeaderTimer.Start();
        RefreshSchedulerLeaderIndicator();

        WireGatewayIndicator();
        InitDirectorInfo();

        MaybeShowToolDetectionWizard();
    }

    /// <summary>
    /// On first run (no agent tools configured yet, issue #392), auto-open the tool-detection
    /// wizard so a new user gets a near-zero-effort setup. Once any tool is configured the
    /// wizard never auto-opens again - it can still be re-run on demand from Settings &gt; Agents.
    /// Posted to the UI thread so it opens after the main window is shown.
    /// </summary>
    private void MaybeShowToolDetectionWizard()
    {
        FileLog.Write("[MainWindow] MaybeShowToolDetectionWizard");
        try
        {
            if (!ToolDetectionWizardModel.IsFirstRun())
            {
                FileLog.Write("[MainWindow] MaybeShowToolDetectionWizard: tools already configured; not auto-opening");
                return;
            }

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await OpenToolDetectionWizardAsync();
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[MainWindow] MaybeShowToolDetectionWizard open FAILED: {ex.Message}");
                }
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] MaybeShowToolDetectionWizard FAILED: {ex.Message}");
        }
    }

    /// <summary>Open the first-run tool-detection wizard modally over the main window.</summary>
    internal async Task OpenToolDetectionWizardAsync()
    {
        FileLog.Write("[MainWindow] OpenToolDetectionWizardAsync");
        var app = global::Avalonia.Application.Current as App;
        var options = app?.SessionManager?.Options ?? app?.Options
            ?? throw new InvalidOperationException("AgentOptions not loaded.");
        var dialog = new ToolDetectionWizardDialog(options);
        await dialog.ShowDialog<bool?>(this);
    }

    private void InitDirectorInfo()
    {
        FileLog.Write("[MainWindow] InitDirectorInfo");
        var app = global::Avalonia.Application.Current as App;
        var port = app?.ControlApiHost?.Port;
        var portStr = port is > 0 ? port.Value.ToString() : "...";
        DirectorInfoText.Text = $"{Environment.MachineName}:{portStr}";
    }

    private async void BtnCopyDirectorInfo_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnCopyDirectorInfo_Click");
        try
        {
            var app = global::Avalonia.Application.Current as App;
            var port = app?.ControlApiHost?.Port;
            if (port is null or 0)
            {
                ShowNotification("Control API not started yet.");
                return;
            }
            var url = await Task.Run(() =>
                TailscaleIdentity.ResolveAdvertisedControlApiEndpoint(port.Value));
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { ShowNotification("Clipboard unavailable."); return; }
            await clipboard.SetTextAsync(url);
            ShowNotification($"Copied: {url}");
            FileLog.Write($"[MainWindow] BtnCopyDirectorInfo_Click: copied {url}");
            BtnCopyDirectorInfo.Content = "Copied!";
            await Task.Delay(1500);
            BtnCopyDirectorInfo.Content = "Copy";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnCopyDirectorInfo_Click FAILED: {ex.Message}");
            ShowNotification($"Copy failed: {ex.Message}");
        }
    }

    // ==================== GATEWAY CONNECTION INDICATOR (issues #223/#224) ====================

    private global::Avalonia.Threading.DispatcherTimer? _gatewayAttachTimer;
    private GatewayConnectionMonitor? _gatewayMonitor;
    private string? _lastAutoPoppedFailure;
    private bool _troubleshooterOpen;

    private const string GatewayIconRing = "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M8,3 A5,5 0 1 1 8,13 A5,5 0 1 1 8,3 Z";
    private const string GatewayIconCheck = "M6.2,12.4 L1.6,7.8 L3,6.4 L6.2,9.6 L13,2.8 L14.4,4.2 Z";
    private const string GatewayIconCross = "M3.4,2 L8,6.6 L12.6,2 L14,3.4 L9.4,8 L14,12.6 L12.6,14 L8,9.4 L3.4,14 L2,12.6 L6.6,8 L2,3.4 Z";

    /// <summary>
    /// Attach the sidebar indicator to the host's GatewayConnectionMonitor. The
    /// ControlApiHost starts in the background after the window opens, so retry on a
    /// short timer until it exists, then go fully event-driven.
    /// </summary>
    private void WireGatewayIndicator()
    {
        _gatewayAttachTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _gatewayAttachTimer.Tick += (_, _) => TryAttachGatewayMonitor();
        _gatewayAttachTimer.Start();
        TryAttachGatewayMonitor();
    }

    private void TryAttachGatewayMonitor()
    {
        if (_gatewayMonitor is not null) return;
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null) return;

        _gatewayMonitor = host.GatewayMonitor;
        _gatewayMonitor.Changed += () => Dispatcher.UIThread.Post(UpdateGatewayIndicator);

        // Same host: wire the Control-API status indicator. The bind may have already failed
        // in the background before we attached, so paint the current state now AND subscribe
        // for later changes (the event can fire on a background thread -> marshal to UI).
        host.StartupStatusChanged += () => Dispatcher.UIThread.Post(UpdateControlApiIndicator);

        _gatewayAttachTimer?.Stop();
        _gatewayAttachTimer = null;
        UpdateGatewayIndicator();
        UpdateControlApiIndicator();
        FileLog.Write("[MainWindow] Gateway indicator attached to GatewayConnectionMonitor");
    }

    private bool _controlApiFailureNotified;

    /// <summary>
    /// Paint the Control-API status indicator from <see cref="ControlApiHost.StartupError"/>.
    /// Hidden while the API is healthy (consistent with the auto-update indicator); RED and
    /// visible when the bind failed, with a one-time loud notification so the degraded state
    /// grabs attention immediately rather than only living in a sidebar tile.
    /// </summary>
    private void UpdateControlApiIndicator()
    {
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null) return;

        var error = host.StartupError;
        if (string.IsNullOrEmpty(error))
        {
            // Healthy (or not yet failed): no tile.
            ControlApiIndicator.IsVisible = false;
            return;
        }

        ControlApiIndicatorSub.Text = "Remote, Gateway & phone access are off. Close another Director or free a port, then restart. Click for details.";
        var tip = $"Control API failed to start:\n{error}\n\n"
                + "The local app still works (session badges are live), but this Director is "
                + "invisible to the fleet and cannot be driven remotely.\n"
                + "Fix: free a Control-API port (7879-7898) by closing another Director, then restart this one.";
        ToolTip.SetTip(ControlApiIndicator, tip);
        ControlApiIndicator.IsVisible = true;

        if (!_controlApiFailureNotified)
        {
            _controlApiFailureNotified = true;
            FileLog.Write($"[MainWindow] Control API DOWN surfaced in UI: {error}");
            ShowNotification($"Control API failed to start: {error}. Remote/Gateway access is off -- see the sidebar.");
        }
    }

    private void ControlApiIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            var error = host?.StartupError ?? "unknown error";
            ShowNotification($"Control API down: {error}. Free a port in 7879-7898 (close another Director) and restart this one.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ControlApiIndicator_PointerPressed FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Paint the indicator from the monitor's state. GREEN only on a passed two-way
    /// handshake; the SORENLAPTOP failure mode (heartbeats fine, callback dead) shows
    /// RED here while heartbeats still succeed - that is the whole point of #224.
    /// </summary>
    private void UpdateGatewayIndicator()
    {
        var m = _gatewayMonitor;
        if (m is null) return;

        string icon, accent, bg, border, label, sub;
        switch (m.Status)
        {
            case GatewayConnectionStatus.Verified:
                (icon, accent, bg, border) = (GatewayIconCheck, "#22C55E", "#1B3A2A", "#22C55E");
                label = "GATEWAY CONNECTED";
                sub = $"two-way verified {m.LastVerifiedAt?.ToLocalTime():HH:mm:ss}";
                break;
            case GatewayConnectionStatus.Connecting:
                (icon, accent, bg, border) = (GatewayIconRing, "#F0B848", "#3A331B", "#F0B848");
                label = "GATEWAY VERIFYING...";
                sub = "proving two-way connection";
                break;
            case GatewayConnectionStatus.Failed:
                (icon, accent, bg, border) = (GatewayIconCross, "#EF4444", "#3A1B1B", "#DC2626");
                label = "GATEWAY UNREACHABLE";
                sub = (m.FailureSummary ?? "verification failed") + " - click to troubleshoot";
                break;
            case GatewayConnectionStatus.NoTailnetIdentity:
                // Issue #324: explicit identity-failure state - the problem is LOCAL (no
                // Tailscale identity to advertise), not the Gateway. Name the fix on screen.
                (icon, accent, bg, border) = (GatewayIconCross, "#EF4444", "#3A1B1B", "#DC2626");
                label = "NO TAILNET IDENTITY";
                sub = (m.FailureSummary ?? "start Tailscale on this machine or set gateway.tailnetEndpoint") + " - click to troubleshoot";
                break;
            default:
                (icon, accent, bg, border) = (GatewayIconRing, "#777777", "#2A2A2A", "#3C3C3C");
                label = "NO GATEWAY";
                sub = "local-only Director";
                break;
        }

        GatewayIndicatorIcon.Data = Geometry.Parse(icon);
        GatewayIndicatorIcon.Fill = Brush.Parse(accent);
        GatewayIndicator.Background = Brush.Parse(bg);
        GatewayIndicator.BorderBrush = Brush.Parse(border);
        GatewayIndicatorLabel.Text = label;
        GatewayIndicatorLabel.Foreground = Brush.Parse(accent);
        GatewayIndicatorSub.Text = sub;

        var tip = $"Gateway connection: {m.Status}";
        if (m.LastVerifiedAt is { } at) tip += $"\nLast verified: {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        if (m.LastResult is { } r && r.CallbackOk) tip += $"\nCallback: {r.CallbackLatencyMs} ms at {r.CallbackEndpoint}";
        if (m.FailureSummary is { } f) tip += $"\n{f}";
        tip += "\nClick to open the troubleshooter.";
        ToolTip.SetTip(GatewayIndicator, tip);

        // Mirror the same gateway state onto the home-page card (shown at zero sessions),
        // so both surfaces always agree. A missing gateway is NOT an error (legitimate
        // local-only Director), so the Fix-it affordance shows for the error states only.
        var gatewayError = m.Status is GatewayConnectionStatus.Failed or GatewayConnectionStatus.NoTailnetIdentity;
        _gatewayError = gatewayError;
        HomeView.SetGateway(accent, bg, border, label, sub, gatewayError);
        ApplyHomeHealth();

        // #223: auto-pop the troubleshooter ONCE per distinct failure. A failure that
        // repeats (same summary on every re-verify) never re-pops; a NEW failure does.
        // #324: the identity-failure state is a failure too - same once-per-summary rule.
        if (m.Status is GatewayConnectionStatus.Failed or GatewayConnectionStatus.NoTailnetIdentity
            && m.FailureSummary is { } failure
            && failure != _lastAutoPoppedFailure
            && !_troubleshooterOpen)
        {
            _lastAutoPoppedFailure = failure;
            FileLog.Write($"[MainWindow] Gateway verification failed - auto-opening troubleshooter: {failure}");
            OpenGatewayTroubleshooter();
        }
    }

    private void GatewayIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        OpenGatewayTroubleshooter();
    }

    private void OpenGatewayTroubleshooter()
    {
        try
        {
            if (_troubleshooterOpen) return;
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            if (host is null)
            {
                FileLog.Write("[MainWindow] OpenGatewayTroubleshooter: Control API not started yet");
                return;
            }
            _troubleshooterOpen = true;
            var dialog = new GatewayTroubleshootDialog(host);
            dialog.Closed += (_, _) => _troubleshooterOpen = false;
            // Non-modal on purpose: the troubleshooter is a tool window (its fixes take
            // seconds and the user may want the session list meanwhile); auto-popup as a
            // modal would also steal an active typing session.
            dialog.Show(this);
            FileLog.Write("[MainWindow] Gateway troubleshooter opened");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OpenGatewayTroubleshooter FAILED: {ex.Message}");
            _troubleshooterOpen = false;
        }
    }

    // ==================== HOME (empty-state) ====================

    /// <summary>Last computed readiness, cached so a gateway change can re-tint the header
    /// without re-running the (async) tool/key probes.</summary>
    private HomeStatus? _lastHomeStatus;

    /// <summary>True when the gateway is in an error state (Failed / no tailnet identity).
    /// A simply-absent gateway is NOT an error - a local-only Director is legitimate.</summary>
    private bool _gatewayError;

    /// <summary>
    /// True when any full-content in-window overlay (Tools / Comms / Connections / Scheduler)
    /// is open. The Home page must not paint over an open overlay, so UpdateHomeVisibility
    /// consults this (issue #447).
    /// </summary>
    private bool IsContentOverlayOpen()
        => ToolsOverlay.IsVisible || CommsOverlay.IsVisible
           || ConnectionsOverlay.IsVisible || SchedulerOverlay.IsVisible;

    /// <summary>
    /// Show the full-screen home page exactly when this Director has zero sessions - it is
    /// the "nothing is running, here is the state, start something" screen. The window menu
    /// bar stays; the toolbar is hidden so only the home shows beneath it. The home appears
    /// (and disappears) the moment the session count crosses zero.
    /// </summary>
    private void UpdateHomeVisibility()
    {
        // Home covers the whole window at ZIndex 30; the content overlays (Tools/Comms/etc.)
        // sit lower in the same grid. If Home paints while an overlay is open, the overlay is
        // hidden behind it and its actions look dead (issue #447). So Home shows only at zero
        // sessions AND when no overlay is up; opening/closing an overlay re-runs this method.
        var overlayOpen = IsContentOverlayOpen();
        var showHome = _sessions.Count == 0 && !overlayOpen;
        MainToolbar.IsVisible = !showHome;
        HomeView.IsVisible = showHome;
        FileLog.Write($"[MainWindow] UpdateHomeVisibility: showHome={showHome}, sessions={_sessions.Count}, overlayOpen={overlayOpen}");

        if (!showHome) return;

        HomeView.SetVersion(AppVersion.Display);
        // Paint the gateway card from current state (no-op until the monitor is attached;
        // its Changed event repaints it once it is).
        UpdateGatewayIndicator();
        _ = RefreshHomeAsync();
    }

    /// <summary>
    /// Gather the readiness facts off the UI thread (tool detection probes PATH; the key
    /// resolver may call the Gateway), then render the rows. Per the responsive-UI rule the
    /// card shows "Checking..." immediately and fills in when the probe completes.
    /// </summary>
    private async Task RefreshHomeAsync()
    {
        HomeView.SetBusy();
        try
        {
            var app = (App)global::Avalonia.Application.Current!;
            var options = app.Options;

            var facts = await Task.Run<(bool found, string? claudeVersion, bool keyPresent,
                                        string keyMessage, bool usesGateway, int built, int total)>(async () =>
            {
                var det = new ToolDetectionService().DetectTool(AgentKind.ClaudeCode, options);
                var validation = ToolDetectionService.ReadValidationStatus(AgentKind.ClaudeCode, options);
                var claudeVersion = validation?.Ok == true ? validation.Version : null;

                var catalog = new ToolCatalogService().GetCatalog();
                var built = catalog.Count(d => d.IsBuilt);
                var total = catalog.Count;

                var resolver = new OpenAiKeyResolver(options);
                string? key = await resolver.ResolveAsync();
                var keyPresent = !string.IsNullOrWhiteSpace(key);

                return (det.Found, claudeVersion, keyPresent, resolver.UnavailableMessage,
                        resolver.UsesGateway, built, total);
            });

            _lastHomeStatus = HomeStatusBuilder.Build(
                facts.found, facts.claudeVersion, facts.keyPresent, facts.keyMessage,
                facts.usesGateway, facts.built, facts.total, AppVersion.Display);

            ApplyHomeHealth();

            // Recent repos are file I/O (a folder of history JSON), so load them off the UI thread too.
            var recent = await Task.Run(BuildRecentRepos);
            HomeView.SetRecent(recent);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RefreshHomeAsync FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// The most recently used repositories, newest first, one per repo path. Drawn from the
    /// session history store; the swatch reuses the colour of that repo's latest session.
    /// </summary>
    private List<FileViewerControls.RecentRepoItem> BuildRecentRepos()
    {
        var app = (App)global::Avalonia.Application.Current!;
        return app.SessionHistoryStore.LoadAll()
            .Where(e => !string.IsNullOrWhiteSpace(e.RepoPath))
            .GroupBy(e => e.RepoPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.LastUsedAt).First())
            .OrderByDescending(e => e.LastUsedAt)
            .Take(5)
            .Select(e => new FileViewerControls.RecentRepoItem(
                RepoDisplayName(e.RepoPath),
                e.RepoPath,
                e.CustomColor is { } color && !string.IsNullOrWhiteSpace(color) ? color : "#3C3C3C",
                RelativeTime.Ago(DateTimeOffset.UtcNow - e.LastUsedAt.ToUniversalTime())))
            .ToList();
    }

    private static string RepoDisplayName(string repoPath)
    {
        var name = Path.GetFileName(repoPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? repoPath : name;
    }

    /// <summary>
    /// One-click launch from the home Recent card: start a Claude session in the given repo
    /// with the configured defaults (no dialog). Mirrors the non-group, non-resume path of
    /// <see cref="ShowNewSessionDialog"/>, including the missing-CLI preflight.
    /// </summary>
    private async Task StartSessionInRepoAsync(string repoPath)
    {
        FileLog.Write($"[MainWindow] StartSessionInRepoAsync: {repoPath}");
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            await MessageBox.ShowAsync(this, "Folder not found",
                $"This repository folder no longer exists:\n\n{repoPath}\n\nUse New Session to pick another.");
            return;
        }

        var app = (App)global::Avalonia.Application.Current!;
        var agent = CreateAgent(AgentKind.ClaudeCode);
        var agentExe = agent.ExecutablePath;
        if (ExecutableResolver.Resolve(agentExe) is null)
        {
            var (agentName, installHint) = AgentInstallInfo(AgentKind.ClaudeCode);
            FileLog.Write($"[MainWindow] StartSessionInRepoAsync: {agentName} '{agentExe}' not found on PATH; aborting");
            await MessageBox.ShowAsync(this, $"{agentName} is not installed",
                $"CC Director could not find its command line tool.\n\nLooked for: {agentExe}\n\n{installHint}");
            return;
        }

        var vm = CreateSession(repoPath, null, null, agent, SessionType.Developer);
        if (vm == null)
        {
            FileLog.Write("[MainWindow] StartSessionInRepoAsync: CreateSession returned null");
            await MessageBox.ShowAsync(this, "Could not start session",
                _lastSessionCreateError ?? "See the Director log for details.");
            return;
        }

        app.RepositoryRegistry?.MarkUsed(repoPath);
        SaveSessionToHistory(vm);
        FileLog.Write($"[MainWindow] StartSessionInRepoAsync: started session {vm.Session.Id} in {repoPath}");
    }

    /// <summary>
    /// Combine the cached readiness with the live gateway state into the home's tagline and
    /// status header. "Healthy" requires every check green AND the gateway not in an error
    /// state. Cheap and idempotent: called after a refresh and on every gateway change.
    /// </summary>
    private void ApplyHomeHealth()
    {
        if (_lastHomeStatus is not { } status) return;

        var healthy = status.AllReady && !_gatewayError;
        HomeView.SetTagline(healthy ? "Mission critical for multiple agents" : "Let's finish setting up");
        HomeView.SetStatus(status, healthy);
    }

    private global::Avalonia.Threading.DispatcherTimer? _schedulerLeaderTimer;
    private bool _lastLeaderState;

    private void RefreshSchedulerLeaderIndicator()
    {
        var scheduler = (global::Avalonia.Application.Current as App)?.Scheduler;
        var isLeader = scheduler?.IsLeader == true;
        if (isLeader == _lastLeaderState) return;

        _lastLeaderState = isLeader;
        Title = isLeader ? "CC Director -- Leader" : "CC Director";
    }

    private void SetBuildInfo()
    {
        try
        {
            // Product version front and center ("v0.6.3 (1cc1abd)"); the build
            // timestamp stays in the tooltip - it is still the fastest way to
            // confirm a local slot build actually deployed.
            BuildInfoText.Text = AppVersion.Display;
            var tip = $"Version: {AppVersion.Full}";
            var exePath = Environment.ProcessPath;
            if (exePath != null && File.Exists(exePath))
            {
                var buildTime = File.GetLastWriteTime(exePath);
                tip += $"\nBuilt: {buildTime:yyyy-MM-dd HH:mm:ss}\nPath: {exePath}";
            }
            ToolTip.SetTip(BuildInfoText, tip);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SetBuildInfo FAILED: {ex.Message}");
            BuildInfoText.Text = "v?";
        }
    }

    // ==================== WORKSPACE LOADING ====================

    private async Task LoadWorkspaceAsync(WorkspaceDefinition workspace)
    {
        FileLog.Write($"[MainWindow] LoadWorkspaceAsync: '{workspace.Name}' with {workspace.Sessions.Count} sessions");

        var progress = new WorkspaceProgressDialog(workspace.Name);
        progress.Show(this);

        try
        {
            var sorted = workspace.Sessions.OrderBy(s => s.SortOrder).ToList();
            int total = sorted.Count;

            for (int i = 0; i < total; i++)
            {
                var entry = sorted[i];
                FileLog.Write($"[MainWindow] LoadWorkspaceAsync: creating session {i + 1}/{total}: {entry.RepoPath}");

                progress.UpdateProgress(i + 1, total, entry.CustomName ?? entry.RepoPath);

                var vm = CreateSession(entry.RepoPath, claudeArgs: entry.ClaudeArgs);
                if (vm != null)
                {
                    vm.Rename(entry.CustomName, entry.CustomColor);
                    SaveSessionToHistory(vm);
                }

                // Delay between sessions to prevent Claude Code settings corruption
                if (i < total - 1)
                    await Task.Delay(2500);
            }

            progress.SetComplete();
            FileLog.Write($"[MainWindow] LoadWorkspaceAsync: workspace '{workspace.Name}' loaded");
        }
        finally
        {
            progress.Close();
        }
    }

    /// <summary>
    /// Create a session group (issue #225): all members of <paramref name="groupDef"/> in one
    /// repo, tied by a single GroupId, in the group's fixed member order. Mirrors
    /// <see cref="LoadWorkspaceAsync"/> - sequential with the 2500ms anti-corruption delay
    /// between spawns - so the members land adjacent in the list (and therefore adjacent in
    /// SortOrder), which is what keeps them tied and draggable as one unit.
    /// </summary>
    private async Task CreateGroupSessionAsync(SessionGroupDefinition groupDef, string repoPath, string? agentArgs, AgentKind agentKind)
    {
        var groupId = Guid.NewGuid();
        var repoName = Path.GetFileName(repoPath.TrimEnd('\\', '/'));
        FileLog.Write($"[MainWindow] CreateGroupSessionAsync: '{groupDef.Name}' groupId={groupId}, {groupDef.Members.Count} members in {repoName}");

        var progress = new WorkspaceProgressDialog($"{groupDef.Name} group");
        progress.Show(this);
        try
        {
            for (int i = 0; i < groupDef.Members.Count; i++)
            {
                var member = groupDef.Members[i];
                var memberName = repoName + member.NameSuffix;
                progress.UpdateProgress(i + 1, groupDef.Members.Count, memberName);

                var vm = CreateSession(repoPath, claudeArgs: agentArgs, agentKind: agentKind,
                    sessionType: member.Type, groupId: groupId, groupRole: member.Role, groupName: groupDef.Name);
                if (vm is not null)
                {
                    vm.Rename(memberName, vm.Session.CustomColor);
                    SaveSessionToHistory(vm);
                }

                if (i < groupDef.Members.Count - 1)
                    await Task.Delay(2500);
            }
            progress.SetComplete();
            PersistSessionState(); // stamp the adjacent SortOrders that keep the group tied
            FileLog.Write($"[MainWindow] CreateGroupSessionAsync: '{groupDef.Name}' group created");
        }
        finally
        {
            progress.Close();
        }
    }

    // ==================== SESSION MANAGEMENT ====================

    /// <summary>
    /// Called by SessionManager.OnSessionCreated when a session was created from outside
    /// MainWindow (notably the web Manager via POST /sessions). Wraps the session in a
    /// SessionViewModel and adds it to the sidebar collection on the UI thread.
    /// </summary>
    private void OnExternalSessionCreated(Session session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_sessions.Any(s => s.Session.Id == session.Id))
                {
                    FileLog.Write($"[MainWindow] OnExternalSessionCreated: session {session.Id} already wrapped, skipping");
                    return;
                }
                FileLog.Write($"[MainWindow] OnExternalSessionCreated: wrapping {session.Id} (repo={session.RepoPath})");
                var vm = new SessionViewModel(session);
                _sessions.Add(vm);

                // If nothing is currently shown, surface the externally-created session
                // (e.g. from the web Manager or Control API) instead of leaving the
                // terminal on the empty "Select a session to begin" state.
                if (_activeSession is null)
                {
                    SessionList.SelectedItem = vm;
                    FileLog.Write($"[MainWindow] OnExternalSessionCreated: auto-selected {session.Id} (no active session)");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionCreated FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Called by SessionManager.OnSessionRenamed when a session's CustomName was
    /// updated from outside MainWindow (notably PATCH /sessions/{sid} on the Control API).
    /// Updates the matching SessionViewModel on the UI thread and triggers a persist.
    /// </summary>
    private void OnExternalSessionRenamed(Session session, string? newName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
                if (vm is null)
                {
                    FileLog.Write($"[MainWindow] OnExternalSessionRenamed: no VM for session {session.Id}");
                    return;
                }
                FileLog.Write($"[MainWindow] OnExternalSessionRenamed: session={session.Id}, name=\"{newName}\"");
                vm.Rename(newName);
                PersistSessionState();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionRenamed FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Called by SessionManager.OnSessionRemoved when a session was removed from
    /// outside MainWindow (notably DELETE /sessions/{sid} on the Control API: a
    /// Cockpit/Gateway kill, or a session killing itself). Drops the matching
    /// rail row on the UI thread; the row used to stay behind forever, wrapping
    /// a dead disposed session (issue #202, root cause of #193).
    ///
    /// Idempotent by construction: the desktop's own close flows
    /// (CloseSessionAsync, CloseAllSessionsAsync) remove the row from _sessions
    /// BEFORE calling RemoveSession, so for those this finds no VM and no-ops.
    /// </summary>
    private void OnExternalSessionRemoved(Session session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
                if (vm is null) return; // desktop-initiated close already pruned the row
                FileLog.Write($"[MainWindow] OnExternalSessionRemoved: dropping rail row for {session.Id}");

                if (_activeSession == vm)
                {
                    // Same active-session teardown CloseSessionAsync performs:
                    // unhook the per-session handlers, detach every session-bound
                    // view, and fall back to the placeholder state.
                    vm.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
                    vm.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
                    vm.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
                    TerminalHost.Detach();
                    GitChangesView.Detach();
                    CleanView.Detach();
                    WingmanView.Detach();
                    _activeSession = null;

                    SetSessionHeaderVisible(false);
                    PlaceholderText.IsVisible = true;
                    TerminalDock.IsVisible = false;
                    PromptBarBorder.IsVisible = false;
                }

                _sessions.Remove(vm);
                PersistSessionState();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionRemoved FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Last failure message from <see cref="CreateSession"/>, so UI callers can surface
    /// why a launch failed instead of swallowing it. Reset at the start of each attempt.
    /// </summary>
    private string? _lastSessionCreateError;

    /// <summary>Construct the <see cref="IAgent"/> strategy for the given agent kind.</summary>
    private IAgent CreateAgent(AgentKind agentKind) => agentKind switch
    {
        AgentKind.Pi => new PiAgent(_sessionManager.Options),
        AgentKind.Codex => new CodexAgent(_sessionManager.Options),
        AgentKind.Gemini => new GeminiAgent(_sessionManager.Options),
        AgentKind.OpenCode => new OpenCodeAgent(_sessionManager.Options),
        _ => new ClaudeAgent(_sessionManager.Options)
    };

    /// <summary>Create a session using a pre-built <see cref="IAgent"/> (e.g. a
    /// <see cref="RawCliAgent"/> constructed by the dialog).</summary>
    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId, string? userArgs, IAgent agent, SessionType sessionType, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agent.Kind}, exe={agent.ExecutablePath}, type={sessionType}, group={groupId?.ToString() ?? "none"}, resume={resumeSessionId ?? "null"}");
        _lastSessionCreateError = null;
        try
        {
            var session = _sessionManager.CreateSession(repoPath, agent, userArgs, SessionBackendType.ConPty, resumeSessionId, sessionType, groupId, groupRole, groupName);
            FileLog.Write($"[MainWindow] CreateSession: session created, id={session.Id}, pid={session.ProcessId}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            FileLog.Write($"[MainWindow] CreateSession: added to UI");

            return vm;
        }
        catch (Exception ex)
        {
            _lastSessionCreateError = ex.Message;
            FileLog.Write($"[MainWindow] CreateSession FAILED: {ex.Message}");
            return null;
        }
    }

    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId = null, string? claudeArgs = null, AgentKind agentKind = AgentKind.ClaudeCode, SessionType sessionType = SessionType.Developer, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agentKind}, type={sessionType}, group={groupId?.ToString() ?? "none"}, resume={resumeSessionId ?? "null"}, args={claudeArgs ?? "default"}");
        _lastSessionCreateError = null;
        try
        {
            IAgent agent = CreateAgent(agentKind);
            var session = _sessionManager.CreateSession(repoPath, agent, claudeArgs, SessionBackendType.ConPty, resumeSessionId, sessionType, groupId, groupRole, groupName);
            FileLog.Write($"[MainWindow] CreateSession: session created, id={session.Id}, pid={session.ProcessId}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            FileLog.Write($"[MainWindow] CreateSession: added to UI");

            // Session-type playbooks are intentionally NOT injected into the terminal:
            // the type still drives the badge, tooltip, and wingman mission clause, but
            // the session starts with a clean composer.

            return vm;
        }
        catch (Exception ex)
        {
            _lastSessionCreateError = ex.Message;
            FileLog.Write($"[MainWindow] CreateSession FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Create a GitHub Actions remote session: the work runs on a GitHub-hosted
    /// runner and streams into a normal session window. Surfaces setup failures
    /// (missing token, etc.) as an explicit dialog rather than silently failing.
    /// </summary>
    private async Task CreateRemoteSessionAsync(RemoteSessionConfig config)
    {
        FileLog.Write($"[MainWindow] CreateRemoteSessionAsync: {config.Slug} mode={config.TriggerMode}");
        try
        {
            var session = _sessionManager.CreateGitHubActionsSession(config);
            FileLog.Write($"[MainWindow] CreateRemoteSessionAsync: session created, id={session.Id}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;

            ShowRenameDialog(vm);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] CreateRemoteSessionAsync FAILED: {ex.Message}");
            await MessageBox.ShowAsync(this,
                "Could not start remote session",
                "CC Director could not start the GitHub Actions session.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Human-readable name and install guidance for an agent CLI, shown when its
    /// executable cannot be found on PATH.
    /// </summary>
    private static (string DisplayName, string InstallHint) AgentInstallInfo(AgentKind kind) => kind switch
    {
        AgentKind.Pi => ("Pi",
            "Install it with: npm install -g @earendil-works/pi-coding-agent"),
        AgentKind.Codex => ("Codex",
            "Install it with: npm install -g @openai/codex"),
        AgentKind.Gemini => ("Gemini CLI",
            "Install it with: npm install -g @google/gemini-cli"),
        AgentKind.OpenCode => ("OpenCode",
            "Install it from https://opencode.ai (for example: 'npm install -g opencode-ai', "
            + "'brew install sst/tap/opencode', or 'scoop install opencode'), then make sure the "
            + "'opencode' command is on your PATH."),
        _ => ("Claude Code",
            "Install Claude Code and make sure the 'claude' command is on your PATH.")
    };

    private void SelectSession(SessionViewModel? vm)
    {
        // Close overlays when switching to any session
        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }

        if (vm == _activeSession) return;

        // Save prompt text and selected tab for outgoing session
        if (_activeSession != null)
        {
            _activeSession.Session.PendingPromptText = PromptInput.Text;
            _activeSession.Session.SelectedTabName = _activeLeftTab;
            FileLog.Write($"[MainWindow] SelectSession: saved prompt and tab={_activeLeftTab} for {_activeSession.Session.Id}");

            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            _activeSession.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
            _activeSession.Session.OnCachedExplainChanged -= OnActiveSessionCachedExplainChanged;
            TerminalHost.Detach();
            GitChangesView.Detach();
            CleanView.Detach();
            WingmanView.Detach();
        }

        _activeSession = vm;

        // Driver-capability action buttons (Stop / Interrupt / Clear context /
        // History) follow the active session; null hides them all.
        ActionBar.Configure(_sessionManager, vm?.Session);

        if (vm == null)
        {
            SetSessionHeaderVisible(false);
            PlaceholderText.IsVisible = true;
            TerminalDock.IsVisible = false;
            PromptBarBorder.IsVisible = false;
            TabBarRefreshButton.IsVisible = false;
            TabBarCaptureButton.IsVisible = false;
            TabBarOpenWingmanButton.IsVisible = false;
            WingmanTabButton.IsVisible = false;
            GitChangesView.Detach();
            CleanView.Detach();
            WingmanView.Detach();
            return;
        }

        // Subscribe to metadata and activity changes for header updates
        vm.Session.OnClaudeMetadataChanged += OnActiveSessionMetadataChanged;
        vm.Session.OnActivityStateChanged += OnActiveSessionActivityChanged;
        // Subscribe to wingman-injected prompt text. The wingman watches the
        // terminal buffer for text Claude Code has placed in its own input line
        // and pushes it through this event; we mirror it into "Type a message..."
        // when the box is empty.
        vm.Session.OnPendingPromptTextChanged += OnActiveSessionPendingPromptTextChanged;
        // Re-render the Wingman tab whenever ProactiveExplainService stores a fresh
        // briefing on this session. The tab is a passive viewer of CachedExplainText.
        vm.Session.OnCachedExplainChanged += OnActiveSessionCachedExplainChanged;

        // Update header
        SetSessionHeaderVisible(true);
        UpdateSessionHeader();

        // Attach terminal
        PlaceholderText.IsVisible = false;
        TerminalDock.IsVisible = true;
        TerminalHost.Attach(vm.Session);
        UpdateScrollBar();

        // Attach source control (hide tab if no .git)
        GitChangesView.Attach(vm.Session.RepoPath);
        UpdateSourceControlTabVisibility(vm.Session.RepoPath);

        // Attach clean view (legacy Agent tab)
        CleanView.Attach(vm.Session);

        // Attach the Wingman observability tab (right panel) to the new session.
        WingmanView.Attach(vm.Session);

        // Show prompt bar
        PromptBarBorder.IsVisible = true;

        // Wingman tab is alpha-hidden
        WingmanTabButton.IsVisible = false;
        // Render whatever cached briefing the ProactiveExplainService has produced so far
        // (or the brand-new greeting set on session creation) IMMEDIATELY for responsiveness,
        // then asynchronously upgrade to the richer Gateway turn brief if one exists. Reset the
        // per-session gateway-brief state first so a stale brief from the previous session is
        // never shown against the incoming one.
        _wingmanShowingGatewayBrief = false;
        _lastWingmanGatewayFetchUtc = DateTime.MinValue;
        RenderWingmanCachedExplain(vm.Session);
        RefreshWingmanTabAsync(vm.Session, force: true);

        // Restore prompt text for incoming session
        PromptInput.Text = vm.Session.PendingPromptText ?? "";
        PromptInput.CaretIndex = PromptInput.Text.Length;

        // Restore last selected tab. The Session/Agent tabs were removed; the
        // wingman/voice view now opens in an external browser. Normalize any
        // persisted values from older builds and default to Terminal. Also fall back
        // to Terminal if the saved tab was Wingman but the session has it disabled.
        var tabName = vm.Session.SelectedTabName;
        if (string.Equals(tabName, "Session", StringComparison.Ordinal) ||
            string.Equals(tabName, "Agent", StringComparison.Ordinal))
            tabName = "Terminal";
        if (string.Equals(tabName, "Wingman", StringComparison.Ordinal) && !vm.Session.WingmanEnabled)
            tabName = "Terminal";
        if (string.IsNullOrEmpty(tabName)) tabName = "Terminal";
        if (tabName != _activeLeftTab)
            SwitchLeftTab(tabName);

        // Switch document tabs to new session
        SwitchDocumentTabsToSession(vm.Session.Id);

        // Refresh right panel for new session
        RefreshQueuePanel();

        // Persist session state (debounced)
        PersistSessionState();

        // Redirect focus to terminal or prompt
        if (_activeLeftTab == "Terminal")
            Dispatcher.UIThread.Post(() => TerminalHost.Focus());
        else
            Dispatcher.UIThread.Post(() => PromptInput.Focus());

        FileLog.Write($"[MainWindow] SelectSession: {vm.DisplayName}");
    }

    private void OnActiveSessionMetadataChanged(ClaudeSessionMetadata? metadata)
    {
        Dispatcher.UIThread.Post(UpdateSessionHeader);
    }

    private void OnActiveSessionActivityChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(UpdateSessionHeader);
        // Wingman note updates are driven by OnCachedExplainChanged, not by activity-state
        // edges. The ProactiveExplainService owns the analysis; this tab only reads what
        // it has produced.
    }

    private void OnActiveSessionCachedExplainChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeSession is null) return;
            // The local explain changed. The Gateway turn brief is the preferred source when
            // present, so don't clobber a shown gateway brief with the weaker local one - the
            // gateway poll keeps it fresh. Only re-render when the local explain IS the source.
            if (_wingmanShowingGatewayBrief) return;
            RenderWingmanCachedExplain(_activeSession.Session);
        });
    }

    /// <summary>
    /// Mirror wingman-detected Claude Code prompt injections into the
    /// "Type a message..." textbox. Only acts on wingman-sourced writes
    /// (source=="wingman") so the textbox's own user-driven save (source=="user")
    /// doesn't loop back. Never clobbers text the user is currently composing.
    /// </summary>
    private void OnActiveSessionPendingPromptTextChanged(string? text, string source)
    {
        if (!string.Equals(source, "wingman", StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(text)) return;
        // Capture into a non-nullable local so the lambda below sees a definite
        // string and we don't need the null-forgiving operator (forbidden by CodingStyle).
        string injectedText = text;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_activeSession is null) return;
                // Honor user input: only fill an empty box. If they've started typing,
                // the wingman's suggestion is silently dropped for this cycle.
                if (!string.IsNullOrEmpty(PromptInput.Text)) return;
                PromptInput.Text = injectedText;
                PromptInput.CaretIndex = injectedText.Length;
                FileLog.Write($"[MainWindow] wingman injected prompt text: len={injectedText.Length}, preview=\"{(injectedText.Length > 60 ? injectedText[..60] + "..." : injectedText)}\"");
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnActiveSessionPendingPromptTextChanged FAILED: {ex.Message}");
        }
    }

    private async Task CloseAllSessionsAsync()
    {
        FileLog.Write("[MainWindow] CloseAllSessionsAsync");
        if (_activeSession != null)
        {
            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            _activeSession.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
        }
        TerminalHost.Detach();
        GitChangesView.Detach();
        CleanView.Detach();
        WingmanView.Detach();
        _activeSession = null;

        var snapshots = _sessions.ToList();
        _sessions.Clear();

        foreach (var vm in snapshots)
        {
            try
            {
                await _sessionManager.KillSessionAsync(vm.Session.Id);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] CloseAllSessionsAsync: failed to kill {vm.Session.Id}: {ex.Message}");
            }
            _sessionManager.RemoveSession(vm.Session.Id);
        }

        SetSessionHeaderVisible(false);
        PlaceholderText.IsVisible = true;
        TerminalDock.IsVisible = false;
        PromptBarBorder.IsVisible = false;

        FileLog.Write($"[MainWindow] CloseAllSessionsAsync: removed {snapshots.Count} session(s)");
    }

    // ==================== SIDEBAR COLLAPSE ====================

    /// <summary>Width of the collapsed sidebar strip: room for the status dots.</summary>
    private const double SidebarCollapsedWidth = 36;

    /// <summary>
    /// The sidebar column width before the last collapse, so expand restores the
    /// user's splitter-chosen width rather than snapping back to the default.
    /// </summary>
    private GridLength _sidebarExpandedWidth = new(264);

    private void SidebarCollapse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetSidebarCollapsed(true, persist: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SidebarCollapse_Click FAILED: {ex}");
        }
    }

    private void SidebarExpand_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetSidebarCollapsed(false, persist: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SidebarExpand_Click FAILED: {ex}");
        }
    }

    /// <summary>
    /// Collapse the session sidebar to a slim status-dot strip, or expand it back.
    /// Swaps the full/slim panels, resizes the grid column, and disables the
    /// splitter while collapsed.
    /// </summary>
    private void SetSidebarCollapsed(bool collapsed, bool persist)
    {
        FileLog.Write($"[MainWindow] SetSidebarCollapsed: collapsed={collapsed}, persist={persist}");

        var column = MainLayoutGrid.ColumnDefinitions[0];
        if (collapsed)
        {
            // Remember the current (possibly splitter-resized) width for expand.
            if (column.Width.IsAbsolute && column.Width.Value > SidebarCollapsedWidth)
                _sidebarExpandedWidth = column.Width;
            column.Width = new GridLength(SidebarCollapsedWidth);
        }
        else
        {
            column.Width = _sidebarExpandedWidth;
        }

        SidebarFullPanel.IsVisible = !collapsed;
        SidebarSlimPanel.IsVisible = collapsed;
        SidebarSplitter.IsEnabled = !collapsed;

        if (persist)
            SidebarConfig.SetCollapsed(collapsed);
    }

    /// <summary>
    /// A status dot in the collapsed sidebar strip was clicked: select that session
    /// (same effect as clicking its row in the expanded list).
    /// </summary>
    private void SlimSessionDot_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control { DataContext: SessionViewModel vm })
                return;
            FileLog.Write($"[MainWindow] SlimSessionDot_Click: {vm.Session.Id}");
            SessionList.SelectedItem = vm;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SlimSessionDot_Click FAILED: {ex}");
        }
    }

    // ==================== SESSION CONTEXT MENU ====================

    private void SessionMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] SessionMenuButton_Click");
        if (sender is not Button button)
            return;

        // Find the SessionViewModel from the button's DataContext
        var vm = button.DataContext as SessionViewModel;
        if (vm == null)
            return;

        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => ShowRenameDialog(vm);

        // Copy a full handover block (session name + id, plus this Director's identity
        // and version) to the clipboard so it can be handed to another agent (e.g. via
        // the Control API) to locate, recall from memory, and talk to this exact session.
        var copyId = new MenuItem { Header = "Copy Handover Info" };
        copyId.Click += async (_, _) =>
        {
            try
            {
                await CopySessionNameAndId(vm);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] Copy Handover Info FAILED: {ex.Message}");
                ShowNotification("Copy failed");
            }
        };

        var separator1 = new Separator();

        var openJsonl = new MenuItem { Header = "Open .jsonl in Explorer" };
        openJsonl.Click += (_, _) => OpenSessionJsonl(vm);

        var separator2 = new Separator();

        var openExplorer = new MenuItem { Header = "Open in Explorer" };
        openExplorer.Click += (_, _) => OpenInExplorer(vm);

        var openVsCode = new MenuItem { Header = "Open in VS Code" };
        openVsCode.Click += (_, _) => OpenInVsCode(vm);

        // Jumps straight to GitHub's "new issue" form for the session's repository
        // (derived from the repo's origin remote) so a bug can be filed mid-session.
        var newIssue = new MenuItem { Header = "New GitHub Issue" };
        newIssue.Click += (_, _) => OpenNewGitHubIssue(vm);

        var relink = new MenuItem { Header = "Relink Session..." };
        relink.Click += (_, _) => _ = ShowRelinkDialog(vm);

        var separatorHold = new Separator();

        // On-hold toggle: parks the session out of the FIFO rotation and paints its
        // list strip dark blue so you can see at a glance which sessions you've set aside.
        var hold = new MenuItem { Header = vm.IsOnHold ? "Take Off Hold" : "Hold" };
        hold.Click += (_, _) => ToggleSessionHold(vm);

        // Wingman toggle: when on, the session gets the auto-explain briefing, the
        // Voice/Wingman tabs, and the Yellow "Wingman is reading" state. Removing it
        // drops the session back to a plain terminal (Blue->Red, no Wingman tabs).
        var wingman = new MenuItem { Header = vm.Session.WingmanEnabled ? "Remove Wingman" : "Add Wingman" };
        wingman.Click += (_, _) => ToggleSessionWingman(vm);

        var separator3 = new Separator();

        var close = new MenuItem { Header = "Close Session" };
        close.Click += (_, _) => _ = CloseSessionAsync(vm);

        menu.Items.Add(rename);
        menu.Items.Add(relink);
        menu.Items.Add(copyId);
        menu.Items.Add(separator1);
        menu.Items.Add(openJsonl);
        menu.Items.Add(separator2);
        menu.Items.Add(openExplorer);
        menu.Items.Add(openVsCode);
        menu.Items.Add(newIssue);
        menu.Items.Add(separatorHold);
        menu.Items.Add(hold);
        menu.Items.Add(wingman);
        menu.Items.Add(separator3);
        menu.Items.Add(close);

        menu.Open(button);
    }

    private void ToggleSessionHold(SessionViewModel vm)
    {
        var newState = !vm.Session.OnHold;
        FileLog.Write($"[MainWindow] ToggleSessionHold: session={vm.Session.Id}, onHold={newState}");
        vm.Session.OnHold = newState;
        ShowNotification(newState
            ? $"{vm.DisplayName} put on hold"
            : $"{vm.DisplayName} taken off hold");
    }

    /// <summary>
    /// Flips <see cref="Session.WingmanEnabled"/> for the session. When the active
    /// session is toggled, the Wingman tab is shown/hidden immediately and the view
    /// falls back to Terminal if the Wingman tab was open while it gets disabled.
    /// </summary>
    private void ToggleSessionWingman(SessionViewModel vm)
    {
        var newState = !vm.Session.WingmanEnabled;
        FileLog.Write($"[MainWindow] ToggleSessionWingman: session={vm.Session.Id}, wingmanEnabled={newState}");
        vm.Session.WingmanEnabled = newState;
        PersistSessionState();

        if (_activeSession == vm)
        {
            WingmanTabButton.IsVisible = false;
            if (!newState && string.Equals(_activeLeftTab, "Wingman", StringComparison.Ordinal))
                SwitchLeftTab("Terminal");
        }

        ShowNotification(newState
            ? $"Wingman added to {vm.DisplayName}"
            : $"Wingman removed from {vm.DisplayName}");
    }

    /// <summary>
    /// Copies a full handover block to the clipboard: the session's display name and
    /// stable ID plus the identity of the Director hosting it (Director ID, machine,
    /// version) and the Control API endpoint another machine can reach it at. When this
    /// node is on a tailnet the endpoint is the Tailscale Serve front door
    /// (https://&lt;magicdns&gt;:&lt;port&gt;) - the same address the Director advertises to
    /// the Gateway - so the block is usable from any tailnet machine, not just this one.
    /// This is everything another agent needs to locate the session and talk to it.
    /// </summary>
    private async Task CopySessionNameAndId(SessionViewModel vm)
    {
        var app = global::Avalonia.Application.Current as App;
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        var lines = new List<string>
        {
            $"Name: {vm.DisplayName}",
            $"Session ID: {vm.Session.Id}",
            $"Repo: {vm.RepoPath}",
            $"Director ID: {app?.ControlApiHost?.DirectorId ?? "(Control API not started)"}",
            $"Machine: {Environment.MachineName}",
            $"Version: {version}",
        };
        var port = app?.ControlApiHost?.Port;
        if (port is > 0)
        {
            // Resolving the tailnet front door shells the tailscale CLI (up to ~5s); keep
            // it off the UI thread so the copy action stays responsive.
            var endpoint = await Task.Run(() =>
                TailscaleIdentity.ResolveAdvertisedControlApiEndpoint(port.Value));
            lines.Add($"Control API: {endpoint}");
        }

        var text = string.Join("\n", lines);
        FileLog.Write($"[MainWindow] CopySessionNameAndId: session={vm.Session.Id}, director={app?.ControlApiHost?.DirectorId}, version={version}");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            FileLog.Write("[MainWindow] CopySessionNameAndId: no clipboard available");
            ShowNotification("Clipboard unavailable");
            return;
        }
        await clipboard.SetTextAsync(text);
        ShowNotification($"Copied handover info for {vm.DisplayName}");
    }

    private async void ShowRenameDialog(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] ShowRenameDialog: session={vm.Session.Id}, name={vm.DisplayName}");
        var dialog = new RenameSessionDialog(vm.DisplayName);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true)
        {
            vm.Rename(dialog.SessionName, null);
            PersistSessionState();
            UpdateSessionHistory(vm);

            if (_activeSession == vm)
                UpdateSessionHeader();

            FileLog.Write($"[MainWindow] ShowRenameDialog: confirmed, name={dialog.SessionName}");
        }
        else
        {
            FileLog.Write("[MainWindow] ShowRenameDialog: cancelled");
        }
    }

    private async Task ShowRelinkDialog(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] ShowRelinkDialog: session={vm.Session.Id}");
        var dialog = new RelinkSessionDialog(vm.Session.RepoPath);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true && !string.IsNullOrEmpty(dialog.SelectedSessionId))
        {
            FileLog.Write($"[MainWindow] ShowRelinkDialog: relinking to {dialog.SelectedSessionId}");
            _sessionManager.RelinkClaudeSession(vm.Session.Id, dialog.SelectedSessionId);

            if (_activeSession == vm)
            {
                UpdateSessionHeader();
                CleanView.Detach();
                CleanView.Attach(vm.Session);
            }

            ShowNotification($"Session relinked to {dialog.SelectedSessionId[..8]}...");
        }
        else
        {
            FileLog.Write("[MainWindow] ShowRelinkDialog: cancelled");
        }
    }

    private void OpenSessionJsonl(SessionViewModel vm)
    {
        FileLog.Write("[MainWindow] OpenSessionJsonl");
        var claudeSessionId = vm.Session.ClaudeSessionId;
        if (string.IsNullOrEmpty(claudeSessionId))
        {
            FileLog.Write("[MainWindow] OpenSessionJsonl: no ClaudeSessionId");
            ShowNotification("Session not linked to Claude yet -- no .jsonl file available");
            return;
        }

        var jsonlPath = ClaudeSessionReader.GetJsonlPath(claudeSessionId, vm.Session.RepoPath);
        if (!File.Exists(jsonlPath))
        {
            FileLog.Write($"[MainWindow] OpenSessionJsonl: file not found: {jsonlPath}");
            ShowNotification($"Session file not found: {jsonlPath}");
            return;
        }

        FileLog.Write($"[MainWindow] OpenSessionJsonl: opening {jsonlPath}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{jsonlPath}\"",
            UseShellExecute = true,
        });
    }

    private void OpenInExplorer(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] OpenInExplorer: {vm.Session.RepoPath}");
        if (!Directory.Exists(vm.Session.RepoPath))
        {
            ShowNotification($"Directory not found: {vm.Session.RepoPath}");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = vm.Session.RepoPath,
            UseShellExecute = true,
        });
    }

    private void OpenInVsCode(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] OpenInVsCode: {vm.Session.RepoPath}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"\"{vm.Session.RepoPath}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Opens the default browser on GitHub's "new issue" form for the session's
    /// repository. The repo is resolved from the session repo's origin remote;
    /// a clear notification is shown when the repo has no GitHub origin.
    /// </summary>
    private async void OpenNewGitHubIssue(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] OpenNewGitHubIssue: repo={vm.Session.RepoPath}");
        try
        {
            var url = await Task.Run(() => GitHubUrls.BuildNewIssueUrl(vm.Session.RepoPath));
            FileLog.Write($"[MainWindow] OpenNewGitHubIssue: opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OpenNewGitHubIssue FAILED: {ex.Message}");
            ShowNotification(ex.Message);
        }
    }

    private async Task CloseSessionAsync(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] CloseSessionAsync: session={vm.Session.Id}");

        if (_activeSession == vm)
        {
            vm.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            vm.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            vm.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
            TerminalHost.Detach();
            GitChangesView.Detach();
            CleanView.Detach();
            WingmanView.Detach();
            _activeSession = null;

            SetSessionHeaderVisible(false);
            PlaceholderText.IsVisible = true;
            TerminalDock.IsVisible = false;
            PromptBarBorder.IsVisible = false;
        }

        _sessions.Remove(vm);
        PersistSessionState();

        await Task.Run(async () =>
        {
            try
            {
                await _sessionManager.KillSessionAsync(vm.Session.Id);
                _sessionManager.RemoveSession(vm.Session.Id);
                FileLog.Write($"[MainWindow] CloseSessionAsync: cleanup complete for {vm.Session.Id}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] CloseSessionAsync cleanup FAILED: {ex.Message}");
            }
        });
    }

    private CancellationTokenSource? _enterRetryCts;

    private const int PersistDebounceMs = 250;
    private CancellationTokenSource? _persistDebounceCts;

    private void PersistSessionState()
    {
        // Sync prompt text on the UI thread before background debounce
        SyncPromptTextToSessions();

        _persistDebounceCts?.Cancel();
        _persistDebounceCts = new CancellationTokenSource();
        var cts = _persistDebounceCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PersistDebounceMs, cts.Token);
                PersistSessionStateCore();
            }
            catch (TaskCanceledException) { /* debounce superseded */ }
        });
    }

    private void PersistSessionStateCore()
    {
        FileLog.Write("[MainWindow] PersistSessionStateCore");
        try
        {
            var app = (App)global::Avalonia.Application.Current!;
            var persisted = _sessions.Select((vm, i) => new PersistedSession
            {
                Id = vm.Session.Id,
                RepoPath = vm.Session.RepoPath,
                ClaudeArgs = vm.Session.ClaudeArgs,
                CustomName = vm.Session.CustomName,
                CustomColor = vm.Session.CustomColor,
                ClaudeSessionId = vm.Session.ClaudeSessionId,
                ActivityState = vm.Session.ActivityState,
                BackendType = vm.Session.BackendType,
                PendingPromptText = vm.Session.PendingPromptText,
                WingmanEnabled = vm.Session.WingmanEnabled,
                SortOrder = i,
            });
            app.SessionStateStore.Save(persisted);

            // Mirror the live roster into the durable crash journal (issue #212 L5). Same
            // snapshot, but keyed per-Director and preserved across an abnormal death so the
            // sessions can be recovered (unlike sessions.json, which is cleared every startup).
            app.CrashJournal?.Update(_sessions.Select(vm => new DirectorCrashJournalSession
            {
                SessionId = vm.Session.Id.ToString(),
                Name = vm.Session.CustomName,
                RepoPath = vm.Session.RepoPath,
                Agent = vm.Session.AgentKind.ToString(),
                ClaudeSessionId = vm.Session.ClaudeSessionId,
                CreatedAtUtc = vm.Session.CreatedAt,
            }));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] PersistSessionStateCore FAILED: {ex.Message}");
        }
    }

    private void SyncPromptTextToSessions()
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            _sessions[i].Session.SortOrder = i;
        }

        if (_activeSession != null)
        {
            _activeSession.Session.PendingPromptText = PromptInput.Text;
            _activeSession.Session.SelectedTabName = _activeLeftTab;
        }
    }

    // ==================== SESSION HEADER ====================

    private void BtnOpenRemoteThread_Click(object? sender, RoutedEventArgs e)
    {
        var url = _activeSession?.Session.RemoteThreadUrl;
        OpenUrlInBrowser(url);
    }

    private void BtnOpenRemoteActions_Click(object? sender, RoutedEventArgs e)
    {
        var slug = _activeSession?.Session.RemoteRepo;
        if (string.IsNullOrEmpty(slug)) return;
        OpenUrlInBrowser($"https://github.com/{slug}/actions");
    }

    // Open the Cockpit. We ASK THE CONFIGURED GATEWAY (GET /cockpit) rather than hardcoding a
    // host/port: the gateway owns the Cockpit port and returns its Tailscale front-door URL.
    // The base URL comes from the one configured source of truth -- GatewayConfig (the gateway
    // block of config.json). When a remote gateway is configured we probe IT; we fall back to
    // the local 127.0.0.1:7878 default ONLY when no gateway URL is configured at all (the
    // same-machine setup). There is NO localhost fallback in the browser: if the gateway has no
    // tailnet URL (Tailscale down) we say so and open nothing, never a loopback URL that only
    // works on this machine. Both failure paths surface as a modal dialog naming the URL we
    // actually probed: a toolbar button that silently does nothing is just confusing.
    private async void BtnCockpit_Click(object? sender, RoutedEventArgs e)
    {
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(GatewayConfig.Load());
        FileLog.Write($"[MainWindow] BtnCockpit_Click: asking gateway for Cockpit URL, baseUrl={baseUrl}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var info = await http.GetFromJsonAsync<global::CcDirector.Gateway.Contracts.CockpitInfoDto>(
                baseUrl + "/cockpit");
            if (info?.Url is { } url)
            {
                FileLog.Write($"[MainWindow] BtnCockpit_Click: opening {url} (up={info.Up}, baseUrl={baseUrl})");
                OpenUrlInBrowser(url);
            }
            else
            {
                FileLog.Write($"[MainWindow] BtnCockpit_Click: gateway at {baseUrl} returned no Tailscale URL (Tailscale unavailable); opening nothing. cc-director never opens a localhost URL.");
                await new MessageDialog(
                    "Cannot Open Cockpit",
                    "Tailscale is unavailable on this machine, so there is no tailnet URL for the " +
                    "Cockpit. Bring Tailscale up and try again. CC Director never opens a localhost " +
                    "URL because it would only work on this one machine.")
                    .ShowDialog<bool?>(this);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnCockpit_Click FAILED (baseUrl={baseUrl}): {ex.Message}");
            // The "is the Gateway tray app running on THIS machine?" hint only makes sense for
            // the loopback default. For a configured remote gateway the failure is about
            // reachability (the remote gateway is down, or the tailnet is unreachable).
            var hint = CockpitUrlResolver.IsLocalhostDefault(baseUrl)
                ? "\n\nIs the Gateway tray app (cc-director-gateway) running on this machine?"
                : "\n\nIs the Gateway running on that machine and reachable over your tailnet?";
            await new MessageDialog(
                "Cannot Open Cockpit",
                $"Could not reach the gateway at {baseUrl}: {ex.Message}{hint}")
                .ShowDialog<bool?>(this);
        }
    }

    private static void OpenUrlInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OpenUrlInBrowser FAILED for {url}: {ex.Message}");
        }
    }

    // Top bar accent: sidebar-colored when idle, blue when a session is active.
    private static readonly IBrush TopBarIdleBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush TopBarActiveBrush = new SolidColorBrush(Color.Parse("#007ACC"));

    // Show or hide the per-session identity block in the unified top bar. The bar
    // itself is always visible (so the global tools can never be occluded); only the
    // identity content and the bar's accent color change with the active session.
    private void SetSessionHeaderVisible(bool visible)
    {
        SessionHeaderBanner.IsVisible = visible;
        TopBar.Background = visible ? TopBarActiveBrush : TopBarIdleBrush;
    }

    private void UpdateSessionHeader()
    {
        if (_activeSession == null) return;

        var session = _activeSession.Session;
        HeaderSessionName.Text = _activeSession.DisplayName;
        HeaderActivityLabel.Text = _activeSession.ActivityLabel;

        // GitHub Actions remote sessions get a links row (repo slug + thread + Actions).
        if (session.IsRemote)
        {
            HeaderRemoteLinks.IsVisible = true;
            HeaderRemoteRepo.Text = session.RemoteRepo ?? "";
            // "Open thread" is only useful once the thread exists; the run links are in
            // the streamed buffer too, but the Actions button is always reachable.
            BtnOpenRemoteThread.IsEnabled = !string.IsNullOrEmpty(session.RemoteThreadUrl);
        }
        else
        {
            HeaderRemoteLinks.IsVisible = false;
        }

        // Message count
        var msgCount = session.ClaudeMetadata?.MessageCount ?? 0;
        if (msgCount > 0)
        {
            HeaderMessageCountText.Text = $"{msgCount} msgs";
            HeaderMessageCountBadge.IsVisible = true;
        }
        else
        {
            HeaderMessageCountBadge.IsVisible = false;
        }


        UpdateHeaderVerification(_activeSession);
    }

    private static readonly ISolidColorBrush VerifiedBadgeBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly ISolidColorBrush WarningBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private void UpdateHeaderVerification(SessionViewModel vm)
    {
    }

    private void CheckTerminalVerification()
    {
        if (_activeSession == null) return;

        var status = _activeSession.TerminalVerificationStatus;
        if (status == TerminalVerificationStatus.Matched)
            return;

        var session = _activeSession;
        var terminalText = TerminalHost.GetAllTerminalText();
        if (string.IsNullOrEmpty(terminalText)) return;

        var lineCount = terminalText.Split('\n').Length;
        if (lineCount < 5) return;

        FileLog.Write($"[MainWindow] CheckTerminalVerification: contentLines={lineCount}, status={status}, session={session.Session.Id}");

        Task.Run(() =>
        {
            try
            {
                var result = session.Session.VerifyWithTerminalContent(terminalText, lineCount);

                Dispatcher.UIThread.Post(() =>
                {
                    if (result.IsMatched)
                    {
                        FileLog.Write($"[MainWindow] Terminal verification CONFIRMED: {result.MatchedSessionId} for {session.Session.Id}");
                        if (!string.IsNullOrEmpty(result.MatchedSessionId))
                            _sessionManager.RegisterClaudeSession(result.MatchedSessionId, session.Session.Id);
                        if (_activeSession?.Session.Id == session.Session.Id)
                            UpdateSessionHeader();
                        PersistSessionState();
                    }
                    else if (result.IsPotential)
                    {
                        FileLog.Write($"[MainWindow] Terminal verification POTENTIAL: {result.MatchedSessionId} for {session.Session.Id} ({lineCount} lines)");
                        if (!string.IsNullOrEmpty(result.MatchedSessionId))
                            _sessionManager.RegisterClaudeSession(result.MatchedSessionId, session.Session.Id);
                        if (_activeSession?.Session.Id == session.Session.Id)
                            UpdateSessionHeader();
                        PersistSessionState();
                    }
                    else
                    {
                        FileLog.Write($"[MainWindow] Terminal verification no match: {result.ErrorMessage} for {session.Session.Id} ({lineCount} lines)");
                    }
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] CheckTerminalVerification FAILED: {ex.Message}");
            }
        });
    }

    private async void BtnRelink_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRelink_Click");
        if (_activeSession == null) return;
        await ShowRelinkDialog(_activeSession);
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+H from anywhere on the window opens the Speak dialog -- but only
        // when the prompt bar is visible (Terminal tab + active session). Other
        // tabs/states don't have a Speak target.
        if (e.Key == Key.H && e.KeyModifiers == KeyModifiers.Control)
        {
            if (!PromptBarBorder.IsVisible)
            {
                FileLog.Write("[MainWindow] Ctrl+H ignored: prompt bar not visible");
                return;
            }
            FileLog.Write("[MainWindow] Ctrl+H -> BtnSpeak_Click");
            e.Handled = true;
            BtnSpeak_Click(this, new RoutedEventArgs());
        }
    }

    // Explain: pop a small modal that asks the Wingman to read the active session's
    // terminal and explain, in plain language, what happened and what the agent wants.
    // The dialog runs the same read-only briefing the FIFO conveyor uses
    // (WingmanService.BriefingQuestion over AnswerViaSessionAsync), so honing that one
    // briefing improves both. The dialog owns its own cancellation; it appears at once
    // and the call resolves a few seconds later.
    private async void BtnExplain_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = _activeSession;
            if (vm is null)
            {
                ShowNotification("Select a session first to explain it.");
                return;
            }
            var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
            if (options is null)
            {
                FileLog.Write("[MainWindow] BtnExplain_Click: AgentOptions not available");
                ShowNotification("Explain not available: AgentOptions not loaded.");
                return;
            }
            FileLog.Write($"[MainWindow] BtnExplain_Click: explaining session {vm.Session.Id}");
            var dlg = new global::CcDirector.Avalonia.Controls.ExplainDialog(vm.Session, options);
            await dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnExplain_Click FAILED: {ex.Message}");
            ShowNotification($"Explain failed: {ex.Message}");
        }
    }

    private async void BtnSpeak_Click(object? sender, RoutedEventArgs e)
    {
        // In-process dictation. Opens SpeakDialog which captures audio via
        // NAudio, runs it through the dictation library (OpenAiRealtimeProvider
        // + CleanupOrchestrator) all in this same process, then returns the
        // cleaned transcript which we insert into PromptInput. No browser, no
        // localhost roundtrip.
        try
        {
            var app = global::Avalonia.Application.Current as App;
            var options = app?.SessionManager?.Options;
            if (options is null)
            {
                FileLog.Write("[MainWindow] BtnSpeak_Click: no AgentOptions available");
                ShowNotification("Dictation not available: AgentOptions not loaded.");
                return;
            }
            if (string.IsNullOrWhiteSpace(options.ResolveOpenAiKey()))
            {
                FileLog.Write("[MainWindow] BtnSpeak_Click: no OpenAI key configured");
                ShowNotification("Dictation needs an OPENAI_API_KEY env var or Voice.OpenAiKey in appsettings.json.");
                return;
            }
            FileLog.Write("[MainWindow] BtnSpeak_Click: opening SpeakDialog");
            // Snapshot the caret BEFORE opening the dialog. Focus moves to the
            // dialog, and on some controls CaretIndex can be reset to 0 after
            // focus loss, which would cause inserted text to land at position 0
            // (effectively prepending instead of inserting at the user's caret).
            var existingTextBefore = PromptInput.Text ?? "";
            var caretBefore = PromptInput.CaretIndex;
            if (caretBefore < 0 || caretBefore > existingTextBefore.Length)
                caretBefore = existingTextBefore.Length;
            var dlg = new global::CcDirector.Avalonia.Voice.SpeakDialog(options);
            await dlg.ShowDialog(this);
            var transcript = dlg.ResultText;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                FileLog.Write("[MainWindow] BtnSpeak_Click: dialog returned no text (cancelled or errored)");
                return;
            }
            InsertIntoPromptInputAt(transcript!, caretBefore);
            FileLog.Write($"[MainWindow] BtnSpeak_Click: inserted {transcript!.Length} chars at caret={caretBefore}, shouldSubmit={dlg.ShouldSubmit}");
            if (dlg.ShouldSubmit)
            {
                SendPrompt();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnSpeak_Click FAILED: {ex.Message}");
            ShowNotification($"Dictation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Insert transcript text at the given caret index in PromptInput. Adds
    /// whitespace separators when needed so the new words do not smush
    /// against existing characters. Caller is expected to snapshot the caret
    /// BEFORE any focus change (e.g. before opening a modal dialog), because
    /// CaretIndex on a TextBox that has just lost focus can be 0.
    /// </summary>
    private void InsertIntoPromptInputAt(string text, int caret)
    {
        var existing = PromptInput.Text ?? "";
        if (caret < 0 || caret > existing.Length) caret = existing.Length;
        var prefix = existing[..caret];
        var suffix = existing[caret..];
        var needsSpaceBefore = prefix.Length > 0 && !char.IsWhiteSpace(prefix[^1]);
        var needsSpaceAfter = suffix.Length > 0 && !char.IsWhiteSpace(suffix[0]);
        var insert = (needsSpaceBefore ? " " : "") + text + (needsSpaceAfter ? " " : "");
        PromptInput.Text = prefix + insert + suffix;
        PromptInput.CaretIndex = prefix.Length + insert.Length;
        PromptInput.Focus();
    }

    private void BtnOpenInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeSession is null)
            {
                FileLog.Write("[MainWindow] BtnOpenInBrowser_Click: no active session");
                return;
            }
            var app = global::Avalonia.Application.Current as App;
            var port = app?.ControlApiHost?.Port;
            if (port is null or 0)
            {
                FileLog.Write("[MainWindow] BtnOpenInBrowser_Click: ControlApi port not available");
                ShowNotification("Web view not available: Control API has not started yet.");
                return;
            }
            var url = $"http://127.0.0.1:{port}/sessions/{_activeSession.Session.Id}/view";
            FileLog.Write($"[MainWindow] BtnOpenInBrowser_Click: opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnOpenInBrowser_Click FAILED: {ex.Message}");
            ShowNotification($"Could not open browser: {ex.Message}");
        }
    }

    private void TabBarRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] TabBarRefreshButton_Click");
        RefreshTerminal();
    }

    private void TabBarCaptureButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] TabBarCaptureButton_Click");
        var capturePath = TerminalHost.DumpDiagnosticCapture();
        if (capturePath != null)
        {
            var fileName = System.IO.Path.GetFileName(capturePath);
            HeaderActivityLabel.Text = $"Captured -> {fileName}";
            FileLog.Write($"[MainWindow] TabBarCaptureButton_Click: captured to {capturePath}");
        }
    }

    private void RefreshTerminal()
    {
        if (_activeSession == null) return;

        TerminalHost.ForceRefresh();
        UpdateScrollBar();
        FileLog.Write("[MainWindow] RefreshTerminal: terminal refreshed");
    }

    private void OnTerminalScrollChanged(object? sender, EventArgs e)
    {
        UpdateScrollBar();
        CheckTerminalVerification();
    }

    private void UpdateScrollBar()
    {
        // Read scrollback size, viewport height, and offset from a single
        // atomic snapshot. Avoids the prior bug where three independent
        // property reads could see different intermediate states of the
        // scrollback list while the parser was growing it concurrently.
        var snap = TerminalHost.GetScrollSnapshot();

        // Avalonia's ScrollBar hides its thumb when Maximum == 0. When there
        // is no scrollback yet we still want a visible thumb filling the
        // entire track ("you're viewing everything"), so floor Maximum at 1.
        int maximum = Math.Max(snap.ScrollbackCount, 1);

        _updatingScrollBar = true;
        TerminalScrollBar.Maximum = maximum;
        TerminalScrollBar.ViewportSize = snap.ViewportRows;
        TerminalScrollBar.LargeChange = snap.ViewportRows;
        TerminalScrollBar.SmallChange = 3;
        TerminalScrollBar.Value = maximum - snap.ScrollOffset;
        _updatingScrollBar = false;
    }

    private void TerminalScrollBar_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollBar.ValueProperty) return;
        if (_updatingScrollBar) return;

        _updatingScrollBar = true;
        int offset = (int)(TerminalScrollBar.Maximum - TerminalScrollBar.Value);
        TerminalHost.ScrollOffset = offset;
        _updatingScrollBar = false;
    }

    private async void OnTerminalBrowserLaunchFailed(string message)
    {
        FileLog.Write($"[MainWindow] OnTerminalBrowserLaunchFailed: {message}");
        try
        {
            await MessageBox.ShowAsync(this, "Open in Browser", message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnTerminalBrowserLaunchFailed FAILED: {ex.Message}");
        }
    }

    private void OnTerminalViewFileRequested(string path)
    {
        FileLog.Write($"[MainWindow] OnTerminalViewFileRequested: {path}");
        try
        {
            if (FileExtensions.IsViewable(path) && File.Exists(path))
            {
                OpenDocumentFile(path);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnTerminalViewFileRequested FAILED: {ex.Message}");
        }
    }

    // ==================== EVENT HANDLERS ====================

    private void SessionList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // When an overlay is open, any click on the session list should close it
        // even if the same session is already selected (SelectionChanged won't fire)
        if ((CommsOverlay.IsVisible || ConnectionsOverlay.IsVisible) && _activeSession != null)
            SelectSession(_activeSession);
    }

    private void SessionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionViewModel vm)
            SelectSession(vm);
    }

    // --- Session drag-and-drop reorder ---

    private async void ColorSquare_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not SessionViewModel vm)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        FileLog.Write($"[MainWindow] ColorSquare drag started: {vm.DisplayName}");
        var dataObject = new DataObject();
        dataObject.Set("SessionViewModel", vm.Session.Id.ToString());
        await DragDrop.DoDragDrop(e, dataObject, global::Avalonia.Input.DragDropEffects.Move);
    }

    private void SessionList_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("SessionViewModel"))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SessionList_Drop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("SessionViewModel")) return;

        var draggedIdStr = e.Data.Get("SessionViewModel") as string;
        if (string.IsNullOrEmpty(draggedIdStr) || !Guid.TryParse(draggedIdStr, out var draggedId))
            return;

        var draggedVm = _sessions.FirstOrDefault(s => s.Session.Id == draggedId);
        if (draggedVm == null) return;

        int fromIndex = _sessions.IndexOf(draggedVm);
        if (fromIndex < 0) return;

        // Issue #225: a group moves as ONE unit - the pure GroupReorder.MoveBlock computes
        // the new order (whole group lifted, members keep internal order, never split or
        // land inside another group). GetSessionDropIndex maps the pixel to a raw insert
        // index from the live container geometry.
        var pos = e.GetPosition(SessionList);
        int rawTarget = GetSessionDropIndex(pos);

        var reordered = GroupReorder.MoveBlock(_sessions, vm => vm.GroupId, fromIndex, rawTarget);
        // Apply the new order onto the live ObservableCollection by stable position moves.
        for (int i = 0; i < reordered.Count; i++)
        {
            int cur = _sessions.IndexOf(reordered[i]);
            if (cur != i) _sessions.Move(cur, i);
        }

        FileLog.Write($"[MainWindow] SessionList_Drop: applied group-aware reorder, dragged {draggedVm.DisplayName}");
        SessionList.SelectedItem = draggedVm;
        RecomputeGroupPositions();
        PersistSessionState();
    }

    /// <summary>Recompute first/last group flags (issue #225) so the header + bracket reflow
    /// after any list change. Cheap; safe to call on every CollectionChanged.</summary>
    private void RecomputeGroupPositions()
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            var vm = _sessions[i];
            if (!vm.IsGroupMember) { vm.IsGroupFirst = false; vm.IsGroupLast = false; continue; }
            var gid = vm.GroupId;
            vm.IsGroupFirst = i == 0 || _sessions[i - 1].GroupId != gid;
            vm.IsGroupLast = i == _sessions.Count - 1 || _sessions[i + 1].GroupId != gid;
        }
    }

    private int GetSessionDropIndex(Point pos)
    {
        // Walk list items and find where the drop point falls
        for (int i = 0; i < _sessions.Count; i++)
        {
            var container = SessionList.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), SessionList);
            if (itemPos == null) continue;

            var bounds = container.Bounds;
            double itemTop = itemPos.Value.Y;
            double itemBottom = itemTop + bounds.Height;

            if (pos.Y >= itemTop && pos.Y <= itemBottom)
            {
                bool below = pos.Y > itemTop + bounds.Height / 2;
                return below ? i + 1 : i;
            }
        }

        return _sessions.Count;
    }

    private void BtnNewSession_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnNewSession_Click");
        _ = ShowNewSessionDialog();
    }

    private async Task ShowNewSessionDialog()
    {
        var app = (App)global::Avalonia.Application.Current!;
        var registry = app.RepositoryRegistry;

        var dialog = new NewSessionDialog(registry, app.SessionHistoryStore);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result != true)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: cancelled");
            return;
        }

        // GitHub (Remote) tab: the work runs on a GitHub-hosted runner, not locally.
        if (dialog.RemoteConfig is { } remoteConfig)
        {
            await CreateRemoteSessionAsync(remoteConfig);
            return;
        }

        if (string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: no path selected");
            return;
        }

        var resumeSessionId = dialog.SelectedResumeSessionId;
        var agentKind = dialog.SelectedAgentKind;

        // Build agent arguments. Claude flags don't apply to other agents.
        // When the dialog specifies no Claude flags, pass null so the machine-level
        // configured default command line (Tools page preset + default model, issue #391)
        // applies instead of an empty override.
        string? agentArgs;
        if (agentKind == AgentKind.ClaudeCode)
        {
            var claudeArgs = "";
            if (dialog.EnableRemoteControl)
                claudeArgs = "remote-control ";
            if (dialog.BypassPermissions)
                claudeArgs += "--dangerously-skip-permissions ";
            agentArgs = claudeArgs.Length > 0 ? claudeArgs.Trim() : null;
        }
        else
        {
            agentArgs = null;
        }

        // Build the IAgent. For RawCli, construct it directly from the dialog's custom fields.
        IAgent agent;
        if (agentKind == AgentKind.RawCli)
        {
            var customCmd = dialog.SelectedCustomCommand;
            if (string.IsNullOrWhiteSpace(customCmd))
            {
                FileLog.Write("[MainWindow] ShowNewSessionDialog: RawCli selected but no command; aborting");
                return;
            }
            agent = new RawCliAgent(customCmd, string.IsNullOrWhiteSpace(dialog.SelectedCustomArgs) ? null : dialog.SelectedCustomArgs);
        }
        else
        {
            agent = CreateAgent(agentKind);
        }

        FileLog.Write($"[MainWindow] ShowNewSessionDialog: path={dialog.SelectedPath}, agent={agentKind}, exe={agent.ExecutablePath}, resume={resumeSessionId ?? "null"}, bypassPermissions={dialog.BypassPermissions}, remoteControl={dialog.EnableRemoteControl}, wingmanEnabled={dialog.WingmanEnabled}");

        // Preflight: make sure the chosen agent's CLI actually exists before we try to spawn it.
        // Without this, a missing binary makes CreateProcess fail with a cryptic Win32 error that
        // gets swallowed, so the dialog just closes and "nothing happens". Resolve it up front and
        // tell the user exactly what to fix. For RawCli, the exe is the user-supplied command.
        var agentExe = agent.ExecutablePath;
        if (ExecutableResolver.Resolve(agentExe) is null)
        {
            string errorTitle;
            string errorBody;
            if (agentKind == AgentKind.RawCli)
            {
                errorTitle = "Command not found";
                errorBody = $"CC Director could not find '{agentExe}' on PATH.\n\n"
                    + "Make sure the command is installed and on your PATH, or supply an absolute path.";
            }
            else
            {
                var (agentName, installHint) = AgentInstallInfo(agentKind);
                errorTitle = $"{agentName} is not installed";
                errorBody = $"CC Director could not start a {agentName} session because its command line tool "
                    + $"could not be found.\n\nLooked for: {agentExe}\n\n{installHint}\n\n"
                    + "If it is installed in a non-standard location, set its path in config.json.";
            }
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: agent {agentKind} executable '{agentExe}' not found on PATH; aborting launch");
            await MessageBox.ShowAsync(this, errorTitle, errorBody);
            return;
        }

        // Group session (issue #225): create all members together, tied by one GroupId,
        // in the group's fixed member order. The per-session Type picker is ignored - each
        // member's type comes from the group definition.
        if (dialog.SelectedGroupDefinition is { } groupDef)
        {
            await CreateGroupSessionAsync(groupDef, dialog.SelectedPath, agentArgs, agentKind);
            return;
        }

        var vm = CreateSession(dialog.SelectedPath, resumeSessionId, agentArgs, agent, dialog.SelectedSessionType);
        if (vm == null)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: CreateSession returned null; showing failure dialog");
            await MessageBox.ShowAsync(this,
                "Could not start session",
                "CC Director could not start the session.\n\n"
                + (_lastSessionCreateError ?? "See the Director log for details."));
            return;
        }

        // Apply the per-session Wingman opt-in chosen in the dialog. Default in the checkbox
        // is true (matches Session.WingmanEnabled's default); the dialog can flip it off so
        // the session boots as plain-terminal with no auto-explain and no Voice/Wingman tabs.
        vm.Session.WingmanEnabled = dialog.WingmanEnabled;

        // Track last used time for repository sorting
        registry?.MarkUsed(dialog.SelectedPath);

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: resume path - looking up history for claude={resumeSessionId}");
            var historyEntry = app.SessionHistoryStore.FindByClaudeSessionId(resumeSessionId);
            if (historyEntry != null)
            {
                vm.Session.CustomName = historyEntry.CustomName;
                vm.Session.CustomColor = historyEntry.CustomColor;
                vm.Session.HistoryEntryId = historyEntry.Id;
                vm.NotifyDisplayChanged();
                historyEntry.LastUsedAt = DateTimeOffset.UtcNow;
                app.SessionHistoryStore.Save(historyEntry);
                FileLog.Write($"[MainWindow] ShowNewSessionDialog: resumed with history entry {historyEntry.Id}, name={historyEntry.CustomName}");
            }
            else
            {
                FileLog.Write("[MainWindow] ShowNewSessionDialog: no history entry found, showing rename dialog");
                ShowRenameDialog(vm);
                SaveSessionToHistory(vm);
            }
        }
        else
        {
            // New session: show rename dialog, create history entry, capture startup text
            FileLog.Write("[MainWindow] ShowNewSessionDialog: new session - showing rename dialog");
            ShowRenameDialog(vm);
            SaveSessionToHistory(vm);
            _ = CaptureStartupTextAsync(vm.Session);
        }

        // If started from a handover, inject the handover prompt after session is ready
        if (!string.IsNullOrEmpty(dialog.SelectedHandoverPath))
        {
            _ = InjectHandoverPromptAsync(vm.Session, dialog.SelectedHandoverPath);
        }

        PersistSessionState();
        FileLog.Write("[MainWindow] ShowNewSessionDialog: complete");
    }

    // Builds the window menu bar (File / Session / View / Tools / Help). Rendered
    // in-window by the NativeMenuBar on Windows/Linux and lifted into the system
    // menu bar on macOS. Replaces the old scattered entry points (sidebar hamburger,
    // New Session caret, top-bar More/Settings/? cluster). Each leaf reuses the
    // existing click handlers / dialog logic so behavior is unchanged.
    private void BuildNativeMenu()
    {
        FileLog.Write("[MainWindow] BuildNativeMenu");

        NativeMenuItem Item(string header, Action onClick, KeyGesture? gesture = null)
        {
            var mi = new NativeMenuItem(header);
            mi.Click += (_, _) => onClick();
            if (gesture != null) mi.Gesture = gesture;
            return mi;
        }

        App AppRef() => global::Avalonia.Application.Current as App
            ?? throw new InvalidOperationException("Application.Current is not the CC Director App");

        var menu = new NativeMenu();

        // ===== File =====
        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu.Items.Add(Item("New Session", () => BtnNewSession_Click(this, new RoutedEventArgs()),
            new KeyGesture(Key.N, KeyModifiers.Control)));
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Save Workspace...", async () =>
        {
            var app = AppRef();
            var sessionData = _sessions.Select(vm => new SessionData(
                vm.DisplayName, vm.Session.RepoPath, vm.Session.CustomName,
                vm.Session.CustomColor, vm.Session.ClaudeArgs));
            var dialog = new SaveWorkspaceDialog(app.WorkspaceStore, sessionData);
            await dialog.ShowDialog<bool?>(this);
        }));
        file.Menu.Items.Add(Item("Load Workspace...", async () =>
        {
            var dialog = new LoadWorkspaceDialog(AppRef().WorkspaceStore);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.SelectedWorkspace != null)
            {
                if (_sessions.Count > 0) await CloseAllSessionsAsync();
                await LoadWorkspaceAsync(dialog.SelectedWorkspace);
            }
        }));
        file.Menu.Items.Add(Item("Clear Workspace", async () =>
        {
            if (_sessions.Count == 0) return;
            await CloseAllSessionsAsync();
        }));
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Open Sessions File", () =>
        {
            var filePath = AppRef().SessionStateStore.FilePath;
            if (File.Exists(filePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                    { UseShellExecute = true });
            else
                ShowNotification($"Sessions file not found: {filePath}");
        }));
        file.Menu.Items.Add(Item("Open History Folder", () =>
        {
            var folder = AppRef().SessionHistoryStore.FolderPath;
            if (Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = folder, UseShellExecute = true });
            else
                ShowNotification($"History folder not found: {folder}");
        }));
        file.Menu.Items.Add(Item("History in VS Code", () =>
        {
            var folder = AppRef().SessionHistoryStore.FolderPath;
            if (Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("code", $"\"{folder}\"")
                    { UseShellExecute = true });
            else
                ShowNotification($"History folder not found: {folder}");
        }));
        file.Menu.Items.Add(Item("Open Logs", () =>
        {
            var logDir = Path.GetDirectoryName(FileLog.CurrentLogPath);
            if (logDir != null && Directory.Exists(logDir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = logDir, UseShellExecute = true });
        }));
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Exit", Close));
        menu.Items.Add(file);

        // ===== Session =====
        var session = new NativeMenuItem("Session") { Menu = new NativeMenu() };
        if (AlphaMode.IsEnabled) // Start FIFO is an alpha feature
            session.Menu.Items.Add(Item("Start FIFO", () => BtnFifo_Click(this, new RoutedEventArgs())));
        session.Menu.Items.Add(new NativeMenuItemSeparator());
        session.Menu.Items.Add(Item("Repositories...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Repositories");
            var dialog = new RepositoryManagerDialog(AppRef().RootDirectoryStore);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.LaunchSessionPath != null)
            {
                var vm = CreateSession(dialog.LaunchSessionPath);
                if (vm != null)
                {
                    ShowRenameDialog(vm);
                    SaveSessionToHistory(vm);
                    SwitchLeftTab("Terminal");
                }
            }
        }));
        session.Menu.Items.Add(Item("Accounts...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Accounts");
            var dialog = new AccountsDialog(AppRef().ClaudeAccountStore);
            await dialog.ShowDialog<bool?>(this);
        }));
        session.Menu.Items.Add(new NativeMenuItemSeparator());
        session.Menu.Items.Add(Item("Show Reviews", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Show Reviews");
            var dialog = new TurnReviewDialog();
            await dialog.ShowDialog(this);
        }));
        menu.Items.Add(session);

        // ===== View =====
        var view = new NativeMenuItem("View") { Menu = new NativeMenu() };
        view.Menu.Items.Add(Item("Toggle Right Panel", () => RightPanelToggle_Click(this, new RoutedEventArgs())));
        view.Menu.Items.Add(Item("Reset Terminal View", () => TabBarRefreshButton_Click(this, new RoutedEventArgs())));
        menu.Items.Add(view);

        // ===== Tools =====
        var tools = new NativeMenuItem("Tools") { Menu = new NativeMenu() };
        tools.Menu.Items.Add(Item("Communications", () => BtnComms_Click(this, new RoutedEventArgs())));
        tools.Menu.Items.Add(Item("Connections", () => BtnConnections_Click(this, new RoutedEventArgs())));
        tools.Menu.Items.Add(Item("Scheduler", () => BtnScheduler_Click(this, new RoutedEventArgs())));
        tools.Menu.Items.Add(new NativeMenuItemSeparator());
        tools.Menu.Items.Add(Item("Claude View...", () => BtnClaudeView_Click(this, new RoutedEventArgs())));
        tools.Menu.Items.Add(Item("MCP Servers...", () => BtnMcpServers_Click(this, new RoutedEventArgs())));
        tools.Menu.Items.Add(Item("Agent Templates...", () => BtnAgentTemplates_Click(this, new RoutedEventArgs())));
        tools.Menu.Items.Add(Item("Claude Code Settings...", () => BtnClaudeConfig_Click(this, new RoutedEventArgs())));
        menu.Items.Add(tools);

        // ===== Help =====
        var help = new NativeMenuItem("Help") { Menu = new NativeMenu() };
        help.Menu.Items.Add(Item("About CC Director", () => BtnHelp_Click(this, new RoutedEventArgs())));
        menu.Items.Add(help);

        NativeMenu.SetMenu(this, menu);
    }

    // ==================== TOP APP BAR ====================

    private async void BtnClaudeConfig_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClaudeConfig_Click: opening Claude Code config dialog");
        var repoPath = _activeSession?.Session.RepoPath;
        var dialog = new ClaudeConfigDialog(repoPath);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void BtnClaudeView_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClaudeView_Click");
        var repoPath = _activeSession?.Session.RepoPath;
        var dialog = new ClaudeViewDialog(repoPath);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void BtnMcpServers_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnMcpServers_Click");
        try
        {
            var manager = new McpConfigManager();
            var projectDir = _activeSession?.Session.RepoPath;
            var dialog = new McpServersDialog(manager, projectDir);
            await dialog.ShowDialog<bool?>(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnMcpServers_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnAgentTemplates_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnAgentTemplates_Click");
        try
        {
            var store = new AgentTemplateStore();
            store.Load();
            var dialog = new AgentTemplatesDialog(store);
            dialog.LaunchRequested += (template, repoPath) =>
            {
                FileLog.Write($"[MainWindow] AgentTemplates LaunchRequested: template={template.Name}, repo={repoPath}");
                var args = template.BuildCliArgs();
                var vm = CreateSession(repoPath, claudeArgs: string.IsNullOrWhiteSpace(args) ? null : args);
                if (vm != null)
                    vm.Rename(template.Name, null);
            };
            await dialog.ShowDialog<bool?>(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnAgentTemplates_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnSettings_Click: opening CC Director settings");
        var controlApi = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var dialog = new SettingsDialog(
            controlApi is not null ? controlApi.ReapplyGatewayAsync : null,
            controlApi?.Port ?? 0,
            ReloadScreenshotsPanelAsync);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void BtnHelp_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnHelp_Click");
        var dialog = new HelpDialog();
        await dialog.ShowDialog<bool?>(this);
    }

    // ==================== LEFT TAB SWITCHING ====================

    private string _activeLeftTab = "Terminal";
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static readonly IBrush InactiveTextBrush = new SolidColorBrush(Color.Parse("#888888"));

    private void TerminalTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Terminal");
    }

    private void SourceControlTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("SourceControl");
    }

    private void VoiceTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Voice");
    }

    private void WingmanTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Wingman");
    }

    // The wingman annotation banner is a passive viewer of the structured briefing the
    // ProactiveExplainService stores on the session at each turn-end. No Opus calls are
    // made from this tab; updates arrive via Session.OnCachedExplainChanged.
    //
    // The banner has these sections, each independently visible so a partial briefing still
    // renders cleanly:
    //   - title (headline)
    //   - what Claude wants you to do next (verbatim, highlighted)
    //   - voice preview: the spoken-version field + a Speak-it button
    // The Opus "what happened" paraphrase (CachedExplainWhatHappened / LongDescription) is
    // deliberately NOT rendered here: the verbatim final answer shows in full in the CleanView
    // below, so a paraphrase on top of it only duplicates. Those fields are still generated and
    // consumed by the phone, which has no verbatim answer to show.
    // The whole top section is a testing/QA surface so we can iterate on how good the
    // wingman is at explaining a session.
    private void RenderWingmanCachedExplain(global::CcDirector.Core.Sessions.Session session)
    {
        var headline = session.CachedExplainHeadline?.Trim();
        var whatNext = session.CachedExplainWhatClaudeWants?.Trim();
        var say = session.CachedExplainSay?.Trim();

        // The "what happened" paraphrase fields are intentionally not part of this surface
        // (see the method doc above); the desktop shows the verbatim answer in the CleanView.
        bool hasAny =
            !string.IsNullOrEmpty(headline) ||
            !string.IsNullOrEmpty(whatNext) ||
            !string.IsNullOrEmpty(say);

        WingmanEmptyText.IsVisible = !hasAny;
        WingmanHeaderRow.IsVisible = hasAny;

        // Title: headline if we have it, otherwise hide.
        WingmanTitleText.IsVisible = !string.IsNullOrEmpty(headline);
        WingmanTitleText.Text = headline ?? "";

        // What Claude wants you to do next. The orange box is an URGENT "you must act" callout,
        // so it must only appear when the session is actually red (needs you). When Claude is
        // working (blue) or idle (green) the briefing still fills whatClaudeWants ("...still
        // working; nothing needed" / "nothing pending"), and painting that in the orange action
        // box is a false alarm. Gate on red; the headline carries the working/idle state instead.
        var isRed = string.Equals(session.StatusColor, "red", StringComparison.OrdinalIgnoreCase);
        WingmanWhatNextSection.IsVisible = isRed && !string.IsNullOrEmpty(whatNext);
        WingmanWhatNextText.Text = whatNext ?? "";

        // CLAUDE SAID: the verbatim trust anchor (validated against the terminal). Shown above the
        // synthesized "what Claude wants" line so the user sees Claude's actual words and can catch
        // any drift in the summary. Hidden when no verbatim line was validated.
        var verbatim = session.CachedExplainClaudeVerbatim?.Trim();
        var hasVerbatim = !string.IsNullOrEmpty(verbatim);
        WingmanClaudeSaidLabel.IsVisible = hasVerbatim;
        WingmanClaudeVerbatimText.IsVisible = hasVerbatim;
        WingmanClaudeVerbatimText.Text = verbatim ?? "";

        // Tap-to-answer buttons: one per briefing action (Session.CachedQuickReplies).
        // Clicking sends that literal text to the agent through the normal send path, so
        // the user can answer the ask in one click instead of typing. Rebuilt each render
        // because the set changes per turn; hidden when the briefing offered no short answers.
        RenderWingmanActionButtons(session.CachedQuickReplies);

        // Voice preview + Speak button. The preview is a QA surface, off by default; it only
        // shows when the user has flipped the header toggle AND there is a spoken version.
        WingmanVoiceText.Text = say ?? "";
        WingmanSpeakVoiceButton.IsEnabled = !string.IsNullOrEmpty(say);
        WingmanVoiceSection.IsVisible = _wingmanShowVoicePreview && !string.IsNullOrEmpty(say);

        // Meta row: model + freshness. Updated live by a timer so the age does not freeze
        // between briefings (it used to show a single stale "Xs ago" computed at render time),
        // and so a regeneration in flight reads "refreshing..." instead of a stale age.
        if (hasAny)
        {
            UpdateWingmanMetaText(session);
            EnsureWingmanFreshnessTimer();
        }
        else
        {
            WingmanMetaText.Text = "";
        }
    }

    // Ticks every 2s while the Wingman tab is active, refreshing only the small meta line.
    // Lazily created on first render; cheap (one text update, no full re-render).
    // Per-session status-color subscriptions so the needs-you count updates the instant any
    // session flips red, not just on the 15s timer. Keyed by VM so we can unsubscribe on remove.
    private readonly Dictionary<SessionViewModel, Action<string, string, string>> _needsYouHandlers = new();

    private void OnSessionsCollectionChanged(object? sender, global::System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == global::System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (var kv in _needsYouHandlers) kv.Key.Session.OnStatusColorChanged -= kv.Value;
            _needsYouHandlers.Clear();
            foreach (var vm in _sessions) SubscribeNeedsYou(vm);
        }
        else
        {
            if (e.OldItems != null)
                foreach (SessionViewModel vm in e.OldItems)
                    if (_needsYouHandlers.TryGetValue(vm, out var h)) { vm.Session.OnStatusColorChanged -= h; _needsYouHandlers.Remove(vm); }
            if (e.NewItems != null)
                foreach (SessionViewModel vm in e.NewItems) SubscribeNeedsYou(vm);
        }
        UpdateNeedsYouCount();
        // The home page is the zero-sessions screen: show/hide it as the count crosses zero.
        UpdateHomeVisibility();
    }

    private void SubscribeNeedsYou(SessionViewModel vm)
    {
        if (_needsYouHandlers.ContainsKey(vm)) return;
        Action<string, string, string> h = (_, _, _) => Dispatcher.UIThread.Post(UpdateNeedsYouCount);
        _needsYouHandlers[vm] = h;
        vm.Session.OnStatusColorChanged += h;
    }

    // Count of sessions that need you (red) shown beside the SESSIONS header, so you get a
    // top-level "is anything waiting on me?" signal without scanning the list. Hidden at zero.
    private void UpdateNeedsYouCount()
    {
        var n = _sessions.Count(s => string.Equals(s.Session.StatusColor, "red", StringComparison.OrdinalIgnoreCase));
        SessionsNeedYouText.Text = n > 0 ? $"{n} need you" : "";
        SessionsNeedYouText.IsVisible = n > 0;
    }

    private global::Avalonia.Threading.DispatcherTimer? _wingmanFreshnessTimer;

    // Desktop Wingman tab: the Gateway turn brief (the rich per-turn brief the warm brain stamps,
    // same content the Cockpit shows) is the PREFERRED source when the Director is gateway-connected
    // and a brief exists; otherwise the local explain. These track the current source so the local
    // explain event + freshness timer never clobber a shown gateway brief, and throttle the poll.
    private bool _wingmanShowingGatewayBrief;
    private DateTime _lastWingmanGatewayFetchUtc = DateTime.MinValue;

    private void EnsureWingmanFreshnessTimer()
    {
        if (_wingmanFreshnessTimer is not null) return;
        _wingmanFreshnessTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _wingmanFreshnessTimer.Tick += (_, _) =>
        {
            if (_activeLeftTab != "Wingman" || _activeSession is null) return;
            var s = _activeSession.Session;
            // Poll the Gateway for a fresh turn brief (throttled internally to ~3s). Preferred
            // source when present; falls back to the local explain when absent.
            RefreshWingmanTabAsync(s, force: false);
            // While the LOCAL explain is the shown source, self-correct the orange box visibility
            // as the session flips red<->working<->idle (the status color can change without a new
            // briefing). When a gateway brief is shown, its own render owns that gating.
            if (!_wingmanShowingGatewayBrief)
            {
                var isRed = string.Equals(s.StatusColor, "red", StringComparison.OrdinalIgnoreCase);
                WingmanWhatNextSection.IsVisible = isRed && !string.IsNullOrEmpty(s.CachedExplainWhatClaudeWants);
                if (string.IsNullOrEmpty(s.CachedExplainModel) && s.CachedExplainAt is null) return;
                UpdateWingmanMetaText(s);
            }
        };
        _wingmanFreshnessTimer.Start();
    }

    // model + live freshness. "refreshing..." while the ProactiveExplainService is mid-flight
    // (IsExplaining), otherwise a human age (Xs / Xm / Xh ago) that ticks on its own.
    private void UpdateWingmanMetaText(global::CcDirector.Core.Sessions.Session session)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(session.CachedExplainModel))
            parts.Add(session.CachedExplainModel!);
        if (session.IsExplaining)
            parts.Add("refreshing...");
        else if (session.CachedExplainAt is { } at)
            parts.Add(RelativeTime.Ago(DateTime.UtcNow - at));
        WingmanMetaText.Text = string.Join("  ", parts);
    }

    // Build one tap-to-answer button per briefing action. Each sends its own literal text
    // to the agent. The buttons are styled to read as answers to the orange "what Claude
    // wants" box they sit inside (amber fill on the action accent color).
    private void RenderWingmanActionButtons(global::System.Collections.Generic.IReadOnlyList<string> actions)
    {
        WingmanActionsPanel.Children.Clear();

        var shown = 0;
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action)) continue;
            var replyText = action.Trim();
            var button = new global::Avalonia.Controls.Button
            {
                Content = replyText,
                Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xD9, 0x77, 0x06)),
                Foreground = global::Avalonia.Media.Brushes.White,
                BorderThickness = new global::Avalonia.Thickness(0),
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(12, 6),
                Margin = new global::Avalonia.Thickness(0, 0, 8, 8),
                FontSize = 13,
                FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            button.Click += (_, _) => SendWingmanActionReply(replyText);
            WingmanActionsPanel.Children.Add(button);
            shown++;
        }

        WingmanActionsPanel.IsVisible = shown > 0;
    }

    // Send a tap-to-answer reply through the same path as the Wingman input box, so the
    // prompt + reply render in the Clean view the Wingman tab hosts.
    private void SendWingmanActionReply(string text)
    {
        if (_activeSession is null || string.IsNullOrWhiteSpace(text)) return;
        // Disable all action buttons the instant one is clicked. The box lingers until the
        // session flips to Working (re-gated by the 2s timer), so without this a rapid second
        // click would send a duplicate answer to the agent. Buttons are rebuilt (enabled) on the
        // next briefing. Also gives an immediate "sent" affordance.
        foreach (var child in WingmanActionsPanel.Children)
            if (child is global::Avalonia.Controls.Button b) b.IsEnabled = false;
        FileLog.Write($"[MainWindow] SendWingmanActionReply: sid={_activeSession.Session.Id}, text=\"{text}\"");
        PromptInput.Text = text;
        SendPrompt();
    }

    // ==================== GATEWAY TURN BRIEF (preferred Wingman-tab source) ====================

    /// <summary>
    /// Fetch the latest Gateway turn brief for the session and render it (preferred over the local
    /// explain), else leave/show the local explain. Throttled to ~3s unless forced, so the 2s
    /// freshness timer can call it every tick. Best-effort: a null brief (no gateway configured,
    /// none stamped yet, or unreachable) keeps the local explain shown - a source preference, not
    /// a silent failure.
    /// </summary>
    private async void RefreshWingmanTabAsync(global::CcDirector.Core.Sessions.Session session, bool force)
    {
        var sid = session.Id;
        if (!force && (DateTime.UtcNow - _lastWingmanGatewayFetchUtc) < TimeSpan.FromSeconds(3))
            return;
        _lastWingmanGatewayFetchUtc = DateTime.UtcNow;

        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        global::CcDirector.Gateway.Contracts.TurnBriefDto? brief = null;
        if (host is not null)
        {
            try { brief = await host.GetLatestTurnBriefAsync(sid.ToString()); }
            catch (Exception ex) { FileLog.Write($"[MainWindow] RefreshWingmanTabAsync FAILED: {ex.Message}"); }
        }

        // Stale guard: the active session or tab may have changed while awaiting the network call.
        if (_activeSession is null || _activeSession.Session.Id != sid || _activeLeftTab != "Wingman") return;

        if (brief is not null && !brief.Degraded)
        {
            _wingmanShowingGatewayBrief = true;
            RenderWingmanTurnBrief(brief);
        }
        else if (_wingmanShowingGatewayBrief)
        {
            // A brief was showing but is now gone (session ended / gateway down): drop back to the
            // local explain rather than leave stale content on screen.
            _wingmanShowingGatewayBrief = false;
            RenderWingmanCachedExplain(session);
        }
    }

    /// <summary>
    /// Render a Gateway turn brief into the EXISTING Wingman-tab controls - Claude's verbatim words
    /// first (CLAUDE SAID), the statement in the orange "what next" box, tap-to-answer option
    /// buttons, and "if you do nothing". The richer sibling of <see cref="RenderWingmanCachedExplain"/>.
    /// </summary>
    private void RenderWingmanTurnBrief(global::CcDirector.Gateway.Contracts.TurnBriefDto brief)
    {
        var ny = brief.NeedsYou;

        WingmanEmptyText.IsVisible = false;
        WingmanHeaderRow.IsVisible = true;

        // Title = chapter headline (fallback intent, then the all-clear line when nothing is needed).
        var title = !string.IsNullOrWhiteSpace(brief.Headline) ? brief.Headline.Trim()
                  : !string.IsNullOrWhiteSpace(brief.Intent) ? brief.Intent.Trim()
                  : (ny is null ? (brief.AllClear ?? "").Trim() : "");
        WingmanTitleText.IsVisible = !string.IsNullOrEmpty(title);
        WingmanTitleText.Text = title;

        // CLAUDE SAID - the verbatim trust anchor (NeedsYou.Evidence), shown above the statement.
        var verbatim = ny?.Evidence?.Trim();
        var hasVerbatim = !string.IsNullOrEmpty(verbatim);
        WingmanClaudeSaidLabel.IsVisible = hasVerbatim;
        WingmanClaudeVerbatimText.IsVisible = hasVerbatim;
        WingmanClaudeVerbatimText.Text = verbatim ?? "";

        // Statement + the orange "what Claude wants" box: shown whenever there is a needs-you. The
        // gateway brief only carries a NeedsYou when the warm brain decided something is needed, so
        // (unlike the local explain) it does not need a red-status gate to avoid false alarms.
        var hasAsk = ny is not null && !string.IsNullOrWhiteSpace(ny.Statement);
        WingmanWhatNextSection.IsVisible = hasAsk;
        WingmanWhatNextText.Text = ny?.Statement ?? "";

        // Tap-to-answer options.
        if (ny is not null && ny.Options.Count > 0) RenderWingmanBriefOptions(ny);
        else { WingmanActionsPanel.Children.Clear(); WingmanActionsPanel.IsVisible = false; }

        // "If you do nothing" - the single most triage-relevant fact.
        var ifIgnored = ny?.IfIgnored?.Trim();
        WingmanIfIgnoredText.IsVisible = !string.IsNullOrEmpty(ifIgnored);
        WingmanIfIgnoredText.Text = string.IsNullOrEmpty(ifIgnored) ? "" : $"If you do nothing: {ifIgnored}";

        // The gateway brief has no spoken field; the QA voice preview is a local-explain surface.
        WingmanVoiceSection.IsVisible = false;

        // Meta: model + turn + freshness.
        WingmanMetaText.Text = $"{brief.Model}  turn {brief.TurnNumber}  {RelativeTime.Ago(DateTime.UtcNow - brief.GeneratedAtUtc)}";
    }

    /// <summary>
    /// Build one tap-to-answer button per brief option. The recommended option is spelled out
    /// "(recommended)" (never "(rec)") and outlined green; the note becomes the tooltip. Clicking
    /// sends the option through the appropriate path (raw keys vs reply) via <see cref="SendWingmanBriefOption"/>.
    /// </summary>
    private void RenderWingmanBriefOptions(global::CcDirector.Gateway.Contracts.TurnBriefNeedsYou ny)
    {
        WingmanActionsPanel.Children.Clear();
        var shown = 0;
        foreach (var o in ny.Options)
        {
            if (o is null || string.IsNullOrEmpty(o.Send)) continue;
            var button = new global::Avalonia.Controls.Button
            {
                Content = o.Key + (o.Recommended ? " (recommended)" : ""),
                Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xD9, 0x77, 0x06)),
                Foreground = global::Avalonia.Media.Brushes.White,
                BorderThickness = o.Recommended ? new global::Avalonia.Thickness(2) : new global::Avalonia.Thickness(0),
                BorderBrush = o.Recommended ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x22, 0xC5, 0x5E)) : null,
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(12, 6),
                Margin = new global::Avalonia.Thickness(0, 0, 8, 8),
                FontSize = 13,
                FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            if (!string.IsNullOrWhiteSpace(o.Note))
                global::Avalonia.Controls.ToolTip.SetTip(button, o.Note);
            var opt = o;
            var answerVia = ny.AnswerVia;
            button.Click += (_, _) => SendWingmanBriefOption(opt, answerVia);
            WingmanActionsPanel.Children.Add(button);
            shown++;
        }
        WingmanActionsPanel.IsVisible = shown > 0;
    }

    /// <summary>
    /// Send a brief option to the active session. A "keys" answer (on-screen menu) carries a raw
    /// key sequence (e.g. "1\r") and is sent literally; a reply answer is routed through the
    /// composer path so it renders in the Clean view with a trailing Enter.
    /// </summary>
    private async void SendWingmanBriefOption(global::CcDirector.Gateway.Contracts.TurnBriefOption o, string answerVia)
    {
        if (_activeSession is null || string.IsNullOrEmpty(o.Send)) return;
        foreach (var child in WingmanActionsPanel.Children)
            if (child is global::Avalonia.Controls.Button b) b.IsEnabled = false;
        FileLog.Write($"[MainWindow] SendWingmanBriefOption: sid={_activeSession.Session.Id}, key=\"{o.Key}\", answerVia={answerVia}");
        if (string.Equals(answerVia, "keys", StringComparison.OrdinalIgnoreCase))
        {
            try { await _activeSession.Session.SendTextAsync(o.Send); }
            catch (Exception ex) { FileLog.Write($"[MainWindow] SendWingmanBriefOption keys FAILED: {ex.Message}"); }
        }
        else
        {
            SendWingmanActionReply(o.Send);
        }
    }

    // Whether the QA voice-preview box is shown. Off by default to keep the tab clean;
    // toggled by the header ToggleButton. Persists across re-renders within the session.
    private bool _wingmanShowVoicePreview;

    private void WingmanVoicePreviewToggle_Changed(object? sender, RoutedEventArgs e)
    {
        _wingmanShowVoicePreview = WingmanVoicePreviewToggle.IsChecked == true;
        if (_activeSession is not null)
            RenderWingmanCachedExplain(_activeSession.Session);
    }

    // Play the spoken-version field aloud through TTS. Opens a modal SpeakPlaybackDialog
    // with a Stop button instead of firing playback in the background: the modal blocks
    // this button while audio plays (no stacking repeated clicks) and gives the user an
    // explicit way to stop listening. Uses the same DesktopTtsPlayer the Voice tab uses;
    // lazily initialised on first click since the Voice tab may not have been opened yet.
    private async void WingmanSpeakVoiceButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeSession is null) return;
            var say = _activeSession.Session.CachedExplainSay?.Trim();
            if (string.IsNullOrEmpty(say)) return;

            if (_ttsPlayer is null)
            {
                var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
                if (options is null)
                {
                    ShowNotification("TTS not available: SessionManager not initialised.");
                    return;
                }
                _ttsPlayer = new global::CcDirector.Avalonia.Voice.DesktopTtsPlayer(options);
            }

            var dlg = new global::CcDirector.Avalonia.Voice.SpeakPlaybackDialog(_ttsPlayer, say);
            await dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] WingmanSpeakVoiceButton FAILED: {ex.Message}");
            ShowNotification($"Play failed: {ex.Message}");
        }
    }

    // Wingman tab "Send": route the typed/dictated text to the agent through the
    // normal send path so the prompt + reply render in the Clean view that the
    // Wingman tab hosts.
    private void WingmanSendButton_Click(object? sender, RoutedEventArgs e) => WingmanSend();

    private void WingmanSend()
    {
        if (_activeSession is null || string.IsNullOrWhiteSpace(WingmanInput.Text)) return;
        PromptInput.Text = WingmanInput.Text;
        WingmanInput.Text = "";
        SendPrompt();
    }

    // Wingman tab "Speak": dictate into the Wingman input box via the same in-process
    // SpeakDialog the Terminal tab's Speak button uses. If the user finishes with Send
    // in the dialog, fire the send immediately.
    private async void WingmanSpeakButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
            if (options is null || string.IsNullOrWhiteSpace(options.ResolveOpenAiKey()))
            {
                ShowNotification("Dictation needs an OPENAI_API_KEY env var or Voice.OpenAiKey in appsettings.json.");
                return;
            }
            var dlg = new global::CcDirector.Avalonia.Voice.SpeakDialog(options);
            await dlg.ShowDialog(this);
            var transcript = dlg.ResultText;
            if (string.IsNullOrWhiteSpace(transcript)) return;
            var existing = WingmanInput.Text ?? "";
            WingmanInput.Text = string.IsNullOrEmpty(existing) ? transcript! : existing + " " + transcript!;
            if (dlg.ShouldSubmit) WingmanSend();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] WingmanSpeakButton FAILED: {ex.Message}");
            ShowNotification($"Dictation failed: {ex.Message}");
        }
    }

    // Lazily wire the Voice tab the first time it is shown: it needs AgentOptions
    // (for the in-process dictation engine), which are not available until the
    // SessionManager is constructed.
    private bool _voiceInitialized;
    private global::CcDirector.Avalonia.Voice.DesktopTtsPlayer? _ttsPlayer;

    private void EnsureVoiceInitialized()
    {
        if (_voiceInitialized) return;
        var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
        if (options is null) return;
        VoiceView.Initialize(options);
        VoiceView.AskAgentRequested += OnVoiceAskAgent;
        VoiceView.AskWingmanRequested += OnVoiceAskWingman;
        _ttsPlayer = new global::CcDirector.Avalonia.Voice.DesktopTtsPlayer(options);
        _voiceInitialized = true;
        FileLog.Write("[MainWindow] Voice tab initialized");
    }

    // Ask Agent: the dictated transcript goes to the working agent via the same
    // send path the prompt bar uses (slash-command handling, Clean-view inject,
    // Enter-retry). Then we follow the agent's turn to completion and speak its
    // reply aloud, reusing the shared ChatService spoken-summary path the phone uses.
    private async void OnVoiceAskAgent(string transcript)
    {
        var vm = _activeSession;
        if (vm is null)
        {
            VoiceView.SetStatus("No session selected.", "#F44747");
            return;
        }
        FileLog.Write($"[MainWindow] OnVoiceAskAgent: {transcript.Length} chars to {vm.Session.Id}");
        PromptInput.Text = transcript;
        SendPrompt();
        try
        {
            await FollowAgentTurnAndSpeakAsync(vm);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnVoiceAskAgent follow FAILED: {ex.Message}");
            VoiceView.SetStatus("Voice follow error: " + ex.Message, "#F44747");
        }
    }

    // Wait for the agent to finish the turn it just started, then read its reply +
    // ear-friendly spoken summary via the shared ChatService poll path and speak it.
    // We watch the session's ActivityState directly (in-process, no double-send): we
    // first wait for it to ENTER Working so a fast poll cannot read the PREVIOUS
    // reply, then wait for it to leave Working into a stopping state.
    private async Task FollowAgentTurnAndSpeakAsync(SessionViewModel vm)
    {
        var app = global::Avalonia.Application.Current as App;
        var options = app?.SessionManager?.Options;
        var sm = app?.SessionManager;
        if (options is null || sm is null) return;
        var session = vm.Session;

        VoiceView.SetStatus("Agent working...", "#DCDCAA");

        // Phase 1: wait briefly for the turn to actually start.
        var startBy = DateTime.UtcNow.AddSeconds(6);
        while (session.ActivityState != ActivityState.Working
               && session.Status is not (SessionStatus.Exited or SessionStatus.Failed)
               && DateTime.UtcNow < startBy)
        {
            await Task.Delay(250);
        }

        // Phase 2: wait for the turn to finish (cap at 10 minutes).
        var finishBy = DateTime.UtcNow.AddMinutes(10);
        while (session.ActivityState == ActivityState.Working
               && session.Status is not (SessionStatus.Exited or SessionStatus.Failed)
               && DateTime.UtcNow < finishBy)
        {
            await Task.Delay(750);
        }

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
        {
            VoiceView.SetStatus("Session exited.", "#F44747");
            return;
        }

        var chat = new global::CcDirector.ControlApi.Chat.ChatService(sm, options);
        var resp = await chat.HandleAsync(new global::CcDirector.Gateway.Contracts.ChatRequest
        {
            SessionId = session.Id.ToString(),
            PollOnly = true,
            Voice = true,
        });

        var display = !string.IsNullOrWhiteSpace(resp.DisplayText) ? resp.DisplayText : (resp.Reply ?? "");
        if (!string.IsNullOrWhiteSpace(display))
            VoiceView.ShowReply(display);

        var spoken = !string.IsNullOrWhiteSpace(resp.Summary) ? resp.Summary : display;
        if (!string.IsNullOrWhiteSpace(spoken) && _ttsPlayer is not null)
        {
            VoiceView.SetStatus("Speaking...", "#2B6CB0");
            await _ttsPlayer.SpeakAsync(spoken);
        }
        VoiceView.SetStatus("Ready", "#5FD08A");
    }

    // Ask Wingman: answer the spoken question with the in-process WingmanService over
    // the session's full cleaned terminal (read-only, verbatim - the same path the
    // /sessions/{sid}/wingman/ask endpoint uses), show the answer as text, and speak
    // it aloud. No turn-following: the wingman answers immediately.
    private async void OnVoiceAskWingman(string transcript)
    {
        var vm = _activeSession;
        if (vm is null)
        {
            VoiceView.SetStatus("No session selected.", "#F44747");
            return;
        }
        var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
        if (options is null)
        {
            VoiceView.SetStatus("Wingman not available: options not loaded.", "#F44747");
            return;
        }
        try
        {
            FileLog.Write($"[MainWindow] OnVoiceAskWingman: {transcript.Length} chars for {vm.Session.Id}");
            VoiceView.SetStatus("Asking the wingman...", "#2B6CB0");
            var session = vm.Session;
            var bytes = session.Buffer?.DumpAll() ?? Array.Empty<byte>();
            var fullTerminal = global::CcDirector.ControlApi.AnsiCleaner.Clean(bytes);
            var result = await global::CcDirector.Core.Wingman.WingmanService.AnswerViaSessionAsync(
                transcript, fullTerminal, session.AgentKind.ToString(), session.RepoPath, options.ClaudePath);
            var answer = string.IsNullOrWhiteSpace(result.Answer)
                ? "The wingman had nothing to report."
                : result.Answer;
            VoiceView.ShowReply(answer);
            if (_ttsPlayer is not null)
            {
                VoiceView.SetStatus("Speaking...", "#2B6CB0");
                await _ttsPlayer.SpeakAsync(answer);
            }
            VoiceView.SetStatus("Ready", "#5FD08A");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnVoiceAskWingman FAILED: {ex.Message}");
            VoiceView.SetStatus("Wingman error: " + ex.Message, "#F44747");
        }
    }

    private bool _commsInitialized;

    // Launch the full-screen FIFO takeover: step through every session that needs the
    // user, one at a time, with the live terminal + wingman briefing. Modal over the main
    // window so there is nothing else to look at while stepping through.
    private async void BtnFifo_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sm = (global::Avalonia.Application.Current as App)?.SessionManager;
            if (sm is null)
            {
                FileLog.Write("[MainWindow] BtnFifo_Click: SessionManager not available");
                return;
            }
            FileLog.Write("[MainWindow] BtnFifo_Click: opening FIFO window");
            await new FifoWindow(sm).ShowDialog(this);

            // The FIFO window is full-screen, so attaching a session there resized that
            // session's PTY to full-screen dimensions. Re-attach the main window's active
            // session so it re-sends ITS dimensions and redraws cleanly, instead of leaving
            // the session rendering at the FIFO window's size.
            if (_activeSession is not null)
                TerminalHost.Attach(_activeSession.Session);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnFifo_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnComms_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnComms_Click: opening Comms overlay");

        // Close other overlays first
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }
        if (SchedulerOverlay.IsVisible)
        {
            SchedulerOverlay.IsVisible = false;
            if (_schedulerInitialized)
                SchedulerView.StopPolling();
        }

        CommsOverlay.IsVisible = true;
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)

        if (!_commsInitialized)
        {
            _commsInitialized = true;
            await CommManagerView.InitializeAsync();
        }
        CommManagerView.StartPolling();
    }

    private void BtnCommsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnCommsClose_Click: closing Comms overlay");
        CommsOverlay.IsVisible = false;
        if (_commsInitialized)
            CommManagerView.StopPolling();
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private bool _connectionsInitialized;
    private bool _schedulerInitialized;

    private void BtnConnections_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnConnections_Click: opening Connections overlay");

        // Close other overlays first
        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (SchedulerOverlay.IsVisible)
        {
            SchedulerOverlay.IsVisible = false;
            if (_schedulerInitialized)
                SchedulerView.StopPolling();
        }

        ConnectionsOverlay.IsVisible = true;
        _connectionsInitialized = true;
        ConnectionsView.StartPolling();
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)
    }

    private void BtnConnectionsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnConnectionsClose_Click: closing Connections overlay");
        ConnectionsOverlay.IsVisible = false;
        if (_connectionsInitialized)
            ConnectionsView.StopPolling();
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private void BtnScheduler_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnScheduler_Click: opening Scheduler overlay");

        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }

        SchedulerOverlay.IsVisible = true;
        _schedulerInitialized = true;
        SchedulerView.StartPolling();
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)
    }

    private void BtnSchedulerClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnSchedulerClose_Click: closing Scheduler overlay");
        SchedulerOverlay.IsVisible = false;
        if (_schedulerInitialized)
            SchedulerView.StopPolling();
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private void BtnTools_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnTools_Click: opening Tools overlay");

        // Close other overlays first.
        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }
        if (SchedulerOverlay.IsVisible)
        {
            SchedulerOverlay.IsVisible = false;
            if (_schedulerInitialized)
                SchedulerView.StopPolling();
        }

        ToolsOverlay.IsVisible = true;
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)
    }

    private void BtnToolsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnToolsClose_Click: closing Tools overlay");
        ToolsOverlay.IsVisible = false;
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private void SwitchLeftTab(string tab)
    {
        if (_activeLeftTab == tab) return;
        _activeLeftTab = tab;
        FileLog.Write($"[MainWindow] SwitchLeftTab: {tab}");

        var accentBrush = (IBrush)(this.FindResource("AccentBrush") ?? Brushes.DodgerBlue);
        var whiteBrush = Brushes.White;
        bool isDocTab = tab.StartsWith("Doc:", StringComparison.Ordinal);

        // Update fixed tab button styles
        TerminalTabButton.Background = tab == "Terminal" ? accentBrush : TransparentBrush;
        TerminalTabButton.Foreground = tab == "Terminal" ? whiteBrush : InactiveTextBrush;
        SourceControlTabButton.Background = tab == "SourceControl" ? accentBrush : TransparentBrush;
        SourceControlTabButton.Foreground = tab == "SourceControl" ? whiteBrush : InactiveTextBrush;
        VoiceTabButton.Background = tab == "Voice" ? accentBrush : TransparentBrush;
        VoiceTabButton.Foreground = tab == "Voice" ? whiteBrush : InactiveTextBrush;
        WingmanTabButton.Background = tab == "Wingman" ? accentBrush : TransparentBrush;
        WingmanTabButton.Foreground = tab == "Wingman" ? whiteBrush : InactiveTextBrush;
        // Update document tab button styles
        foreach (var docTab in _documentTabs)
        {
            bool isActive = isDocTab && docTab.TabId == tab;
            docTab.TabButton.Background = isActive ? accentBrush : TransparentBrush;
            docTab.TabButton.Foreground = isActive ? whiteBrush : InactiveTextBrush;
        }

        // Show/hide panels
        TerminalPanel.IsVisible = tab == "Terminal";
        SourceControlPanel.IsVisible = tab == "SourceControl";
        VoicePanel.IsVisible = tab == "Voice";
        WingmanPanel.IsVisible = tab == "Wingman";
        DocumentPanel.IsVisible = isDocTab;

        // The shared prompt bar belongs to the terminal-style tabs. The Voice and
        // Wingman tabs have their own input affordances (push-to-talk buttons / a
        // Speak+Send bar), so hide the shared bar there to avoid a duplicate input.
        if (_activeSession != null)
            PromptBarBorder.IsVisible = tab != "Voice" && tab != "Wingman";

        if (tab == "Voice")
        {
            EnsureVoiceInitialized();
            VoiceView.SetSession(_activeSession?.DisplayName);
        }
        else
        {
            // Leaving the Voice tab: cut off any reply still being spoken.
            _ttsPlayer?.Stop();
        }

        // The Wingman tab is a passive viewer of Session.CachedExplainText. Just render
        // whatever is currently cached; OnCachedExplainChanged will keep it fresh.
        if (tab == "Wingman" && _activeSession is not null)
        {
            RenderWingmanCachedExplain(_activeSession.Session);
        }

        // Show refresh button only when Terminal tab is active and a session exists
        TabBarRefreshButton.IsVisible = tab == "Terminal" && _activeSession != null;
        TabBarCaptureButton.IsVisible = tab == "Terminal" && _activeSession != null;

        // Swap document panel content
        if (isDocTab)
        {
            DocumentPanel.Children.Clear();
            var activeDocTab = _documentTabs.FirstOrDefault(d => d.TabId == tab);
            if (activeDocTab != null)
                DocumentPanel.Children.Add(activeDocTab.ViewerControl);
        }

        // Force terminal refresh when switching back to Terminal tab.
        // The terminal display corrupts while hidden (Bounds=0) and needs
        // a full buffer re-parse + ConPTY resize to render correctly.
        if (tab == "Terminal" && _activeSession != null)
        {
            Dispatcher.UIThread.Post(() => TerminalHost.ForceRefresh(), DispatcherPriority.Render);
        }
    }

    private void UpdateSourceControlTabVisibility(string repoPath)
    {
        var gitDir = Path.Combine(repoPath, ".git");
        var hasGit = Directory.Exists(gitDir) || File.Exists(gitDir);
        SourceControlTabButton.IsVisible = hasGit;

        // If Source Control tab was selected but is now hidden, switch to Terminal
        if (!hasGit && _activeLeftTab == "SourceControl")
            SwitchLeftTab("Terminal");

        FileLog.Write($"[MainWindow] UpdateSourceControlTabVisibility: hasGit={hasGit}");
    }

    private void BtnSend_Click(object? sender, RoutedEventArgs e)
    {
        SendPrompt();
    }

    private void PromptInput_KeyDown(object? sender, KeyEventArgs e)
    {
        // Slash command popup navigation
        if (SlashCommandPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (SlashCommandList.SelectedIndex < _filteredSlashCommands.Count - 1)
                        SlashCommandList.SelectedIndex++;
                    if (SlashCommandList.SelectedItem is { } downItem)
                        SlashCommandList.ScrollIntoView(downItem);
                    e.Handled = true;
                    return;

                case Key.Up:
                    if (SlashCommandList.SelectedIndex > 0)
                        SlashCommandList.SelectedIndex--;
                    if (SlashCommandList.SelectedItem is { } upItem)
                        SlashCommandList.ScrollIntoView(upItem);
                    e.Handled = true;
                    return;

                case Key.Tab:
                    InsertSelectedSlashCommand();
                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                    InsertSelectedSlashCommand();
                    e.Handled = true;
                    return;

                case Key.Escape:
                    SlashCommandPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Shift+Enter = Queue prompt
        if (e.Key == Key.Enter && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            QueueCurrentPrompt();
            return;
        }

        // Ctrl+Enter = Send prompt (Enter inserts newline via AcceptsReturn)
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            SendPrompt();
            return;
        }
    }

    // ==================== SLASH COMMAND AUTOCOMPLETE ====================

    private void PromptInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = PromptInput.Text ?? "";

        // Only trigger when / is the first non-whitespace character
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("/"))
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        // Extract the slash command prefix (text from / to first space)
        var afterSlash = trimmed.Substring(1);
        var spaceIndex = afterSlash.IndexOf(' ');
        var filter = spaceIndex >= 0 ? afterSlash.Substring(0, spaceIndex) : afterSlash;

        // If there's a space after the command, popup should close (command is complete)
        if (spaceIndex >= 0)
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        var repoPath = _activeSession?.Session.RepoPath;
        var allCommands = _slashCommandProvider.GetCommands(repoPath);

        // Exclude interactive TUI commands from PromptInput autocomplete -- they don't work here.
        // Users who know these commands can use them in the Terminal tab directly.
        var available = allCommands.Where(c => !InteractiveTuiCommands.Contains(c.Name)).ToList();

        _filteredSlashCommands = string.IsNullOrEmpty(filter)
            ? available
            : available.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_filteredSlashCommands.Count == 0)
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        SlashCommandList.ItemsSource = _filteredSlashCommands;
        SlashCommandList.SelectedIndex = 0;
        SlashCommandPopup.IsOpen = true;
    }

    private void SlashCommandList_Tapped(object? sender, TappedEventArgs e)
    {
        InsertSelectedSlashCommand();
    }

    private void SlashCommandList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SlashCommandList.SelectedItem is not SlashCommandItem selected)
        {
            SlashCommandDocPanel.IsVisible = false;
            return;
        }

        SlashCommandDocTitle.Text = "/" + selected.Name;
        SlashCommandDocSource.Text = selected.Source == "project" ? "Project skill" : "Global skill";
        SlashCommandDocDesc.Text = selected.Description;

        if (!string.IsNullOrWhiteSpace(selected.Documentation))
        {
            SlashCommandDocBody.Text = selected.Documentation;
            SlashCommandDocBody.IsVisible = true;
        }
        else
        {
            SlashCommandDocBody.Text = string.Empty;
            SlashCommandDocBody.IsVisible = false;
        }

        SlashCommandDocPanel.IsVisible = true;
    }

    private void InsertSelectedSlashCommand()
    {
        if (SlashCommandList.SelectedItem is not SlashCommandItem selected)
            return;

        FileLog.Write($"[MainWindow] InsertSelectedSlashCommand: /{selected.Name}");
        PromptInput.Text = "/" + selected.Name + " ";
        PromptInput.CaretIndex = PromptInput.Text.Length;
        SlashCommandPopup.IsOpen = false;
        PromptInput.Focus();
    }

    // ==================== SEND / QUEUE / HANDOVER ====================

    private static readonly HashSet<string> TerminalOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "context", "copy", "diff", "rewind", "checkpoint", "export", "mcp", "agents",
    };

    private bool TryHandleSlashCommand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("/"))
            return false;

        var commandName = text.ToLowerInvariant().TrimStart('/');

        // Terminal-only commands: show redirect message, keep text in prompt
        if (TerminalOnlyCommands.Contains(commandName))
        {
            FileLog.Write($"[MainWindow] TryHandleSlashCommand: terminal-only command blocked: /{commandName}");
            ShowNotification($"Use the Terminal tab for {text}");
            PromptInput.Text = text;
            PromptInput.CaretIndex = text.Length;
            return true;
        }

        // Commands handled by ClaudeConfigDialog (with tab selection)
        var configTab = commandName switch
        {
            "config" or "settings" => "general",
            "permissions" or "allowed-tools" => "permissions",
            "model" => "general",
            "hooks" => "hooks",
            "plugin" => "plugins",
            _ => (string?)null
        };

        if (configTab != null)
        {
            FileLog.Write($"[MainWindow] TryHandleSlashCommand: opening ClaudeConfigDialog tab={configTab} for /{commandName}");
            var dialog = new ClaudeConfigDialog(_activeSession?.Session.RepoPath, configTab);
            _ = dialog.ShowDialog<bool?>(this);
            return true;
        }

        // Commands with their own native dialogs
        switch (commandName)
        {
            case "status":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening StatusDialog");
                _ = new StatusDialog().ShowDialog<bool?>(this);
                return true;

            case "help":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening HelpDialog");
                _ = new HelpDialog().ShowDialog<bool?>(this);
                return true;

            case "theme":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening ThemeDialog");
                _ = new ThemeDialog().ShowDialog<bool?>(this);
                return true;

            case "memory":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening MemoryDialog");
                _ = new MemoryDialog(_activeSession?.Session.RepoPath).ShowDialog<bool?>(this);
                return true;

            case "stats":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening StatsDialog");
                _ = new StatsDialog().ShowDialog<bool?>(this);
                return true;

            case "output-style":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening OutputStyleDialog");
                _ = new OutputStyleDialog().ShowDialog<bool?>(this);
                return true;

            case "resume" or "continue":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening ResumeDialog");
                _ = HandleResumeCommand();
                return true;
        }

        return false;
    }

    private async Task HandleResumeCommand()
    {
        var dialog = new ResumeDialog(_activeSession?.Session.RepoPath);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.SelectedSessionId != null)
        {
            SwitchLeftTab("Terminal");
            PromptInput.Text = $"claude --resume {dialog.SelectedSessionId}";
            ShowNotification("Session selected -- press Enter to resume in Terminal");
        }
    }

    private async void SendPrompt()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text)) return;

        // Strip newlines -- Claude Code prompt expects single-line input
        var text = PromptInput.Text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Intercept slash commands and show native dialogs
        if (TryHandleSlashCommand(text))
        {
            PromptInput.Text = "";
            return;
        }

        FileLog.Write($"[MainWindow] SendPrompt: {text.Length} chars to session {_activeSession.Session.Id}");

        PromptInput.Text = "";

        // Clear saved prompt text so switching away and back shows empty box
        _activeSession.Session.PendingPromptText = string.Empty;

        // Snapshot the JSONL before sending so we can rewind to this point
        _activeSession.Session.InitializeHistory();
        _activeSession.Session.History?.TakeSnapshot();

        // Inject user prompt into Clean view immediately for instant feedback
        CleanView.InjectUserPrompt(text);

        // Notify user when large input is redirected to a temp file
        if (CcDirector.Core.Input.LargeInputHandler.IsLargeInput(text))
        {
            ShowNotification($"Text over {CcDirector.Core.Input.LargeInputHandler.LargeInputThreshold:N0} chars -- saved to temp file and @filepath sent to Claude Code ({text.Length:N0} chars)");
        }
        else
        {
            ClearNotification();
        }

        // Check if this is an interactive TUI command
        var isInteractiveCommand = text.StartsWith("/") && InteractiveTuiCommands.Contains(text.TrimStart('/'));

        // Backends send Enter (CR/LF) explicitly after the text -- don't append a submit
        // newline here. Appending one used to trip LargeInputHandler's multi-line check
        // and route short single-line prompts through a temp file.
        await _activeSession.Session.SendTextAsync(text);

        if (!isInteractiveCommand)
        {
            ScheduleEnterRetry(_activeSession.Session);
        }

        if (isInteractiveCommand)
        {
            EnterInteractiveTuiMode(_activeSession.Session);
        }
        else
        {
            PromptInput.Focus();
        }
    }

    private void ScheduleEnterRetry(Session session)
    {
        _enterRetryCts?.Cancel();
        _enterRetryCts = new CancellationTokenSource();
        var cts = _enterRetryCts;

        void OnStateChanged(ActivityState oldState, ActivityState newState)
        {
            if (newState == ActivityState.Working)
            {
                cts.Cancel();
                session.OnActivityStateChanged -= OnStateChanged;
            }
        }

        session.OnActivityStateChanged += OnStateChanged;
        _ = RetryEnterAfterDelay(session, cts, OnStateChanged);
    }

    private async Task RetryEnterAfterDelay(Session session, CancellationTokenSource cts,
        Action<ActivityState, ActivityState> handler)
    {
        try
        {
            await Task.Delay(3000, cts.Token);
            await session.SendEnterAsync();
            FileLog.Write("[MainWindow] Enter retry: sent extra Enter (no activity within 3s)");
        }
        catch (TaskCanceledException) { /* Activity arrived - no retry needed */ }
        finally
        {
            session.OnActivityStateChanged -= handler;
        }
    }

    // ==================== INTERACTIVE TUI MODE ====================

    /// <summary>
    /// Enters interactive TUI mode: focuses the terminal so keystrokes go directly
    /// to the ConPTY process instead of PromptInput. Auto-exits when TUI closes.
    /// </summary>
    private void EnterInteractiveTuiMode(Session session)
    {
        FileLog.Write("[MainWindow] EnterInteractiveTuiMode: focusing terminal for interactive TUI");
        _isInteractiveTuiMode = true;

        // Cancel any pending Enter retry - interactive TUIs must not receive stray Enters
        _enterRetryCts?.Cancel();

        // Focus the terminal so keystrokes go to the ConPTY process
        SwitchLeftTab("Terminal");
        Dispatcher.UIThread.Post(() => TerminalHost.Focus());

        ShowNotification("Interactive mode -- keys go to terminal. Click prompt input to exit.");

        // Auto-exit when the session transitions back to idle (TUI closed)
        void OnStateChanged(ActivityState oldState, ActivityState newState)
        {
            if (newState is ActivityState.Idle or ActivityState.WaitingForInput)
            {
                session.OnActivityStateChanged -= OnStateChanged;
                Dispatcher.UIThread.Post(ExitInteractiveTuiMode);
            }
        }

        session.OnActivityStateChanged += OnStateChanged;
    }

    private void ExitInteractiveTuiMode()
    {
        if (!_isInteractiveTuiMode) return;

        FileLog.Write("[MainWindow] ExitInteractiveTuiMode: returning focus to PromptInput");
        _isInteractiveTuiMode = false;
        ClearNotification();
        PromptInput.Focus();
    }

    private void PromptInput_GotFocus(object? sender, GotFocusEventArgs e)
    {
        // Exit interactive TUI mode if the user clicks back to the prompt input
        if (_isInteractiveTuiMode)
            ExitInteractiveTuiMode();
    }

    private void PromptInput_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Text) || e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void PromptInput_Drop(object? sender, DragEventArgs e)
    {
        string? path = null;

        if (e.Data.Contains(DataFormats.Text))
        {
            path = e.Data.GetText();
        }
        else if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null)
            {
                var first = files.FirstOrDefault();
                if (first != null)
                    path = first.Path.LocalPath;
            }
        }

        if (!string.IsNullOrEmpty(path))
        {
            FileLog.Write($"[MainWindow] PromptInput_Drop: inserting path={path}");
            var insertion = path + "\n";
            var idx = PromptInput.CaretIndex;
            var text = PromptInput.Text ?? "";
            PromptInput.Text = text.Insert(idx, insertion);
            PromptInput.CaretIndex = idx + insertion.Length;
            PromptInput.Focus();
        }

        e.Handled = true;
    }

    private void BtnQueuePrompt_Click(object? sender, RoutedEventArgs e)
    {
        QueueCurrentPrompt();
    }

    private async void PromptExpand_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] PromptExpand_Click");
        try
        {
            var dialog = new ExpandedEditorDialog("Edit prompt", PromptInput.Text ?? "");
            var applied = await dialog.ShowDialog<bool?>(this);
            if (applied == true)
            {
                PromptInput.Text = dialog.EditedText;
                PromptInput.CaretIndex = PromptInput.Text.Length;
                PromptInput.Focus();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] PromptExpand_Click FAILED: {ex.Message}");
        }
    }

    private async void QueuePreview_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] QueuePreview_Click");
        try
        {
            if (sender is not Control control || control.DataContext is not SessionViewModel vm)
            {
                FileLog.Write("[MainWindow] QueuePreview_Click: no session view model");
                return;
            }

            var queue = vm.Session.PromptQueue;
            if (queue == null || queue.Count == 0)
                return;

            var dialog = new ExpandedEditorDialog($"Queue - {vm.DisplayName}", queue);
            await dialog.ShowDialog<bool?>(this);

            // Edits mutate the queue in memory; persist and refresh the visible panel.
            PersistSessionState();
            if (_activeSession?.Session.Id == vm.Session.Id)
                RefreshQueuePanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] QueuePreview_Click FAILED: {ex.Message}");
        }
    }

    private void QueueCurrentPrompt()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text))
            return;

        var text = PromptInput.Text.Trim();
        FileLog.Write($"[MainWindow] QueueCurrentPrompt: session={_activeSession.Session.Id}, text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\"");
        _activeSession.Session.PromptQueue?.Enqueue(text);
        PromptInput.Text = "";

        RefreshQueuePanel();

        // Auto-open queue tab
        if (_rightPanelExpanded)
            RightPanelTabs.SelectedItem = QueueTab;

        UpdateQueueButtonStyle();
    }

    private void BtnHandover_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnHandover_Click");
        if (_activeSession == null)
        {
            FileLog.Write("[MainWindow] BtnHandover_Click: no active session");
            return;
        }

        _activeSession.Session.SendText("/handover\n");
        FileLog.Write($"[MainWindow] BtnHandover_Click: sent /handover to session {_activeSession.Session.Id}");
    }

    private void UpdateQueueButtonStyle()
    {
        var queue = _activeSession?.Session.PromptQueue;
        var count = queue?.Count ?? 0;

        BtnQueuePrompt.Content = count > 0 ? $"Queue ({count})" : "Queue";

        if (count > 0)
        {
            BtnQueuePrompt.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            BtnQueuePrompt.Foreground = Brushes.White;
        }
        else
        {
            BtnQueuePrompt.Background = (IBrush)(this.FindResource("ButtonBackground") ?? Brushes.DarkGray);
            BtnQueuePrompt.Foreground = (IBrush)(this.FindResource("TextForeground") ?? Brushes.LightGray);
        }
    }

    // ==================== NOTIFICATION BAR ====================

    private void ShowNotification(string message)
    {
        FileLog.Write($"[MainWindow] ShowNotification: {message}");
        NotificationText.Text = message;
        NotificationIcon.IsVisible = true;
        // A plain notification never carries a progress bar; hide any left over from a download.
        HideDownloadProgress();
        NotificationBar.IsVisible = true;
    }

    private void ClearNotification()
    {
        NotificationText.Text = string.Empty;
        NotificationIcon.IsVisible = false;
        HideDownloadProgress();
        NotificationBar.IsVisible = false;
    }

    // ==================== PASTE-REMINDER CARD ====================

    private DispatcherTimer? _pasteCardTimer;
    private const int PasteCardAutoHideSeconds = 15;

    /// <summary>
    /// Shows the floating bottom-right card (e.g. "Browser opened -- press Ctrl+V").
    /// Unlike the thin notification bar, this is meant to still be on screen when
    /// the user comes back from the browser. Auto-hides after
    /// <see cref="PasteCardAutoHideSeconds"/>; the X dismisses it immediately.
    /// </summary>
    private void ShowPasteCard(string title, string body)
    {
        FileLog.Write($"[MainWindow] ShowPasteCard: {title}");
        PasteCardTitle.Text = title;
        PasteCardBody.Text = body;
        PasteCard.IsVisible = true;

        _pasteCardTimer?.Stop();
        _pasteCardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PasteCardAutoHideSeconds) };
        _pasteCardTimer.Tick += (_, _) =>
        {
            _pasteCardTimer?.Stop();
            PasteCard.IsVisible = false;
        };
        _pasteCardTimer.Start();
    }

    private void PasteCardClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] PasteCardClose_Click");
        _pasteCardTimer?.Stop();
        PasteCard.IsVisible = false;
    }

    // ==================== AUTO-UPDATE NOTICE ====================

    /// <summary>True once a verified build is staged, so the sidebar indicator stays green.</summary>
    private bool _updateStaged;

    // Icons reused from the Gateway indicator: a ring while busy, a check when ready,
    // a cross on failure.
    private const string UpdateIconRing = GatewayIconRing;
    private const string UpdateIconCheck = GatewayIconCheck;
    private const string UpdateIconCross = GatewayIconCross;

    /// <summary>
    /// Passively note that an update has been downloaded. It installs
    /// automatically the next time CC Director is launched -- the running app is
    /// never interrupted, so no active sessions are lost. Called by App after
    /// UpdateService stages a verified build (marshalled to the UI thread).
    /// </summary>
    public void ShowUpdateReady(string version)
    {
        FileLog.Write($"[MainWindow] ShowUpdateReady: {version}");
        ShowNotification($"CC Director {version} downloaded -- installs next time you open the app.");
    }

    /// <summary>
    /// Drive the sidebar update indicator and the notification-bar progress bar from
    /// UpdateService phase/byte events (already marshalled to the UI thread by App).
    /// Makes the otherwise-silent check + download visible.
    /// </summary>
    public void OnUpdateProgress(CcDirector.Core.Update.UpdateProgress p)
    {
        try
        {
            switch (p.Phase)
            {
                case CcDirector.Core.Update.UpdatePhase.Checking:
                    SetUpdateIndicator(UpdateIconRing, "#3B82F6", "#1B2A3A", "#3B82F6",
                        "CHECKING FOR UPDATES", "contacting GitHub...");
                    HideDownloadProgress();
                    break;

                case CcDirector.Core.Update.UpdatePhase.Downloading:
                    var pct = p.Fraction is { } f ? (int)Math.Round(f * 100) : 0;
                    SetUpdateIndicator(UpdateIconRing, "#3B82F6", "#1B2A3A", "#3B82F6",
                        "DOWNLOADING UPDATE",
                        p.Fraction is null ? $"{p.Version}" : $"{p.Version} - {pct}%");
                    ShowDownloadProgress(p, pct);
                    break;

                case CcDirector.Core.Update.UpdatePhase.Verifying:
                    SetUpdateIndicator(UpdateIconRing, "#3B82F6", "#1B2A3A", "#3B82F6",
                        "VERIFYING UPDATE", $"{p.Version} - checking integrity");
                    NotificationProgress.IsVisible = true;
                    NotificationProgress.IsIndeterminate = false;
                    NotificationProgress.Value = 100;
                    NotificationProgressMeta.IsVisible = true;
                    NotificationProgressMeta.Text = "verifying...";
                    break;

                case CcDirector.Core.Update.UpdatePhase.Staged:
                    _updateStaged = true;
                    SetUpdateIndicator(UpdateIconCheck, "#22C55E", "#1B3A2A", "#22C55E",
                        "UPDATE READY", $"{p.Version} - installs on restart");
                    HideDownloadProgress();
                    break;

                case CcDirector.Core.Update.UpdatePhase.UpToDate:
                    // Nothing to do: keep the indicator hidden unless an update is already staged.
                    if (!_updateStaged) UpdateIndicator.IsVisible = false;
                    HideDownloadProgress();
                    break;

                case CcDirector.Core.Update.UpdatePhase.Failed:
                    SetUpdateIndicator(UpdateIconCross, "#F59E0B", "#3A2A1B", "#F59E0B",
                        "UPDATE CHECK FAILED", "click to retry");
                    HideDownloadProgress();
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnUpdateProgress FAILED: {ex.Message}");
        }
    }

    private void SetUpdateIndicator(string icon, string accent, string bg, string border, string label, string sub)
    {
        UpdateIndicatorIcon.Data = Geometry.Parse(icon);
        UpdateIndicatorIcon.Fill = Brush.Parse(accent);
        UpdateIndicator.Background = Brush.Parse(bg);
        UpdateIndicator.BorderBrush = Brush.Parse(border);
        UpdateIndicatorLabel.Text = label;
        UpdateIndicatorLabel.Foreground = Brush.Parse(accent);
        UpdateIndicatorSub.Text = sub;
        ToolTip.SetTip(UpdateIndicator, $"{label}\n{sub}\nClick to check for updates now.");
        UpdateIndicator.IsVisible = true;
    }

    private void ShowDownloadProgress(CcDirector.Core.Update.UpdateProgress p, int pct)
    {
        NotificationIcon.IsVisible = false;
        NotificationText.Text = $"Downloading CC Director {p.Version}...";
        NotificationProgress.IsVisible = true;
        if (p.Fraction is not null)
        {
            NotificationProgress.IsIndeterminate = false;
            NotificationProgress.Value = pct;
            NotificationProgressMeta.Text = $"{pct}%   {FormatMb(p.Downloaded)} / {FormatMb(p.Total)}";
        }
        else
        {
            NotificationProgress.IsIndeterminate = true;
            NotificationProgressMeta.Text = $"{FormatMb(p.Downloaded)} downloaded";
        }
        NotificationProgressMeta.IsVisible = true;
        NotificationBar.IsVisible = true;
    }

    private void HideDownloadProgress()
    {
        NotificationProgress.IsVisible = false;
        NotificationProgress.IsIndeterminate = false;
        NotificationProgressMeta.IsVisible = false;
    }

    private static string FormatMb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>Click the sidebar indicator to run a check now (off the UI thread).</summary>
    private void UpdateIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var updater = (global::Avalonia.Application.Current as App)?.Updater;
            if (updater is null)
            {
                FileLog.Write("[MainWindow] UpdateIndicator_PointerPressed: no updater available");
                return;
            }
            FileLog.Write("[MainWindow] UpdateIndicator clicked - manual update check");
            _ = Task.Run(() => updater.CheckAndStageAsync());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] UpdateIndicator_PointerPressed FAILED: {ex.Message}");
        }
    }

    // ==================== RIGHT PANEL TOGGLE ====================

    private void RightPanelToggle_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] RightPanelToggle_Click");
        _rightPanelExpanded = !_rightPanelExpanded;

        if (_rightPanelExpanded)
        {
            RightPanel.IsVisible = true;
            RightPanel.Width = 280;
            RightPanelToggle.Content = "<<";
        }
        else
        {
            RightPanel.IsVisible = false;
            RightPanel.Width = 0;
            RightPanelToggle.Content = ">>";
        }
    }

    // ==================== QUEUE ====================

    private void RefreshQueuePanel()
    {
        _queueItems.Clear();

        var queue = _activeSession?.Session.PromptQueue;
        if (queue == null || queue.Count == 0)
        {
            UpdateQueueBadge(0);
            return;
        }

        var items = queue.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var text = items[i].Text;
            _queueItems.Add(new QueueItemViewModel
            {
                Id = items[i].Id,
                Index = $"#{i + 1}",
                Preview = text.Length > 300 ? text.Substring(0, 300) + "..." : text,
                FullText = text,
            });
        }

        UpdateQueueBadge(items.Count);
    }

    private void UpdateQueueBadge(int count)
    {
        QueueCountText.Text = count == 1 ? "1 item" : $"{count} items";
        QueueTab.Header = count > 0 ? $"Queue ({count})" : "Queue";
        QueueEmptyText.IsVisible = count == 0;
        QueueItemsList.IsVisible = count > 0;
        UpdateQueueButtonStyle();
    }

    private void BtnClearQueue_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearQueue_Click");
        _activeSession?.Session.PromptQueue?.Clear();
        RefreshQueuePanel();
    }

    private void QueueItemPop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemPop_Click: {itemId}");
        var item = _queueItems.FirstOrDefault(q => q.Id == itemId);
        if (item == null) return;

        // Insert into prompt input
        PromptInput.Text = (PromptInput.Text ?? "") + item.FullText;
        _activeSession?.Session.PromptQueue?.Remove(itemId);
        RefreshQueuePanel();
    }

    private async void QueueItemEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemEdit_Click: {itemId}");
        try
        {
            var queue = _activeSession?.Session.PromptQueue;
            if (queue == null || queue.Count == 0)
                return;

            var title = _activeSession != null ? $"Queue - {_activeSession.DisplayName}" : "Queue";
            var dialog = new ExpandedEditorDialog(title, queue, itemId);
            await dialog.ShowDialog<bool?>(this);

            // Edits mutate the queue in memory; persist and refresh the visible panel.
            PersistSessionState();
            RefreshQueuePanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] QueueItemEdit_Click FAILED: {ex.Message}");
        }
    }

    private void QueueItemMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemMoveUp_Click: {itemId}");
        _activeSession?.Session.PromptQueue?.MoveUp(itemId);
        RefreshQueuePanel();
    }

    private void QueueItemMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemMoveDown_Click: {itemId}");
        _activeSession?.Session.PromptQueue?.MoveDown(itemId);
        RefreshQueuePanel();
    }

    private void QueueItemRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemRemove_Click: {itemId}");
        _activeSession?.Session.PromptQueue?.Remove(itemId);
        RefreshQueuePanel();
    }

    private void QueueItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_activeSession == null) return;

        var selected = QueueItemsList.SelectedItem as QueueItemViewModel;
        if (selected == null) return;

        FileLog.Write($"[MainWindow] QueueItemsList_DoubleTapped: {selected.Id}");

        // Pop item and insert into prompt input
        PromptInput.Text = (PromptInput.Text ?? "") + selected.FullText;
        PromptInput.CaretIndex = PromptInput.Text.Length;
        _activeSession.Session.PromptQueue?.Remove(selected.Id);
        RefreshQueuePanel();
        PromptInput.Focus();
    }

    // ==================== SCREENSHOTS ====================

    /// <summary>
    /// Re-point the screenshots tab after the configured folder changes (Settings save), so
    /// the new folder takes effect without restarting the app. Idempotent - safe to call
    /// repeatedly; it tears down the previous watcher first.
    /// </summary>
    public Task ReloadScreenshotsPanelAsync() => InitializeScreenshotsPanelAsync();

    private async Task InitializeScreenshotsPanelAsync()
    {
        FileLog.Write("[MainWindow] InitializeScreenshotsPanelAsync: starting");

        try
        {
            // Idempotent: tear down any previous watcher/timer and clear the list so a reload
            // after a folder change doesn't double-watch or stack stale thumbnails.
            if (_screenshotWatcher is not null)
            {
                _screenshotWatcher.EnableRaisingEvents = false;
                _screenshotWatcher.Created -= OnScreenshotFileChanged;
                _screenshotWatcher.Deleted -= OnScreenshotFileChanged;
                _screenshotWatcher.Renamed -= OnScreenshotFileChanged;
                _screenshotWatcher.Dispose();
                _screenshotWatcher = null;
            }
            _screenshotDebounceTimer?.Stop();
            _screenshotDebounceTimer = null;
            _screenshots.Clear();

            // Single source of truth: the same resolver the phone-upload endpoint writes to
            // (CcStorage.Screenshots()), so the tab always watches where images actually land.
            // It honors the configured folder, falls back to the platform default, and creates
            // the directory if needed - so it always returns a real, existing path.
            _screenshotsDirectory = await Task.Run(() => CcDirector.Core.Storage.CcStorage.Screenshots());

            FileLog.Write($"[MainWindow] InitializeScreenshotsPanelAsync: directory={_screenshotsDirectory}");

            var vms = await Task.Run(() => LoadScreenshotViewModels(_screenshotsDirectory));

            foreach (var vm in vms)
                _screenshots.Add(vm);

            FileLog.Write($"[MainWindow] InitializeScreenshotsPanelAsync: loaded {vms.Count} screenshots");

            // Start file watcher
            _screenshotWatcher = new FileSystemWatcher(_screenshotsDirectory)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _screenshotWatcher.Created += OnScreenshotFileChanged;
            _screenshotWatcher.Deleted += OnScreenshotFileChanged;
            _screenshotWatcher.Renamed += OnScreenshotFileChanged;

            _screenshotDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _screenshotDebounceTimer.Tick += async (_, _) =>
            {
                _screenshotDebounceTimer.Stop();
                await RefreshScreenshots();
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] InitializeScreenshotsPanelAsync FAILED: {ex.Message}");
        }
    }

    private static List<ScreenshotViewModel> LoadScreenshotViewModels(string directory)
    {
        var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
        return Directory.GetFiles(directory)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(50)
            .Select(f => new ScreenshotViewModel(f))
            .ToList();
    }

    private void OnScreenshotFileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _screenshotDebounceTimer?.Stop();
            _screenshotDebounceTimer?.Start();
        });
    }

    private async Task RefreshScreenshots()
    {
        if (_screenshotsDirectory == null) return;

        FileLog.Write("[MainWindow] RefreshScreenshots");

        var vms = await Task.Run(() => LoadScreenshotViewModels(_screenshotsDirectory));

        _screenshots.Clear();
        foreach (var vm in vms)
            _screenshots.Add(vm);
    }

    private void BtnRefreshScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRefreshScreenshots_Click");
        _ = RefreshScreenshots();
    }

    private void BtnClearScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearScreenshots_Click");
        // Clear from UI only (not from disk)
        _screenshots.Clear();
    }

    private async void ScreenshotItem_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ScreenshotViewModel vm)
            return;

        FileLog.Write($"[MainWindow] ScreenshotItem_PointerPressed: {vm.FilePath}");

        // Only start drag on left button press
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        var dataObject = new DataObject();
        dataObject.Set(DataFormats.Text, vm.FilePath);
        await DragDrop.DoDragDrop(e, dataObject, global::Avalonia.Input.DragDropEffects.Copy);
    }

    private void ScreenshotView_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotView_Click: {filePath}");
        try
        {
            // Open images in document tab if a session is active
            if (_activeSession != null && FileExtensions.IsViewable(filePath) && File.Exists(filePath))
            {
                OpenDocumentFile(filePath);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotView_Click FAILED: {ex.Message}");
        }
    }

    private async void ScreenshotCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotCopy_Click: {filePath}");
        try
        {
            // Copy the actual image (not the path) so it pastes into GitHub,
            // Claude Code, Paint, etc. Dragging the thumbnail still gives the path.
            await Task.Run(() => WindowsClipboardImage.CopyImageFile(filePath));
            ShowNotification("Screenshot copied -- paste with Ctrl+V");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotCopy_Click FAILED: {ex.Message}");
            ShowNotification($"Copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One-click "file a bug from this screenshot": copies the image onto the
    /// clipboard, then opens GitHub's new-issue form for the active session's repo
    /// so the screenshot can be pasted straight into the issue body with Ctrl+V.
    /// Hard failures (no session, no GitHub origin) surface as a modal dialog --
    /// the bottom notification bar is too easy to miss for a refused action.
    /// </summary>
    private async void ScreenshotCreateIssue_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotCreateIssue_Click: {filePath}");
        try
        {
            // Modal dialogs already block this button; this guards the one
            // non-modal owned window (the startup restore-progress dialog).
            if (OwnedWindows.Count > 0)
            {
                FileLog.Write("[MainWindow] ScreenshotCreateIssue_Click: refused, owned window open");
                return;
            }

            var session = _activeSession;
            if (session == null)
            {
                await new MessageDialog(
                    "Select a Session First",
                    "The GitHub issue is created in the repository of the active session. " +
                    "Select or create a session, then click Issue again.")
                    .ShowDialog<bool?>(this);
                return;
            }

            var repoPath = session.Session.RepoPath;
            var url = await Task.Run(() =>
            {
                WindowsClipboardImage.CopyImageFile(filePath);
                return GitHubUrls.BuildNewIssueUrl(repoPath);
            });

            FileLog.Write($"[MainWindow] ScreenshotCreateIssue_Click: opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            ShowPasteCard(
                "Browser opened -- one step left",
                "The screenshot is on the clipboard. Click into the issue body and press Ctrl+V to attach it.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotCreateIssue_Click FAILED: {ex.Message}");
            await new MessageDialog("Cannot Create GitHub Issue", ex.Message).ShowDialog<bool?>(this);
        }
    }

    private void ScreenshotDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotDelete_Click: {filePath}");
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);

            var vm = _screenshots.FirstOrDefault(s => s.FilePath == filePath);
            if (vm != null)
                _screenshots.Remove(vm);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotDelete_Click FAILED: {ex.Message}");
        }
    }

    // ==================== SOURCE CONTROL ====================

    private void OnGitViewFileRequested(string fullPath)
    {
        FileLog.Write($"[MainWindow] OnGitViewFileRequested: {fullPath}");
        try
        {
            // Open viewable files in document tabs; everything else externally
            if (FileExtensions.IsViewable(fullPath) && File.Exists(fullPath))
            {
                OpenDocumentFile(fullPath);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnGitViewFileRequested FAILED: {ex.Message}");
        }
    }

    // ==================== RIGHT PANEL TAB SWITCHING ====================

    private bool _sessionBrowserLoaded;

    private void RightPanelTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Lazy-load session browser when Sessions tab is first selected
        if (RightPanelTabs.SelectedItem is TabItem tab && tab.Header?.ToString() == "Sessions" && !_sessionBrowserLoaded)
        {
            _sessionBrowserLoaded = true;
            _ = SessionBrowserView.LoadAsync();
        }
    }

    // ==================== SESSION BROWSER ====================

    private void OnSessionBrowserResumeRequested(string repoPath, string sessionId)
    {
        FileLog.Write($"[MainWindow] OnSessionBrowserResumeRequested: repo={repoPath}, session={sessionId}");

        if (string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(sessionId))
        {
            FileLog.Write("[MainWindow] OnSessionBrowserResumeRequested: missing repoPath or sessionId");
            return;
        }

        var vm = CreateSession(repoPath, resumeSessionId: sessionId);
        if (vm != null)
        {
            SaveSessionToHistory(vm);
            ShowNotification($"Resumed session in {repoPath}");
        }
    }

    // ==================== REWIND ====================

    private async void OnCleanViewRewindRequested(Session session, int entryNumber)
    {
        FileLog.Write($"[MainWindow] OnCleanViewRewindRequested: session={session.Id}, entry={entryNumber}");

        if (session.History == null)
        {
            FileLog.Write("[MainWindow] OnCleanViewRewindRequested: no History on session");
            ShowNotification("Cannot rewind -- session has no history");
            return;
        }

        var repoPath = session.RepoPath;
        var oldSessionVm = _sessions.FirstOrDefault(vm => vm.Session.Id == session.Id);

        if (entryNumber == 0)
        {
            // Fresh reset: start a new session from scratch
            FileLog.Write("[MainWindow] OnCleanViewRewindRequested: entry 0 -- fresh reset");

            // Detach and remove old session
            if (oldSessionVm != null)
            {
                if (_activeSession == oldSessionVm)
                {
                    _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
                    _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
                    TerminalHost.Detach();
                    GitChangesView.Detach();
                    CleanView.Detach();
                    _activeSession = null;
                }

                _sessions.Remove(oldSessionVm);
            }

            // Kill old session in background
            _ = Task.Run(async () =>
            {
                await _sessionManager.KillSessionAsync(session.Id);
                _sessionManager.RemoveSession(session.Id);
            });

            CreateSession(repoPath);
            ShowNotification("Session reset -- started fresh");
        }
        else
        {
            // Restore from snapshot
            var newSessionId = session.History.RestoreSnapshot(entryNumber, repoPath);
            if (newSessionId == null)
            {
                FileLog.Write("[MainWindow] OnCleanViewRewindRequested: RestoreSnapshot returned null");
                ShowNotification("Rewind failed -- snapshot not found");
                return;
            }

            FileLog.Write($"[MainWindow] OnCleanViewRewindRequested: restored snapshot -> newSessionId={newSessionId}");

            // Detach and remove old session
            if (oldSessionVm != null)
            {
                if (_activeSession == oldSessionVm)
                {
                    _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
                    _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
                    TerminalHost.Detach();
                    GitChangesView.Detach();
                    CleanView.Detach();
                    _activeSession = null;
                }

                _sessions.Remove(oldSessionVm);
            }

            // Kill old session in background
            _ = Task.Run(async () =>
            {
                await _sessionManager.KillSessionAsync(session.Id);
                _sessionManager.RemoveSession(session.Id);
            });

            CreateSession(repoPath, resumeSessionId: newSessionId);
            ShowNotification($"Rewound to turn {entryNumber} -- resumed as new session");
        }
    }

    // ==================== WINDOW CLOSING ====================

    private bool _closeConfirmed;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // Log WHY the window is closing (issue #212 L1). The 2026-06-06 post-mortem
        // could not tell an OS shutdown from a user "End task" from a programmatic close
        // because OnClosing logged nothing but the bare event. CloseReason answers it:
        // WindowClosing (user X/Alt+F4), OSShutdown, ApplicationShutdown, OwnerWindowClosing.
        FileLog.Write($"[MainWindow] OnClosing: reason={e.CloseReason}, programmatic={e.IsProgrammatic}, sessions={_sessions.Count}");

        // Check for working sessions and show close dialog
        if (!_closeConfirmed)
        {
            var workingSessions = _sessions
                .Where(vm => vm.Session.ActivityState is ActivityState.Working or ActivityState.WaitingForInput)
                .ToList();

            if (workingSessions.Count > 0)
            {
                e.Cancel = true;

                var sessionNames = workingSessions
                    .Select(vm => vm.DisplayName)
                    .ToList();

                var dialog = new CloseDialog(_sessionManager, sessionNames);
                var result = await dialog.ShowDialog<bool?>(this);

                if (result == true)
                {
                    _closeConfirmed = true;
                    Close();
                }

                return;
            }
        }

        // Unsubscribe from active session events
        if (_activeSession != null)
        {
            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
        }

        // Update LastUsedAt for all active sessions in history
        UpdateAllSessionHistoryTimestamps();

        // Cancel any pending debounced persist and flush immediately
        _persistDebounceCts?.Cancel();
        SyncPromptTextToSessions();
        PersistSessionStateCore();

        // Detach terminal, source control, clean view, and usage dashboard
        TerminalHost.Detach();
        GitChangesView.Detach();
        CleanView.Detach();
        UsageDashboardView.Detach();
        _activeSession = null;

        // Stop git status polling
        _sessionGitTimer?.Stop();

        // Cleanup screenshot watcher
        _screenshotDebounceTimer?.Stop();
        _screenshotWatcher?.Dispose();

        // Unwire session registration
        try
        {
            _sessionManager.OnClaudeSessionRegistered -= OnClaudeSessionRegistered;
            _sessionManager.OnSessionCreated -= OnExternalSessionCreated;
            _sessionManager.OnSessionRenamed -= OnExternalSessionRenamed;
            _sessionManager.OnSessionRemoved -= OnExternalSessionRemoved;
        }
        catch { /* App may be shutting down */ }

        // Call shutdown directly instead of relying on ShutdownRequested event
        // which may never fire depending on Avalonia lifetime state.
        // OnShutdown kills sessions, disposes services, and calls Environment.Exit(0).
        var appRef = (App)global::Avalonia.Application.Current!;
        appRef.OnShutdown(msg => FileLog.Write($"[CcDirector] {msg}"));

        // Environment.Exit(0) inside OnShutdown means we never reach here,
        // but keep base.OnClosing as a safety net.
        base.OnClosing(e);
    }

    private void OnClaudeSessionRegistered(Session session, string claudeSessionId)
    {
        FileLog.Write($"[MainWindow] Claude session registered: {claudeSessionId} for {session.RepoPath}");
        Dispatcher.UIThread.Post(() =>
        {
            PersistSessionState();

            // Update session history entry with the new ClaudeSessionId
            var sessionVm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
            if (sessionVm != null)
                UpdateSessionHistory(sessionVm);

            // Update header if this is the active session
            if (_activeSession?.Session.Id == session.Id)
                UpdateSessionHeader();
        });
    }

    // ==================== SESSION HISTORY ====================

    /// <summary>
    /// Create a new history entry for a session that was just created and renamed.
    /// </summary>
    private void SaveSessionToHistory(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] SaveSessionToHistory: session={vm.Session.Id}, name={vm.Session.CustomName}, repo={vm.Session.RepoPath}");
        var app = (App)global::Avalonia.Application.Current!;
        var entry = new SessionHistoryEntry
        {
            Id = vm.Session.HistoryEntryId ?? Guid.NewGuid(),
            CustomName = vm.Session.CustomName,
            CustomColor = vm.Session.CustomColor,
            RepoPath = vm.Session.RepoPath,
            ClaudeSessionId = vm.Session.ClaudeSessionId,
            CreatedAt = vm.Session.CreatedAt,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
        vm.Session.HistoryEntryId = entry.Id;
        app.SessionHistoryStore.Save(entry);
        FileLog.Write($"[MainWindow] SaveSessionToHistory: saved historyEntryId={entry.Id}");
    }

    /// <summary>
    /// Update an existing history entry with the session's current name, color, and ClaudeSessionId.
    /// </summary>
    private void UpdateSessionHistory(SessionViewModel vm)
    {
        if (vm.Session.HistoryEntryId == null)
        {
            SaveSessionToHistory(vm);
            return;
        }

        var app = (App)global::Avalonia.Application.Current!;
        var entry = app.SessionHistoryStore.Load(vm.Session.HistoryEntryId.Value);
        if (entry == null)
        {
            SaveSessionToHistory(vm);
            return;
        }

        entry.CustomName = vm.Session.CustomName;
        entry.CustomColor = vm.Session.CustomColor;
        entry.ClaudeSessionId = vm.Session.ClaudeSessionId;
        entry.LastUsedAt = DateTimeOffset.UtcNow;
        entry.FirstPromptSnippet = vm.Session.ClaudeMetadata?.FirstPrompt ?? entry.FirstPromptSnippet;
        app.SessionHistoryStore.Save(entry);
    }

    /// <summary>
    /// Update LastUsedAt for all active sessions in history. Called on app close.
    /// </summary>
    private void UpdateAllSessionHistoryTimestamps()
    {
        var app = (App)global::Avalonia.Application.Current!;
        foreach (var vm in _sessions)
        {
            if (vm.Session.HistoryEntryId == null)
                continue;

            var entry = app.SessionHistoryStore.Load(vm.Session.HistoryEntryId.Value);
            if (entry == null)
                continue;

            entry.LastUsedAt = DateTimeOffset.UtcNow;
            entry.ClaudeSessionId = vm.Session.ClaudeSessionId ?? entry.ClaudeSessionId;
            app.SessionHistoryStore.Save(entry);
        }
    }

    // ==================== HANDOVER INJECTION ====================

    /// <summary>
    /// After a new session starts from a handover, wait for Claude Code to be ready
    /// and then send the handover file as a prompt asking it to review and plan next steps.
    /// </summary>
    private async Task InjectHandoverPromptAsync(Session session, string handoverPath)
    {
        FileLog.Write($"[MainWindow] InjectHandoverPromptAsync: waiting for session {session.Id}, handover={handoverPath}");

        // Wait for Claude Code to finish starting up
        await Task.Delay(TimeSpan.FromSeconds(5));

        var prompt = $"@{handoverPath} This is a handover document from a previous session. "
            + "Please read it carefully, then give me a high-level summary of what was done "
            + "and what you think we should work on next. Show the scope of remaining work "
            + "and suggest priorities.";

        await session.SendTextAsync(prompt);
        FileLog.Write($"[MainWindow] InjectHandoverPromptAsync: sent handover prompt for session {session.Id}");
    }

    // ==================== STARTUP TEXT CAPTURE ====================

    /// <summary>
    /// Capture terminal startup text after a brief delay and persist it to the session.
    /// Also writes a debug dump to %LOCALAPPDATA%\CcDirector\debug\.
    /// </summary>
    private async Task CaptureStartupTextAsync(Session session)
    {
        try
        {
            FileLog.Write($"[MainWindow] CaptureStartupTextAsync: waiting 3s for session {session.Id}");
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (session.Buffer == null)
            {
                FileLog.Write($"[MainWindow] CaptureStartupTextAsync: no buffer for session {session.Id}");
                return;
            }

            var startupInfo = TerminalOutputParser.Parse(session.Buffer);
            session.RawStartupText = startupInfo.RawText;
            FileLog.Write($"[MainWindow] CaptureStartupTextAsync: captured {startupInfo.RawText.Length} bytes, {startupInfo.Urls.Count} URLs for session {session.Id}");

            var debugDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CcDirector", "debug");
            Directory.CreateDirectory(debugDir);
            var debugPath = Path.Combine(debugDir, $"startup-{session.Id}.txt");
            TerminalOutputParser.WriteDump(debugPath, startupInfo, session.Id, session.RepoPath, session.ProcessId);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] CaptureStartupTextAsync FAILED: {ex.Message}");
        }
    }

    // ==================== SESSION GIT STATUS POLLING ====================

    private async Task RefreshSessionGitStatusAsync()
    {
        if (_sessionGitRefreshRunning) return;
        _sessionGitRefreshRunning = true;

        try
        {
            var sessions = _sessions.ToList();
            using var semaphore = new SemaphoreSlim(4);

            var tasks = sessions.Select(async vm =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var repoPath = vm.Session.RepoPath;
                    if (!Directory.Exists(repoPath)) return;

                    int count = await _gitStatusProvider.GetCountAsync(repoPath);
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.UncommittedCount = count);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            _sessionGitRefreshRunning = false;
        }
    }

    // ==================== DOCUMENT TABS ====================

    private record DocumentTabInfo(
        Guid SessionId,
        string FilePath,
        string TabId,
        Button TabButton,
        UserControl ViewerControl,
        FileViewerControls.IFileViewer Viewer);

    private readonly List<DocumentTabInfo> _documentTabs = new();

    /// <summary>
    /// Opens a file in a document tab, or switches to it if already open.
    /// </summary>
    public void OpenDocumentFile(string filePath)
    {
        FileLog.Write($"[MainWindow] OpenDocumentFile: {filePath}");

        if (_activeSession == null) return;

        var sessionId = _activeSession.Session.Id;

        // Check if already open for this session
        var existing = _documentTabs.FirstOrDefault(d =>
            d.SessionId == sessionId &&
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            SwitchLeftTab(existing.TabId);
            return;
        }

        // Create the appropriate viewer
        var category = FileExtensions.GetViewerCategory(filePath);
        var (viewer, control) = CreateViewer(category);

        var tabId = $"Doc:{Guid.NewGuid():N}";

        // Create tab button with close button
        var tabPanel = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        var nameText = new TextBlock
        {
            Text = Path.GetFileName(filePath),
            FontSize = 12,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        var closeBtn = new Button
        {
            Content = "x",
            FontSize = 9,
            Padding = new global::Avalonia.Thickness(4, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new global::Avalonia.Thickness(0),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        };
        tabPanel.Children.Add(nameText);
        tabPanel.Children.Add(closeBtn);

        var tabButton = new Button
        {
            Content = tabPanel,
            Background = Brushes.Transparent,
            Foreground = InactiveTextBrush,
            Padding = new global::Avalonia.Thickness(12, 4),
            BorderThickness = new global::Avalonia.Thickness(0),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        };

        var docTab = new DocumentTabInfo(sessionId, filePath, tabId, tabButton, control, viewer);

        // Wire tab button click
        var capturedTabId = tabId;
        tabButton.Click += (_, _) => SwitchLeftTab(capturedTabId);

        // Wire close button
        closeBtn.Click += (_, _) =>
        {
            CloseDocumentTab(docTab);
            // Prevent the tab button click from also firing
        };

        // Wire display name changes (dirty indicator)
        viewer.DisplayNameChanged += () =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                nameText.Text = viewer.GetDisplayName();
            });
        };

        _documentTabs.Add(docTab);

        // Add button to the tab bar
        DocumentTabBar.Items.Add(tabButton);
        DocTabSeparator.IsVisible = true;
        DocumentTabBar.IsVisible = true;
        CloseAllDocsButton.IsVisible = true;

        // Switch to the new tab
        SwitchLeftTab(tabId);

        // Load file content asynchronously
        LoadDocumentContentInBackground(viewer, filePath);
    }

    private static (FileViewerControls.IFileViewer viewer, UserControl control) CreateViewer(FileViewerCategory category)
    {
        switch (category)
        {
            case FileViewerCategory.Image:
                var img = new FileViewerControls.ImageViewerControl();
                return (img, img);
            case FileViewerCategory.Code:
                var code = new FileViewerControls.CodeViewerControl();
                return (code, code);
            case FileViewerCategory.Markdown:
                var md = new FileViewerControls.MarkdownViewerControl();
                return (md, md);
            case FileViewerCategory.Pdf:
                var pdf = new FileViewerControls.PdfViewerControl();
                return (pdf, pdf);
            case FileViewerCategory.Html:
                var html = new FileViewerControls.HtmlViewerControl();
                return (html, html);
            case FileViewerCategory.Text:
            default:
                var text = new FileViewerControls.TextViewerControl();
                return (text, text);
        }
    }

    private async void LoadDocumentContentInBackground(FileViewerControls.IFileViewer viewer, string filePath)
    {
        try
        {
            await viewer.LoadFileAsync(filePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] LoadDocumentContentInBackground FAILED: {ex.Message}");
            viewer.ShowLoadError(ex.Message);
        }
    }

    private void CloseDocumentTab(DocumentTabInfo docTab)
    {
        FileLog.Write($"[MainWindow] CloseDocumentTab: {docTab.FilePath}");

        // Remove from tracking
        _documentTabs.Remove(docTab);

        // Remove button from tab bar
        DocumentTabBar.Items.Remove(docTab.TabButton);

        // Remove from document panel if currently shown
        if (DocumentPanel.Children.Contains(docTab.ViewerControl))
            DocumentPanel.Children.Remove(docTab.ViewerControl);

        // Update visibility of doc tab UI
        var hasDocTabs = _documentTabs.Any(d => d.SessionId == (_activeSession?.Session.Id ?? Guid.Empty));
        DocTabSeparator.IsVisible = hasDocTabs;
        DocumentTabBar.IsVisible = _documentTabs.Count > 0;
        CloseAllDocsButton.IsVisible = hasDocTabs;

        // If the closed tab was active, switch to Terminal
        if (_activeLeftTab == docTab.TabId)
            SwitchLeftTab("Terminal");
    }

    private void CloseAllDocsButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] CloseAllDocsButton_Click");

        if (_activeSession == null) return;

        var sessionId = _activeSession.Session.Id;
        var toClose = _documentTabs.Where(d => d.SessionId == sessionId).ToList();

        foreach (var docTab in toClose)
        {
            _documentTabs.Remove(docTab);
            DocumentTabBar.Items.Remove(docTab.TabButton);
            if (DocumentPanel.Children.Contains(docTab.ViewerControl))
                DocumentPanel.Children.Remove(docTab.ViewerControl);
        }

        DocTabSeparator.IsVisible = false;
        DocumentTabBar.IsVisible = _documentTabs.Count > 0;
        CloseAllDocsButton.IsVisible = false;

        if (_activeLeftTab.StartsWith("Doc:", StringComparison.Ordinal))
            SwitchLeftTab("Terminal");
    }

    /// <summary>
    /// Shows/hides document tab buttons based on the active session.
    /// Called when switching sessions.
    /// </summary>
    private void SwitchDocumentTabsToSession(Guid sessionId)
    {
        FileLog.Write($"[MainWindow] SwitchDocumentTabsToSession: {sessionId}");

        // Rebuild the tab bar items for the new session
        DocumentTabBar.Items.Clear();

        var sessionDocTabs = _documentTabs.Where(d => d.SessionId == sessionId).ToList();

        foreach (var docTab in sessionDocTabs)
            DocumentTabBar.Items.Add(docTab.TabButton);

        var hasTabs = sessionDocTabs.Count > 0;
        DocTabSeparator.IsVisible = hasTabs;
        DocumentTabBar.IsVisible = _documentTabs.Count > 0;
        CloseAllDocsButton.IsVisible = hasTabs;

        // If the active tab was a document from a different session, switch to Terminal
        if (_activeLeftTab.StartsWith("Doc:", StringComparison.Ordinal))
        {
            var isStillValid = sessionDocTabs.Any(d => d.TabId == _activeLeftTab);
            if (!isStillValid)
            {
                // Force tab switch by resetting _activeLeftTab
                _activeLeftTab = "";
                SwitchLeftTab("Terminal");
            }
        }
    }
}
