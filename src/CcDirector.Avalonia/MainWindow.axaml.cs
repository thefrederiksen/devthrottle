using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Core.HostedAi;
using Avalonia.VisualTree;
using CcDirector.ControlApi;
using CcDirector.Core.Account;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Setup;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Home;
using CcDirector.Core.Instances;
using CcDirector.Core.Network;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Sessions;
using CcDirector.Core.Settings;
using CcDirector.Gateway.Contracts;
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
    // Internal, not private: the compose-box route test (ruling R20) adds a session here and drives the
    // real SelectSession, insert and send through it.
    internal readonly ObservableCollection<SessionViewModel> _sessions = new();
    private SessionViewModel? _activeSession;

    // Slash command autocomplete
    private readonly SlashCommandProvider _slashCommandProvider = new();
    private List<SlashCommandItem> _filteredSlashCommands = new();

    // Rail refresh timers. The git-status probe that used to ride the first of these now runs on the
    // Director (Core.Git.SessionGitStatusMonitor) so the count reaches every surface, not just this one.
    private global::Avalonia.Threading.DispatcherTimer? _sessionGitTimer;
    private global::Avalonia.Threading.DispatcherTimer? _dictationLockTimer;

    /// <summary>
    /// 1 while a dictation-lock read is out on the thread pool (issue #1111). The one-second tick skips
    /// rather than queues, so a slow disk cannot build a backlog of reads whose answers are already stale.
    /// An int rather than a bool because it is set with <see cref="Interlocked"/> from both the dispatcher
    /// and the pool thread.
    /// </summary>
    private int _dictationLockReadInFlight;

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

    // Serializes screenshots-panel reloads. A reload tears down the watcher and builds a new one, so two
    // overlapping reloads (impatient Refresh clicks, or Refresh while the wizard's save reloads) would
    // leave an orphaned watcher raising events at the panel forever.
    private readonly SemaphoreSlim _screenshotReloadGate = new(1, 1);
    // The image file types the Screenshots panel loads and clears. Kept in one place so listing
    // (LoadScreenshotViewModels) and Clear All (DeleteAllScreenshots) agree on what a screenshot is.
    private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public MainWindow()
    {
        InitializeComponent();
        FileLog.Write("[MainWindow] Avalonia MainWindow initialized");

        // The declared size is a PREFERENCE, not a promise - shrink it to the display before the
        // window is ever shown (issue #1049).
        FitToWorkArea();

        // Show which named instance this window is when multiple instances exist, so they are
        // distinguishable. A single-instance install reads plainly as "DevThrottle Director".
        Title = "DevThrottle Director" + InstanceTitleSuffix();

        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;

        // Register KeyDown as tunnel so it fires before AcceptsReturn consumes Ctrl+Enter
        PromptInput.AddHandler(KeyDownEvent, PromptInput_KeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Every change to the box's text - typing, a paste, a session switch, the clear after a send - passes
        // through this one hook: slash-command autocomplete, and the compose box's own record of which of
        // its characters came from a microphone (ruling R20). Wired here, not in Loaded, so it is live
        // before any code sets the box's text.
        PromptInput.TextChanged += PromptInput_TextChanged;

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

        // Alpha gating: Handover is an alpha feature, hidden by default.
        // Re-gate live when the flag is toggled in the Settings dialog.
        ApplyAlphaFeatureVisibility();
        AlphaMode.Changed += OnAlphaModeChanged;
        Closed += (_, _) => AlphaMode.Changed -= OnAlphaModeChanged;

        // The monitor's live-session source is wired HERE, in the constructor, NOT in
        // MainWindow_Loaded: App.ShowMainWindow starts the first repository rescan
        // synchronously right after constructing this window, and the monitor refuses to
        // scan unwired (ruling R2-8). Loaded fires asynchronously after layout, so wiring
        // there loses the race and the first scan throws - which is exactly what happened
        // on the first live run of the fixed build.
        if (global::Avalonia.Application.Current is App appForMonitor)
            appForMonitor.RepositoryMonitor.LiveSessionsProvider = GetLiveSessionsOnThisMachineAsync;

        BuildNativeMenu();
    }

    /// <summary>
    /// Shrinks the window to the work area of the display it is about to open on, and centres it
    /// there. Runs in the constructor, before Show, so the window is never briefly bigger than the
    /// desktop.
    ///
    /// Issue #1049: the window opened at a fixed 1400x900 regardless of the screen. On a small
    /// display that left it larger than the desktop and inset from the corner, so it ran off the
    /// right and bottom edges - putting Settings off the right edge and unclickable, and with it
    /// every "you can change this later in Settings" promise the setup wizard makes. The supported
    /// minimum is a 1280x720 display, about 1280x672 of work area once the taskbar is subtracted.
    /// </summary>
    private void FitToWorkArea()
    {
        // Before the window is shown the platform frame does not exist yet, so the real border and
        // title bar overhead is unknown. Size against the work area alone here - that alone stops a
        // window very much larger than the desktop ever existing - and correct it in OnOpened,
        // where the frame can be measured.
        ApplyFit(FrameOverhead.None, movePosition: false, stage: "pre-show");
    }

    /// <summary>
    /// Re-fits the window once it exists, when two things are knowable that were not before: how
    /// much bigger the frame is than the client area, and which display the window actually opened
    /// on. Both were measured to matter on issue #1049 - the frame is 39 device independent pixels
    /// taller than the client on Windows 11, and a Position set before Show is discarded.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var frame = FrameSize is { } size
            ? new FrameOverhead(
                Math.Max(0, size.Width - ClientSize.Width),
                Math.Max(0, size.Height - ClientSize.Height))
            : FrameOverhead.None;

        ApplyFit(frame, movePosition: true, stage: "opened");
    }

    /// <summary>
    /// Shrinks the window to the work area of the display it is on and centres it there.
    ///
    /// Issue #1049: the window opened at a fixed 1400x900 regardless of the screen. On a small
    /// display that left it larger than the desktop and inset from the corner, so it ran off the
    /// right and bottom edges - putting Settings off the right edge and unclickable, and with it
    /// every "you can change this later in Settings" promise the setup wizard makes. The supported
    /// minimum is a 1280x720 display, about 1280x672 of work area once the taskbar is subtracted.
    /// </summary>
    private void ApplyFit(FrameOverhead frame, bool movePosition, string stage)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            // Desktop Avalonia always reports at least one display. Nothing to fit against means
            // something is wrong with the windowing platform, so say so loudly rather than
            // silently opening at a size that may not fit.
            FileLog.Write($"[MainWindow] ApplyFit ({stage}) FAILED: the windowing platform reported no display; leaving the window as declared");
            return;
        }

        var area = new WorkArea(
            screen.WorkingArea.X,
            screen.WorkingArea.Y,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height,
            screen.Scaling);

        var placement = WindowFit.Fit(Width, Height, area, frame);

        FileLog.Write(
            $"[MainWindow] ApplyFit ({stage}): workArea={area.Width}x{area.Height} physical, scaling={area.Scaling}, " +
            $"logical={area.LogicalWidth:F0}x{area.LogicalHeight:F0}, frame={frame.Width:F0}x{frame.Height:F0}, " +
            $"desired={Width}x{Height}, chosen={placement.Width:F0}x{placement.Height:F0} at {placement.X},{placement.Y}");

        Width = placement.Width;
        Height = placement.Height;

        if (!movePosition)
            return;

        // The platform applies its own default placement as part of showing the window, and that
        // happens AFTER OnOpened - measured on 30 July 2026, a Position assigned here was honoured
        // when the size also changed and silently discarded when it did not, landing the window on
        // a different monitor. Posting the move puts it after the show completes, so it holds
        // either way. Centring is part of the fix, not decoration: a window as tall as the work
        // area is pushed off the bottom by any default offset at all.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Dispatcher.UIThread.Post(
            () =>
            {
                Position = new PixelPoint(placement.X, placement.Y);
                FileLog.Write($"[MainWindow] ApplyFit ({stage}): position applied at {Position.X},{Position.Y}");
            },
            DispatcherPriority.Loaded);
    }

    private void OnAlphaModeChanged()
    {
        // The flag could be toggled off the UI thread (e.g. a future REST write); always hop to it.
        // BuildNativeMenu is rebuilt too because the Developer menu is alpha-gated.
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
        SourceControlView.ViewFileRequested += OnGitViewFileRequested;
        SourceControlView.OrphanedCountChanged += OnOrphanedWorktreeCountChanged;
        // Feed the Worktrees page the AUTHORITATIVE live sessions on this machine (fleet-wide across
        // Director slots) for its reaper. This is the destructive path, so it uses the fail-closed
        // provider that refuses rather than act on a partial roster (issue 516) - not the monitor's
        // best-effort display source.
        SourceControlView.LiveSessionsProvider = GetAuthoritativeLiveSessionsAsync;

        // Keep the pinned Repositories badge (safe-to-reap worktree count) in sync with the scan.
        // (The monitor's LiveSessionsProvider itself is wired in the CONSTRUCTOR - it must be
        // in place before App.ShowMainWindow triggers the first rescan; see the ctor comment.)
        if (global::Avalonia.Application.Current is App appForRepo)
        {
            appForRepo.RepositoryMonitor.Upserted += _ => Dispatcher.UIThread.Post(UpdateRepositoriesBadge);
            appForRepo.RepositoryMonitor.Removed += _ => Dispatcher.UIThread.Post(UpdateRepositoriesBadge);
            appForRepo.RepositoryMonitor.ProgressChanged += () => Dispatcher.UIThread.Post(UpdateRepositoriesBadge);
            UpdateRepositoriesBadge();
        }

        // Repository detail: authoritative (fail-closed) live sessions for the worktrees panel's reaper.
        RepositoriesView.LiveSessionsProvider = GetAuthoritativeLiveSessionsAsync;

        // Pinned Browsers group (Browsers feature, slice 2): manage lands on Settings > Browsers,
        // and its action/failure feedback rides the shared notification strip.
        BrowsersRail.ManageRequested += (_, _) => _ = OpenSettingsAsync(onBrowsersTab: true);
        BrowsersRail.Notified += (_, message) => Dispatcher.UIThread.Post(() => ShowNotification(message));

        // The prompt box's text-changed hook (slash-command autocomplete AND the compose box's provenance,
        // ruling R20) is wired in the CONSTRUCTOR with the box's other handlers, so it is live from the first
        // character the box ever holds - a session selected before Loaded fires restores its pending text
        // through that hook too.
        PromptInput.LostFocus += (_, _) => SlashCommandPopup.IsOpen = false;
        PromptInput.GotFocus += PromptInput_GotFocus;

        SetBuildInfo();
        // The update status paints from the first frame and keeps itself current. It is deliberately
        // started here, beside the version footer, because the two answer the same question and only
        // one of them used to have an answer (issue #1030).
        StartUpdateStatusDisplay();
        _ = InitializeScreenshotsPanelAsync();
        // No automatic workspace picker on startup (like VS Code). Use File | Open Workspace.

        // Home page (empty-state): its actions route to the existing flows. Paint it now
        // so the very first frame at zero sessions is the home, not a blank content area.
        HomeView.NewSessionRequested += (_, _) => { FileLog.Write("[MainWindow] Home -> New Session"); _ = ShowNewSessionDialog(); };
        HomeView.OpenToolsRequested += (_, _) => { FileLog.Write("[MainWindow] Home -> Tools tab in Settings"); _ = OpenSettingsAsync(onToolsTab: true); };
        HomeView.RepairToolsRequested += (_, _) => _ = RepairToolsAsync();
        HomeView.OpenSettingsRequested += (_, _) => BtnSettings_Click(this, new RoutedEventArgs());
        HomeView.GatewayClicked += (_, _) => OpenGatewayConnectionPanel();

        // The agent readiness row is a READ of a fact that anything can change while this window is
        // open - the first-run wizard, the Settings Agents tab, the Control API. Computing it once at
        // startup is what let the board go on saying "No coding agent found" after the wizard had just
        // installed one and written it (issue #1047). Re-read it whenever the store is written, so the
        // board cannot be stale rather than merely being refreshed by someone who remembered to.
        AgentEntryStore.EntriesChanged += OnAgentEntriesChanged;
        Closed += (_, _) => AgentEntryStore.EntriesChanged -= OnAgentEntriesChanged;

        UpdateHomeVisibility();

        // Refresh the rail's relative time labels and the needs-you count every 15 seconds.
        //
        // This timer used to ALSO probe git for each session's uncommitted file count and write it onto the
        // view model. That poll has moved to the Director (Core.Git.SessionGitStatusMonitor), which writes
        // Session.UncommittedCount and so puts the number on the wire for the Cockpit roster and the phone
        // too. Computing it here meant the number existed only on this screen. The rail now subscribes to
        // the session's own change event and renders the same count every other surface sees.
        _sessionGitTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _sessionGitTimer.Tick += (_, _) =>
        {
            foreach (var vm in _sessions) vm.RefreshTimeLabels();
            UpdateNeedsYouCount();
        };
        _sessionGitTimer.Start();

        // Issue #1181, Task 3b: refresh each session's "receiving a dictation" flag once a second so the
        // rail can paint it orange while a phone dictation is inbound. The Session raises a change event
        // only when it flips, so the rail repaints just on the edges. (Task 4 will additionally compute
        // this at the Gateway so the phone and cockpit show the same state.)
        //
        // Issue #1111: this refresh used to be the single most expensive thing the Director did, and it got
        // worse with every session opened. Two things were wrong and both are fixed here.
        //
        // FIRST, the store was read once per tick PER SESSION. Each session asked the marker store about
        // itself, and each ask re-enumerated the whole directory and re-read every marker in it, so the work
        // was sessions x markers. Measured on this repository's own harness against a 28-marker store: 2.3ms
        // a tick at one session, 58ms a tick at twenty-seven - every millisecond of it on the dispatcher,
        // once a second, forever. It is read ONCE here and each session then asks the resulting set for free,
        // which is flat and under a millisecond regardless of how many sessions are open.
        //
        // SECOND, even the deduped read is file input/output, and file input/output does not belong on the
        // thread that paints. It runs on the thread pool and only the RESULT comes back to the dispatcher;
        // RefreshReceivingDictation raises its change event solely on a flip, so an idle Director posts a
        // set, compares it, and repaints nothing.
        //
        // A tick is SKIPPED while the previous read is still running rather than queued behind it. A slow
        // disk must not build a backlog of reads whose answers are all superseded by the newest one - the
        // only interesting answer is the current state of the store, and a late tick carries a stale one.
        _dictationLockTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _dictationLockTimer.Tick += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref _dictationLockReadInFlight, 1, 0) != 0) return;

            _ = Task.Run(() =>
            {
                IReadOnlySet<string> lockedSessionIds;
                try
                {
                    lockedSessionIds = Session.DictationLockedIds();
                }
                catch (Exception ex)
                {
                    // The reader already fails open per marker; this is the belt for the whole pass, because an
                    // unhandled throw on a pool thread takes the process down and this runs every second.
                    FileLog.Write($"[MainWindow] dictation lock read FAILED: {ex.Message}");
                    Interlocked.Exchange(ref _dictationLockReadInFlight, 0);
                    return;
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        foreach (var vm in _sessions) vm.Session.RefreshReceivingDictation(lockedSessionIds);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _dictationLockReadInFlight, 0);
                    }
                });
            });
        };
        _dictationLockTimer.Start();

        WireGatewayStatusBox();
        InitDirectorInfo();

        MaybeShowFirstRunWizards();
    }

    /// <summary>
    /// Run the first-run setup wizard on the UI thread after the main window is shown (issue #2101,
    /// epic #2100): one guided wizard replacing the retired chain of two dialogs (onboarding then
    /// tool-detection). Gated so a returning user - or a machine that finished the OLD onboarding -
    /// sees nothing. Posted to Background priority so it opens after the first render, never blocking
    /// it.
    /// </summary>
    private void MaybeShowFirstRunWizards()
    {
        FileLog.Write("[MainWindow] MaybeShowFirstRunWizards");
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await MaybeShowFirstRunWizardAsync();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] MaybeShowFirstRunWizards FAILED: {ex.Message}");
            }
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// On first launch (the completion marker is absent, issue #2101), open the single first-run
    /// wizard that walks a fresh user from launch to a working agent. Once completed or skipped it
    /// writes the marker and never auto-opens again. If the user chooses "Start my first agent" on the
    /// Done screen, route them straight to the New Session dialog.
    /// </summary>
    private async Task MaybeShowFirstRunWizardAsync()
    {
        FileLog.Write("[MainWindow] MaybeShowFirstRunWizardAsync");
        if (!FirstRunWizardModel.ShouldShow())
        {
            FileLog.Write("[MainWindow] MaybeShowFirstRunWizardAsync: first-run already complete; not auto-opening");
            return;
        }

        var wantsNewSession = await OpenFirstRunWizardAsync();
        if (wantsNewSession)
        {
            FileLog.Write("[MainWindow] MaybeShowFirstRunWizardAsync: user chose to start their first agent");
            await ShowNewSessionDialog();
        }
    }

    /// <summary>Open the first-run wizard modally; returns true when the user asked to start a session.</summary>
    internal async Task<bool> OpenFirstRunWizardAsync()
    {
        FileLog.Write("[MainWindow] OpenFirstRunWizardAsync");
        var app = global::Avalonia.Application.Current as App;
        var options = app?.SessionManager?.Options ?? app?.Options
            ?? throw new InvalidOperationException("AgentOptions not loaded.");
        // The wizard's Screenshots step writes the folder into config; hand it the panel reload so the
        // thumbnails appear behind the wizard the moment the folder is confirmed.
        var dialog = new FirstRunWizardDialog(options, ReloadScreenshotsPanelAsync);
        await dialog.ShowDialog<bool?>(this);

        // The wizard installs agents AND tools, so every readiness fact behind the board may have moved
        // while it was open. Re-read all of them before the user sees the board - arriving at a screen
        // that contradicts the receipt they just read is the whole of issue #1047. The tool-health cache
        // is dropped too, or the tools row would keep reporting the pre-wizard run.
        FileLog.Write("[MainWindow] OpenFirstRunWizardAsync: wizard closed, re-reading readiness");
        _lastToolHealth = null;
        UpdateHomeVisibility();

        return dialog.WantsNewSession;
    }

    private void InitDirectorInfo()
    {
        FileLog.Write("[MainWindow] InitDirectorInfo");
        // This Director's NAME, on the always-visible app toolbar - the one thing that tells this
        // Director apart from the others on the same machine. It is known before the window opens
        // (the instance is resolved in Program.Main), so there is nothing to wait for.
        //
        // It used to read "MACHINE:port" and poll until the Control API bound its port. Both are gone:
        // the machine name cannot distinguish this Director from its neighbours, and nothing reaches a
        // Director by port any more - the fleet goes through the Gateway.
        DirectorInfoText.Text = DirectorHandle.Label(InstanceContext.DisplayName, Environment.MachineName);
    }

    /// <summary>
    /// Puts this Director's identity on the clipboard - name, id, machine - so it can be pasted to
    /// another agent, which is then told what to do with it.
    ///
    /// This button used to copy the Control API URL, from when reaching a Director meant dialling its
    /// port directly. Nothing does that now, so what it copied could not be used for anything.
    /// </summary>
    private async void BtnCopyDirectorInfo_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnCopyDirectorInfo_Click");
        try
        {
            var app = global::Avalonia.Application.Current as App;
            var directorId = app?.ControlApiHost?.DirectorId;
            if (string.IsNullOrWhiteSpace(directorId))
            {
                // No id yet means the Control API has not been constructed. The id is the whole point
                // of the copy - handing over the other two lines without it identifies nothing.
                ShowNotification("This Director has no id yet - it is still starting.");
                return;
            }

            var text = DirectorHandle.Identity(
                InstanceContext.DisplayName, directorId, Environment.MachineName);

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { ShowNotification("Clipboard unavailable."); return; }
            await clipboard.SetTextAsync(text);
            ShowNotification("Copied this Director's name, id, and machine.");
            FileLog.Write($"[MainWindow] BtnCopyDirectorInfo_Click: copied identity of {directorId}");
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

    private const string GatewayIconRing = "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M8,3 A5,5 0 1 1 8,13 A5,5 0 1 1 8,3 Z";
    private const string GatewayIconCheck = "M6.2,12.4 L1.6,7.8 L3,6.4 L6.2,9.6 L13,2.8 L14.4,4.2 Z";
    private const string GatewayIconCross = "M3.4,2 L8,6.6 L12.6,2 L14,3.4 L9.4,8 L14,12.6 L12.6,14 L8,9.4 L3.4,14 L2,12.6 L6.6,8 L2,3.4 Z";

    /// <summary>
    /// Attach the sidebar indicator to the host's GatewayConnectionMonitor. The
    /// ControlApiHost starts in the background after the window opens, so retry on a
    /// short timer until it exists, then go fully event-driven.
    /// </summary>
    private void WireGatewayStatusBox()
    {
        // Seed the gateway host the box shows on line 1 from config NOW. Without this a configured-Director
        // startup would briefly paint line 1 empty (looking brand-new) before the monitor attaches.
        RefreshGatewayConfigFields();

        // Line 2 of the box (account signed-in) is fed by a heartbeat poll of the Gateway's
        // GET /account/status; line 1 (Gateway reachable) is fed by the GatewayConnectionMonitor
        // attached below. Both repaint the one box through GatewayStatusBoxPresenter.
        WireAccountStatusPoll();

        _gatewayAttachTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _gatewayAttachTimer.Tick += (_, _) => TryAttachGatewayMonitor();
        _gatewayAttachTimer.Start();
        TryAttachGatewayMonitor();
    }

    // Read the configured gateway right now so line 1 of the box knows WHICH gateway to name before any
    // network read returns. Cheap config-file read (same one RefreshAccountStatusAsync does on the UI
    // thread); account fields are left untouched - the poll owns those.
    private void RefreshGatewayConfigFields()
    {
        var config = GatewayConfig.Load();
        _boxGatewayConfigured = config.IsEnabled;
        _boxGatewayHost = SafeHost(config.Url);
    }

    private global::Avalonia.Threading.DispatcherTimer? _settleRepaintTimer;

    /// <summary>
    /// One-shot repaint a beat after the Gateway settle grace. A row that is online but still holds no Gateway
    /// stamp flips to the magenta "unstamped" sentinel only once the connection has SETTLED (see
    /// <see cref="SessionViewModel"/>), and settling is the passage of time, which fires no per-row event.
    /// Without this, a broken push would leave a non-working row on the neutral placeholder until some unrelated
    /// event happened to repaint it. Re-armed on every connection change; a healthy connection stamps every row
    /// before this fires, making the repaint a no-op. 18s covers the 15s grace with margin.
    /// </summary>
    private void ScheduleSettleRepaint()
    {
        if (_gatewayMonitor is null || _gatewayMonitor.Status != GatewayConnectionStatus.Connected) return;
        _settleRepaintTimer?.Stop();
        _settleRepaintTimer = new global::Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(18) };
        _settleRepaintTimer.Tick += (_, _) =>
        {
            _settleRepaintTimer?.Stop();
            _settleRepaintTimer = null;
            foreach (var vm in _sessions) vm.RefreshGatewayFloor();
        };
        _settleRepaintTimer.Start();
    }

    private void TryAttachGatewayMonitor()
    {
        if (_gatewayMonitor is not null) return;
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null) return;

        _gatewayMonitor = host.GatewayMonitor;
        _gatewayMonitor.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // ReapplyGatewayAsync resets this same monitor after loading the new config. Refresh the
                // display identity before repainting so a change or disconnect can never pair the new verdict
                // with the previous gateway host. The account poll deliberately does not own these fields.
                RefreshGatewayConfigFields();
                UpdateGatewayStatusBox();
                // The rail's offline floor (SessionViewModel.EffectiveColor) renders blue/red from local activity
                // whenever the tunnel is not Connected, and Connected's stamp otherwise. A connect/disconnect
                // carries none of the per-session events a row hears, so repaint every row from here - the one
                // place that owns the GatewayConnectionMonitor subscription - or a settled row keeps its old dot
                // until an unrelated event happens to repaint it.
                foreach (var vm in _sessions) vm.RefreshGatewayFloor();
                ScheduleSettleRepaint();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] Gateway monitor change handling FAILED: {ex}");
                ShowNotification("Gateway status could not be refreshed. Open Gateway settings for details.");
            }
        });

        // There is no Control-API status indicator any more: the Remove-the-network-port mission
        // deleted the Director's listener, so there is no bind that can fail and nothing for a
        // port-exhaustion tile to report. Fleet reachability is the Gateway status box below.

        _gatewayAttachTimer?.Stop();
        _gatewayAttachTimer = null;
        RefreshGatewayConfigFields();
        UpdateGatewayStatusBox();
        FileLog.Write("[MainWindow] Gateway status box attached to GatewayConnectionMonitor");
    }

    // Cached line-2 (account) inputs, refreshed by the account status poll; combined with the live
    // monitor state (line 1) to paint the one box through GatewayStatusBoxPresenter (spec section 6).
    private GatewayAccountSignInState _boxAccount = GatewayAccountSignInState.Unknown;
    private string? _boxAccountEmail;
    private bool _boxDeviceKeyPresent;
    private bool _boxGatewayConfigured;
    private string? _boxGatewayHost;

    // True once the two-way handshake has proven Connected at least once this run, so a later failure
    // reads as "was working, now unreachable" (red repair) rather than "never set up" (spec section 4).
    private bool _boxWasEverConnected;

    /// <summary>
    /// Paint the ONE bottom-left status box from both verification sources (design spec section 6):
    /// line 1 (Gateway reachable) from the live <see cref="GatewayConnectionMonitor"/>, line 2 (account
    /// signed in) from the cached account-status poll. Both are reduced by
    /// <see cref="GatewayStatusBoxPresenter"/> - which runs the same resolver the panel uses - so the box
    /// and the panel can never disagree. GREEN is EARNED: line 1 only on a proven two-way handshake
    /// (the EXAMPLE-PC failure mode of heartbeats-fine-callback-dead shows RED, the point of #224); line 2
    /// only on the Gateway's own signed-in report.
    /// </summary>
    private void UpdateGatewayStatusBox()
    {
        var inputs = BuildStatusBoxInputs();
        if (inputs.Connection == GatewayConnectionVerification.Connected)
            _boxWasEverConnected = true;

        var content = GatewayStatusBoxPresenter.Describe(inputs, _boxGatewayHost, _boxAccountEmail);

        // The chip form: the dot and the short verdict carry the state; the two full check lines
        // (which gateway, which account) live in the tooltip so no information was lost in the slim-down.
        var (dot, text) = ChipStyle(content.Visual);
        GatewayChipDot.Fill = Brush.Parse(dot);
        GatewayChipText.Text = content.ChipText;
        GatewayChipText.Foreground = Brush.Parse(text);
        var detail = string.IsNullOrEmpty(content.Connected.Text)
            ? content.SignedIn.Text
            : $"{content.Connected.Text}. {content.SignedIn.Text}";
        ToolTip.SetTip(GatewayStatusBox, $"{detail}.\n{content.Tooltip}");
        AutomationProperties.SetName(GatewayStatusBox, detail);
        AutomationProperties.SetHelpText(GatewayStatusBox, content.Tooltip);

        // A missing gateway is NOT an error (a legitimate local-only Director); only the red failure
        // states count against readiness and surface a problem row on the status screen.
        _gatewayError = content.Visual == GatewayStatusBoxVisual.Red;
        ApplyHomeHealth();
    }

    // Combine the live monitor state (line 1) with the cached account poll (line 2) into the resolver's
    // full input snapshot. GatewayConfigured is true when either source says so, so the box resolves
    // correctly whichever attached first.
    private GatewayConnectionInputs BuildStatusBoxInputs()
    {
        var m = _gatewayMonitor;
        var (connection, leg) = MapMonitor(m);
        var configured = _boxGatewayConfigured || (m is not null && m.Status != GatewayConnectionStatus.NotConfigured);
        return new GatewayConnectionInputs(
            GatewayConfigured: configured,
            Connection: connection,
            FailedLeg: leg,
            WasEverConnected: _boxWasEverConnected,
            DeviceKeyPresent: _boxDeviceKeyPresent,
            Account: _boxAccount);
    }

    // Map the monitor's raw status onto the resolver's connection verification plus the failing leg.
    // Gateway Cleanup mission (tunnel-only): a failure is always the OUTBOUND reach now - this Director could
    // not get its tunnel up. The Callback leg (the Gateway dialing this Director back) no longer exists, so it
    // is never reported: the handshake that used to distinguish the two legs is gone with the dial-back it
    // measured. NoTailnetIdentity is a local identity failure named generically here (the panel names it
    // precisely in Step 1 repair).
    private static (GatewayConnectionVerification, GatewayConnectionFailedLeg) MapMonitor(GatewayConnectionMonitor? m)
    {
        if (m is null) return (GatewayConnectionVerification.Unknown, GatewayConnectionFailedLeg.None);
        return m.Status switch
        {
            GatewayConnectionStatus.Connected => (GatewayConnectionVerification.Connected, GatewayConnectionFailedLeg.None),
            GatewayConnectionStatus.Connecting => (GatewayConnectionVerification.Verifying, GatewayConnectionFailedLeg.None),
            GatewayConnectionStatus.Failed => (GatewayConnectionVerification.Failed, GatewayConnectionFailedLeg.OutboundReach),
            GatewayConnectionStatus.NoTailnetIdentity => (GatewayConnectionVerification.Failed, GatewayConnectionFailedLeg.None),
            _ => (GatewayConnectionVerification.Unknown, GatewayConnectionFailedLeg.None),
        };
    }

    // The chip surface applies colors; the presenter's visual state decides which (spec section 6).
    // Colors live here with the surface, not in the Core presenter. Green keeps the text muted (all is
    // well, nothing shouts); the attention and failure states color the text with the dot.
    private static (string Dot, string Text) ChipStyle(GatewayStatusBoxVisual visual) => visual switch
    {
        GatewayStatusBoxVisual.Green => ("#22C55E", "#CCCCCC"),
        GatewayStatusBoxVisual.Red => ("#EF4444", "#EF4444"),
        // Amber (needs attention) and Yellow (verifying) share the warm scheme; the chip text carries
        // the distinction ("Sign in" / "No Gateway" versus "Connecting...").
        _ => ("#F0B848", "#F0B848"),
    };

    /// <summary>
    /// One click opens the Gateway Connection panel on the resolver's current step (spec section 6):
    /// green -> the Done view, connected-but-not-signed-in -> Step 2 (sign in), everything else -> Step 1
    /// (the automatic scan / progress / named failure). First-time setup and re-sign-in are the same flow
    /// into the same panel.
    /// </summary>
    private void GatewayStatusBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            FileLog.Write("[MainWindow] GatewayStatusBox clicked; opening the connection panel on its current step");
            OpenGatewayConnectionPanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] GatewayStatusBox_PointerPressed FAILED: {ex}");
        }
    }

    private void GatewayStatusBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;

        e.Handled = true;
        try
        {
            FileLog.Write("[MainWindow] GatewayStatusBox keyboard activation; opening the connection panel on its current step");
            OpenGatewayConnectionPanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] GatewayStatusBox_KeyDown FAILED: {ex}");
        }
    }

    // Open the one reusable Gateway Connection panel in a tool window, on the resolver's current step
    // (spec section 6). CreateForCurrentState resolves the step from the live handshake state: a proven
    // connection opens on Done/Step 2, a prior failure opens Step 1 in REPAIR mode (Phase 5), everything
    // else opens the first-time scan.
    private void OpenGatewayConnectionPanel()
    {
        try
        {
            var panel = Controls.GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.StatusWindow);
            // Push an immediate status-box refresh the instant the panel settles the sign-in state, so
            // line 2 flips within a couple of seconds instead of waiting for its 30-second heartbeat poll.
            panel.AccountStateSettled += (_, _) => _ = RefreshAccountStatusAsync();
            var window = new Window
            {
                Title = "Gateway Connection",
                Width = 560,
                Height = 660,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = global::Avalonia.Media.Brush.Parse("#252526"),
                Content = panel,
            };
            // Catch-all: refresh once when the panel window closes, covering any path that signs in and
            // dismisses the window before the settled event lands.
            window.Closed += (_, _) => _ = RefreshAccountStatusAsync();
            window.Show(this);
            FileLog.Write("[MainWindow] Gateway Connection panel opened (on the resolver's current step)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OpenGatewayConnectionPanel FAILED: {ex.Message}");
        }
    }

    // ==================== GATEWAY STATUS BOX - ACCOUNT LINE (line 2) ====================

    private global::Avalonia.Threading.DispatcherTimer? _accountPollTimer;
    private bool _accountReadInFlight;

    /// <summary>How often line 2 of the status box re-reads the Gateway's signed-in status.</summary>
    private static readonly TimeSpan AccountPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wire the account line of the status box (spec section 6, line 2): a heartbeat poll that reads the
    /// connected Gateway's <c>GET /account/status</c> off the UI thread, caches the line-2 inputs, and
    /// repaints the one box. Purely informational and never a gate - the box paints immediately and updates
    /// when the first read returns, so the sidebar never waits on the network (#651/#664).
    /// </summary>
    private void WireAccountStatusPoll()
    {
        _accountPollTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = AccountPollInterval,
        };
        _accountPollTimer.Tick += AccountPollTimer_Tick;
        _accountPollTimer.Start();
        // Kick the first read immediately so line 2 resolves shortly after startup without waiting a
        // full poll interval. The timer Tick handler is the catching boundary.
        AccountPollTimer_Tick(null, EventArgs.Empty);
        FileLog.Write("[MainWindow] Account status poll started (status box line 2)");
    }

    private async void AccountPollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshAccountStatusAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] AccountPollTimer_Tick FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Read the Gateway's signed-in status off the UI thread, then fold it into the status box (spec
    /// section 6, line 2). The read is best-effort (an unreachable Gateway is a result value, never an
    /// exception out of the client), and overlapping reads are skipped so a slow Gateway cannot pile up
    /// timer ticks.
    /// </summary>
    private async Task RefreshAccountStatusAsync()
    {
        if (_accountReadInFlight) return;
        _accountReadInFlight = true;
        try
        {
            // Snapshot the config on the UI thread, then do the network read off it.
            var config = GatewayConfig.Load();
            var status = await Task.Run(() => new GatewayAccountStatusClient().GetStatusAsync(config));
            Dispatcher.UIThread.Post(() => ApplyAccountStatus(config, status));
        }
        finally
        {
            _accountReadInFlight = false;
        }
    }

    /// <summary>
    /// Fold the Gateway's account status into the status box's line-2 inputs and repaint (spec sections 4,
    /// 6): the signed-in state, the email (shown on the green line only), and whether this device holds its
    /// own token. Gateway configuration and host are refreshed with the connection monitor so an older
    /// account read can never overwrite line one after a gateway change. An unreachable or not-configured
    /// Gateway maps to a MUTED "cannot tell yet" - never a false sign-out (decision 3). The email is used
    /// only to render the identity; no token ever reaches the box (security DT-05).
    /// </summary>
    private void ApplyAccountStatus(GatewayConfig config, GatewayAccountStatus status)
    {
        _boxAccount = MapAccount(status);
        _boxAccountEmail = status.SignedIn ? status.Email : null;
        _boxDeviceKeyPresent = !string.IsNullOrWhiteSpace(config.Token);

        // Log the state and booleans, never the email (PII; CodingStyle Section 4 / 12).
        FileLog.Write($"[MainWindow] ApplyAccountStatus: account={_boxAccount}, deviceKey={_boxDeviceKeyPresent}, configured={status.GatewayConfigured}, reachable={status.Reachable}");
        UpdateGatewayStatusBox();
    }

    // Map the Gateway's account report onto the resolver's signed-in input. A not-configured Gateway is
    // Unknown and an unreachable one is Unavailable - both muted, never a false sign-out (decision 3).
    private static GatewayAccountSignInState MapAccount(GatewayAccountStatus status)
    {
        if (!status.GatewayConfigured) return GatewayAccountSignInState.Unknown;
        if (!status.Reachable) return GatewayAccountSignInState.Unavailable;
        return status.SignedIn ? GatewayAccountSignInState.SignedIn : GatewayAccountSignInState.SignedOut;
    }

    private static string? SafeHost(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // ==================== HOME (empty-state) ====================

    /// <summary>Last computed readiness, cached so a gateway change can re-tint the header
    /// without re-running the (async) tool/key probes.</summary>
    private HomeStatus? _lastHomeStatus;

    /// <summary>True when the gateway is in an error state (Failed / no tailnet identity).
    /// A simply-absent gateway is NOT an error - a local-only Director is legitimate.</summary>
    private bool _gatewayError;

    /// <summary>True when the user asked to see the status screen (View &gt; Status) while a
    /// session is open. Cleared the moment a session is selected, so the terminal returns.</summary>
    private bool _statusRequested;

    /// <summary>Show the status screen over the content area without closing any session.</summary>
    private void ShowStatusView()
    {
        FileLog.Write("[MainWindow] ShowStatusView");
        _statusRequested = true;
        UpdateHomeVisibility();
    }

    /// <summary>
    /// True when any full-content in-window overlay (Tools / Comms / Connections)
    /// is open. The Home page must not paint over an open overlay, so UpdateHomeVisibility
    /// consults this (issue #447).
    /// </summary>
    private bool IsContentOverlayOpen()
        => CommsOverlay.IsVisible
           || ConnectionsOverlay.IsVisible
           || RepositoriesOverlay.IsVisible;

    /// <summary>
    /// The configured agents changed (wizard, Settings, or Control API) - re-read the readiness facts
    /// so the board agrees with whatever just wrote them. Raised on the writer's thread, so it hops to
    /// the interface thread; going through <see cref="UpdateHomeVisibility"/> keeps the "only refresh
    /// what is actually on screen" rule in one place.
    /// </summary>
    private void OnAgentEntriesChanged()
    {
        FileLog.Write("[MainWindow] OnAgentEntriesChanged: agent store written, re-reading readiness");
        Dispatcher.UIThread.Post(UpdateHomeVisibility);
    }

    /// <summary>
    /// Show the full-screen home page exactly when this Director has zero sessions - it is
    /// the "nothing is running, here is the state, start something" screen. The window menu
    /// bar stays; the toolbar is hidden so only the home shows beneath it. The home appears
    /// (and disappears) the moment the session count crosses zero.
    /// </summary>
    private void UpdateHomeVisibility()
    {
        // The status screen sits in the main content cell (ZIndex 30, over the terminal). The
        // window chrome - toolbar and session rail - stays visible around it. It shows when
        // there are no sessions, or on demand (View > Status, _statusRequested). The content
        // overlays (Tools/Comms/etc.) sit lower in the same cell; if the status screen paints
        // over an open overlay its actions look dead (issue #447), so it yields while one is up.
        var overlayOpen = IsContentOverlayOpen();
        var showHome = (_sessions.Count == 0 || _statusRequested) && !overlayOpen;
        HomeView.IsVisible = showHome;
        FileLog.Write($"[MainWindow] UpdateHomeVisibility: showHome={showHome}, sessions={_sessions.Count}, statusRequested={_statusRequested}, overlayOpen={overlayOpen}");

        if (!showHome) return;

        HomeView.SetVersion(AppVersion.Display);
        // Paint the gateway status box from current state (no-op until the monitor is attached;
        // its Changed event repaints it once it is).
        UpdateGatewayStatusBox();
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

            var facts = await Task.Run<(List<AgentCliFact> clis, int built, int total, List<string> missing)>(() =>
            {
                // ONE authority for "does this machine have a coding agent" - the same scan the
                // first-run wizard reads, so the board and the wizard's receipt cannot answer the
                // same question differently (issue #1047).
                var clis = AgentReadiness.Scan(options)
                    .Select(f => new AgentCliFact(f.DisplayName, f.Present, f.Version))
                    .ToList();

                // Only consider tools this install is EXPECTED to provide (shim, built, or on PATH); tools
                // never installed here (extras tier, other bundles, manifest drift) must not raise a warning.
                // Availability is judged by PATH OR the bundled bin dir (issue #448), not bin-dir presence
                // alone - a machine where cc-* resolve on PATH is fully working even if this build's bin is empty.
                var catalog = new ToolCatalogService().GetCatalog();
                var expected = catalog.Where(d => d.IsExpected).ToList();
                var built = expected.Count(d => d.IsAvailable);
                var total = expected.Count;
                var missing = expected.Where(d => !d.IsAvailable).Select(d => d.Name).ToList();

                return (clis, built, total, missing);
            });

            _lastClis = facts.clis;
            _lastBuildFacts = (facts.built, facts.total, facts.missing);
            // Tools that are missing on the very first launch are the setup the installer promised
            // ("your tools finish setting up the first time you open DevThrottle"), and the automatic
            // self-heal below is about to run. Rule it as in-progress BEFORE painting, so the first
            // frame never shows the expected state as a red failure.
            _toolsSetupInProgress = _repairingTools
                || (facts.missing.Count > 0 && !_autoRepairAttempted);
            _lastHomeStatus = HomeStatusBuilder.Build(
                facts.clis, facts.built, facts.total, facts.missing, _lastToolHealth, _lastBasePythonBroken,
                _toolsSetupInProgress, _lastFleetToolCheck);

            ApplyHomeHealth();

            // Run the tool checks in the background so the tools row shows real pass/fail/not-built
            // instead of just build status. The home renders immediately; the breakdown fills in.
            _ = RefreshToolHealthAsync();

            // Startup auto self-heal: if any EXPECTED tool is broken (installed but not runnable), repair
            // it automatically - no click needed. Guarded to fire at most once per run and never loop: a
            // failed repair leaves the manual "Fix" button for a retry rather than re-triggering forever.
            if (facts.missing.Count > 0 && !_autoRepairAttempted && !_repairingTools)
            {
                _autoRepairAttempted = true;
                FileLog.Write($"[MainWindow] startup auto self-heal: {facts.missing.Count} broken tool(s) detected, repairing automatically");
                _ = RepairToolsAsync(auto: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RefreshHomeAsync FAILED: {ex.Message}");
        }
    }

    private bool _repairingTools;

    /// <summary>Set once a run when startup auto self-heal has fired, so a failed repair never loops.</summary>
    private bool _autoRepairAttempted;

    /// <summary>
    /// Health-based repair of the cc-* Python tools - from the Home "Fix it" button (<paramref name="auto"/>
    /// false) or fired automatically on startup when a broken tool is detected (<paramref name="auto"/> true).
    /// Forces a venv rebuild via <see cref="CcDirector.Setup.Engine.ToolUpdater.RepairPythonToolsAsync"/> (which
    /// is NOT version-gated, so it actually fixes a half-installed toolset the silent auto-update would skip),
    /// streams live progress onto the tools row, then re-runs the readiness check so the card flips green.
    /// Runs the slow pip work off the UI thread; guarded so a double-click cannot start two rebuilds.
    /// </summary>
    private async Task RepairToolsAsync(bool auto = false)
    {
        if (_repairingTools) return;
        _repairingTools = true;
        _toolsSetupInProgress = true;
        FileLog.Write(auto ? "[MainWindow] Tools auto self-heal started" : "[MainWindow] Tools repair requested from Home");
        try
        {
            HomeView.SetToolsRepairing(auto ? "starting" : "starting...", firstRunSetup: auto);
            var layout = CcDirector.Setup.Engine.InstallLayout.Default();
            var progress = new Progress<string>(msg => HomeView.SetToolsRepairing(msg, firstRunSetup: auto));
            var result = await Task.Run(() =>
                new CcDirector.Setup.Engine.ToolUpdater(layout).RepairPythonToolsAsync(progress));
            FileLog.Write($"[MainWindow] Tools repair done: success={result.Success}, count={result.ToolCount}, msg={result.Message}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RepairToolsAsync FAILED: {ex.Message}");
        }
        finally
        {
            _repairingTools = false;
            _lastToolHealth = null; // the tools changed - re-run the health check on the next refresh
            CcDirector.Core.Tools.ToolHealthProbe.Invalidate(); // and let every surface re-read it
            await RefreshHomeAsync();
        }
    }

    // Cached so the background tool-health pass can rebuild the home status without re-detecting CLIs.
    private List<AgentCliFact>? _lastClis;
    private (int built, int total, List<string> missing)? _lastBuildFacts;
    private CcDirector.Core.Tools.ToolHealthSummary? _lastToolHealth;
    // Cached result of the last shared base-Python runtime probe (issue #995), so the immediate home render
    // in RefreshHomeAsync reflects a known-broken runtime without re-launching the probe on the fast path.
    private bool _lastBasePythonBroken;
    // Whether the cc-devthrottle a spawned session would reach can drive THIS Director. Null means not
    // judged yet, which is rendered as nothing rather than as a pass.
    private FleetToolCheck? _lastFleetToolCheck;
    private bool _toolHealthRunning;
    // True while the tools are still being set up (first-launch provisioning or a repair in flight).
    // The status screen renders this as progress instead of a failure - see HomeCheckLevel.Busy.
    private bool _toolsSetupInProgress;

    // ---- Active cc-* tools indicator state machine (issue #829) ----
    // The rail badge is no longer a passive "fix me" warning: when drift is detected and
    // tools.autoUpdate.enabled is true it shows the orange "Syncing tools..." state, auto-runs
    // ToolReconciler.ReconcileAsync (one at a time, with backoff), returns to green when in sync,
    // and only falls to red "Tools need attention" after repeated failures. The pure transition
    // rules live in ToolsSyncStateMachine; this window owns the in-flight guard, the cooldown, and
    // the retry timer so reconcile is never thrashed.
    private readonly ToolsSyncStateMachine _toolsSync = new();
    private bool _toolsReconcileInFlight;
    private DateTime _toolsReconcileCooldownUntil = DateTime.MinValue;
    private DispatcherTimer? _toolsReconcileRetryTimer;

    /// <summary>
    /// Run every cc-* tool's checks off the UI thread and roll them up into pass/fail/not-built, then
    /// re-apply the home status so the tools row shows the real breakdown (the screenshot complaint:
    /// the home said "all systems go" while the Tools page showed a FAIL and not-built tools). Bounded
    /// concurrency, guarded against pile-up. Auth-gated tools declare no smoke test, so this is just
    /// their presence+version check; tools with a read-only smoke run that too.
    /// </summary>
    private async Task RefreshToolHealthAsync(bool force = false, bool driveSync = true)
    {
        if (_toolHealthRunning) return;
        if (_lastToolHealth is not null && !force) return; // computed once this session; reuse the cache
        _toolHealthRunning = true;
        try
        {
            // ONE health check, shared with the first-run wizard (issue #1045): ToolHealthProbe runs the
            // checks, publishes the snapshot, and logs each failure WITH its reason, so no surface has to
            // invent an answer and no failure arrives without one.
            var (summary, basePythonBroken) = await Task.Run(async () =>
            {
                var snapshot = await CcDirector.Core.Tools.ToolHealthProbe.RunAsync();
                // Probe the shared base Python directly. Every Python cc-* tool delegates to it, so if it is
                // hollow (present but cannot import its standard library) they ALL fail at once - a single,
                // repairable runtime failure the per-tool breakdown would otherwise show as N unrelated fails.
                var pyBroken = !CcDirector.Setup.Engine.PythonRuntimeProbe.IsBasePythonHealthy(
                    CcDirector.Setup.Engine.InstallLayout.Default());
                return (summary: snapshot.Summary, basePythonBroken: pyBroken);
            });

            _lastToolHealth = summary;
            _lastBasePythonBroken = basePythonBroken;
            FileLog.Write($"[MainWindow] tool health: pass={summary.Pass}, fail={summary.Fail}, notBuilt={summary.NotBuilt}, broken={summary.Broken}, basePythonBroken={basePythonBroken}" +
                          (summary.Failures.Count == 0 ? "" : $", failing: {string.Join("; ", summary.Failures)}"));

            // Same rule as the fast path: a broken runtime that the automatic self-heal below is about
            // to repair is unfinished setup, not a fault, and is painted as progress rather than red.
            _toolsSetupInProgress = _repairingTools || (basePythonBroken && !_autoRepairAttempted);

            if (_lastClis is { } clis && _lastBuildFacts is { } bf)
            {
                _lastHomeStatus = HomeStatusBuilder.Build(
                    clis, bf.built, bf.total, bf.missing, summary, basePythonBroken, _toolsSetupInProgress,
                    _lastFleetToolCheck);
                ApplyHomeHealth();
            }

            // Startup auto self-heal for a hollow shared Python runtime (issue #995): the tool exes exist and
            // resolve on PATH, so the missing-tools trigger in RefreshHomeAsync never fires - yet every Python
            // tool is failing because the shared base Python cannot start. Repair re-provisions it. Guarded to
            // fire at most once per run so a failed repair leaves the manual "Fix" button rather than looping.
            if (basePythonBroken && !_autoRepairAttempted && !_repairingTools)
            {
                _autoRepairAttempted = true;
                FileLog.Write("[MainWindow] startup auto self-heal: shared base Python runtime is broken, repairing automatically");
                _ = RepairToolsAsync(auto: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RefreshToolHealthAsync FAILED: {ex.Message}");
        }
        finally
        {
            _toolHealthRunning = false;
        }

        // Drive the active tools indicator off this fresh health snapshot (issue #829). Skipped on the
        // post-reconcile re-probe (driveSync=false), where RunToolsReconcileAsync records the attempt
        // outcome itself so the success/failure bookkeeping is not double-counted.
        if (driveSync)
            await DriveToolsSyncAsync();
    }

    /// <summary>
    /// Evaluate the active cc-* tools indicator (issue #829) against the latest health snapshot and, when
    /// warranted, start an automatic reconcile. What counts as a problem, and which kind, is decided by
    /// <see cref="ClassifyToolsProblemAsync"/>: only drift a reconcile can actually correct starts one, and
    /// a tool that is installed but failing is reported rather than looped on (issue #1045). When
    /// auto-update is OFF the indicator behaves exactly as it did before issue #829: a passive warning on
    /// the row, no auto-reconcile. Starting a reconcile is debounced (one in flight) and cooldown-gated
    /// (backoff between attempts) so the badge never thrashes the reconcile engine.
    /// </summary>
    private async Task DriveToolsSyncAsync()
    {
        // Re-ask "can a session I spawn actually drive me?" alongside the tool health run, so the badge
        // reflects the PATH as it is now. A repair, an installer run, or another Director starting can
        // change the answer without anything on this machine's disk looking different.
        await RefreshFleetToolReachabilityAsync();

        // Re-fold the home rows now the verdict exists. This check finishes AFTER the home page is first
        // built, so without this the page keeps the answer it had before the question was asked - which is
        // how it came to print "All systems go" while the log already held the failure.
        if (_lastClis is { } homeClis && _lastBuildFacts is { } homeFacts)
        {
            _lastHomeStatus = HomeStatusBuilder.Build(
                homeClis, homeFacts.built, homeFacts.total, homeFacts.missing, _lastToolHealth,
                _lastBasePythonBroken, _toolsSetupInProgress, _lastFleetToolCheck);
            ApplyHomeHealth();
        }

        var enabled = ToolAutoUpdateSetting.Get();
        var (reconcilableDrift, unreconcilableFault) = await ClassifyToolsProblemAsync(probeReconciler: enabled);

        var previousState = _toolsSync.State;
        var decision = _toolsSync.Evaluate(reconcilableDrift, unreconcilableFault, enabled, _toolsReconcileInFlight);
        if (decision.State != previousState)
            FileLog.Write($"[MainWindow] tools indicator state: {previousState} -> {decision.State} " +
                          $"(reconcilableDrift={reconcilableDrift}, toolFault={unreconcilableFault}, autoUpdate={enabled})");

        UpdateToolsIndicator();

        // Start a reconcile only when the machine asks for one, nothing is in flight, no other tool repair is
        // running, and the backoff cooldown has elapsed - the no-thrash guarantee.
        if (decision.ShouldReconcile
            && !_toolsReconcileInFlight
            && !_repairingTools
            && DateTime.UtcNow >= _toolsReconcileCooldownUntil)
        {
            StartToolsReconcile();
        }
    }

    /// <summary>
    /// Split the tools problem into the two things it can be, because they want opposite responses
    /// (issue #1045):
    ///
    ///   reconcilable drift - a missing shim, an orphaned legacy alias, a broken or absent shared venv, a
    ///     tool the install was meant to provide but did not. <c>ToolReconciler</c> has a mechanism for
    ///     each of these, so an automatic attempt is warranted.
    ///   an unreconcilable tool fault - the tool IS installed, its shim is there, the venv is healthy, and
    ///     it still fails its own check. No reconcile touches this. Feeding it to one anyway is how a clean
    ///     install came to run three reconciles that each correctly reported in-sync, count all three as
    ///     ineffective, and land on a red badge that named no reason.
    ///
    /// <paramref name="probeReconciler"/> gates the extra read-only <c>HasDrift</c> probe: it is worth
    /// running when we would act on the answer (auto-update on, or judging a reconcile that just finished)
    /// and not otherwise. It is skipped while a reconcile is in flight - it would be reading a moving target.
    /// </summary>
    /// <summary>
    /// Ask whether the cc-devthrottle a spawned session would reach can actually drive THIS Director, and
    /// keep the answer for the badge and the repair panel.
    ///
    /// This is a different question from every other tools check, which is why it needed its own: the rest
    /// ask whether the tools this install placed are present and working. This one asks whether the tool
    /// PATH resolves is OURS. A machine can pass all of those and still hand every agent a cc-devthrottle
    /// from an older install that cannot authenticate - the Director healthy and connected, every session
    /// reporting "cannot connect to DevThrottle".
    /// </summary>
    /// <summary>
    /// THIS Director's own tool directory - the one whose cc-devthrottle can drive it.
    ///
    /// Deliberately NOT <c>InstallLayout.Default().BinDir</c>. That resolves to the flat
    /// %LOCALAPPDATA%\cc-director\bin, which predates instance homes: on a machine upgraded through the
    /// move to <c>instances\&lt;slug&gt;</c> it is exactly where the SUPERSEDED tools were left behind.
    /// Using it made the check name the broken directory as the good one, so a machine whose PATH
    /// resolved that stale copy reported "same install" and offered no repair - and had the button been
    /// offered, it would have repointed PATH at the stale copy it was supposed to escape.
    ///
    /// Storage is per instance, so the tools that belong to this Director are under ITS home.
    /// </summary>
    private static string OwnToolBinDir()
        => Path.Combine(CcDirector.Core.Instances.InstanceContext.InstanceHome, "bin");

    private async Task RefreshFleetToolReachabilityAsync()
    {
        // The tools reach the fleet through the Gateway with a session key, so the probe needs what a
        // real session gets: the Gateway's address and a freshly minted, freshly REGISTERED key. The
        // host owns both.
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null)
            return; // no verdict is not a pass: leave the previous answer (or none) standing

        try
        {
            var credential = await host.MintFleetToolProbeCredentialAsync();
            if (credential is null)
            {
                // No Gateway configured, or the tunnel is down: there is nothing for the tools to
                // reach, which is the mission's accepted no-Gateway state - a fact about this
                // machine's connection, never a fault in the toolbelt. Recorded as its own verdict
                // so nothing paints the install-repair banner for it.
                _lastFleetToolCheck = new FleetToolCheck(
                    FleetToolVerdict.NoGateway, null, OwnToolBinDir(),
                    "No Gateway connection right now, so the fleet tools have nothing to reach. "
                    + "Agent tooling requires the Gateway.");
                return;
            }

            try
            {
                _lastFleetToolCheck = await new FleetToolReachability()
                    .RunAsync(credential.GatewayUrl, credential.SessionKey, OwnToolBinDir());
            }
            finally
            {
                // The probe key is bound to an id that never joins the session roster, so nothing
                // else will ever end it - this is its one revocation.
                credential.Revoke();
            }
        }
        catch (CcDirector.ControlApi.GatewayRefusedSessionKeyException ex)
        {
            // The Gateway is there and refusing us. This is a VERDICT, not an absence of one, and it
            // has to be its own: no verdict renders as no Sessions row, so this exact failure - every
            // session in the fleet locked out - used to leave the Home page blank while the log named
            // the cause every ten seconds (#2457, #2459). It is the one screen that should have said
            // so, and it said nothing.
            //
            // Not the no-Gateway verdict either: that is the benign accepted trade, and dressing a
            // live refusal in it would be worse than silence.
            FileLog.Write($"[MainWindow] the Gateway did NOT accept this Director's session key: {ex.Message}");
            _lastFleetToolCheck = new FleetToolCheck(
                FleetToolVerdict.GatewayRefusedKey, null, OwnToolBinDir(),
                "The Gateway is connected but did not accept this Director's session key, so every "
                + "session's command line is answered 401. The most common cause is a Gateway older "
                + "than this Director, which needs deploying - but the registration only reports that "
                + "it did not land, not why, so a transport failure looks the same from here. Nothing "
                + "on this machine can repair either one.");
        }
        catch (Exception ex)
        {
            // Never let a probe failure take the window down, and never let it read as a pass either.
            // NO VERDICT is the honest outcome for anything we cannot name - an unexplained failure
            // must not be handed a confident label just because a label is available.
            FileLog.Write($"[MainWindow] RefreshFleetToolReachabilityAsync FAILED: {ex.Message}");
            _lastFleetToolCheck = null;
        }
    }

    private async Task<(bool ReconcilableDrift, bool UnreconcilableFault)> ClassifyToolsProblemAsync(bool probeReconciler)
    {
        var health = _lastToolHealth;

        // A tool missing from the install is repairable drift; a tool present-but-failing is not. Before the
        // first health run there is no verdict, so fall back to the tools row: unjudged is not a pass, and
        // whatever it reports is treated as reconcilable so the existing self-heal still fires.
        var toolsCheck = _lastHomeStatus?.Checks.FirstOrDefault(c => c.Title == HomeStatusBuilder.ToolsRowTitle);
        var rowNotOk = toolsCheck is not null && toolsCheck.Level != HomeCheckLevel.Ok;
        var reconcilableDrift = health is { } h ? h.HasMissingTool : rowNotOk;
        var unreconcilableFault = health is { } h2 && h2.HasFailingTool;

        // The shared base Python being hollow takes every Python tool down at once and IS repairable, so it
        // counts as drift rather than a per-tool fault.
        if (_lastBasePythonBroken) reconcilableDrift = true;

        // A cc-devthrottle that cannot reach the Gateway is a fault no reconcile touches: the reconciler
        // repairs THIS install's shims and venv, all of which can be perfect while PATH still resolves an
        // older install's copy first. Feeding it to a reconcile would spend the attempt budget on three
        // guaranteed no-ops and land on a red badge naming no reason - exactly the shape issue #1045 fixed.
        if (_lastFleetToolCheck is { Verdict: FleetToolVerdict.CannotReachGateway })
            unreconcilableFault = true;

        if (probeReconciler && !reconcilableDrift && !_toolsReconcileInFlight)
        {
            reconcilableDrift = await Task.Run(() =>
                new CcDirector.Setup.Engine.ToolReconciler(CcDirector.Setup.Engine.InstallLayout.Default()).HasDrift());
        }

        return (reconcilableDrift, unreconcilableFault);
    }

    /// <summary>
    /// Kick off a single automatic reconcile for the active indicator. The orange "Syncing tools..." state is
    /// already set by <see cref="ToolsSyncStateMachine.Evaluate"/>; this renders it immediately (responsive
    /// &lt;100ms, before any awaited work) and then runs the reconcile off the UI thread.
    /// </summary>
    private void StartToolsReconcile()
    {
        _toolsReconcileInFlight = true;
        FileLog.Write("[MainWindow] tools auto-reconcile starting (indicator Syncing)");
        UpdateToolsIndicator(); // paint orange now - the reconcile runs in the background
        _ = RunToolsReconcileAsync();
    }

    /// <summary>
    /// Run one reconcile, re-probe drift, and record the outcome on the state machine. Success (the reconcile
    /// did not fail AND drift is gone) returns the badge to green; otherwise it counts a failed attempt and,
    /// below the retry ceiling, schedules a backoff retry. At the ceiling the machine is already in
    /// <see cref="ToolsIndicatorState.NeedsAttention"/> (red) and no further retry is scheduled.
    /// </summary>
    private async Task RunToolsReconcileAsync()
    {
        CcDirector.Setup.Engine.ReconcileOutcome outcome;
        try
        {
            var result = await Task.Run(() =>
                new CcDirector.Setup.Engine.ToolReconciler(CcDirector.Setup.Engine.InstallLayout.Default()).ReconcileAsync());
            outcome = result.Outcome;
            FileLog.Write($"[MainWindow] tools auto-reconcile done: outcome={outcome}, actions={result.Actions.Count}" +
                          (result.Error is null ? "" : $", error={result.Error}"));
        }
        catch (Exception ex)
        {
            outcome = CcDirector.Setup.Engine.ReconcileOutcome.Failed;
            FileLog.Write($"[MainWindow] tools auto-reconcile FAILED: {ex.Message}");
        }
        finally
        {
            _toolsReconcileInFlight = false;
        }

        // Re-probe health so we judge against the post-reconcile reality (driveSync:false - we record the
        // outcome here rather than letting the snapshot path start another reconcile).
        _lastToolHealth = null;
        CcDirector.Core.Tools.ToolHealthProbe.Invalidate();
        await RefreshToolHealthAsync(force: true, driveSync: false);

        // Judge the reconcile against the drift a reconcile is FOR. A tool that is installed and still
        // fails its own check was never this pass's job (see ClassifyToolsProblemAsync), so counting it
        // against the attempt made the reconcile look ineffective when it had nothing left to do.
        var (reconcilableDrift, unreconcilableFault) = await ClassifyToolsProblemAsync(probeReconciler: true);
        var reconcileFailed = outcome == CcDirector.Setup.Engine.ReconcileOutcome.Failed;

        var previousState = _toolsSync.State;
        // One entry point, and it takes the drift verdict - so there is no way to record success while the
        // drift this pass was reconciling is still standing (issue #1045).
        _toolsSync.OnReconcileFinished(reconcileFailed, reconcilableDrift);

        if (_toolsSync.State == ToolsIndicatorState.InSync && unreconcilableFault)
        {
            // The layout is now correct and a tool still does not work. That is a real to-do, not a sync
            // problem: say so once, rather than retrying a reconcile that has nothing left to correct.
            _toolsSync.OnUnreconcilableFault();
        }

        var failing = _lastToolHealth is { } h && h.Failures.Count > 0
            ? $", failing: {string.Join("; ", h.Failures)}"
            : "";
        FileLog.Write($"[MainWindow] tools indicator state: {previousState} -> {_toolsSync.State} " +
                      $"(reconcile outcome={outcome}, reconcilableDrift={reconcilableDrift}, toolFault={unreconcilableFault}, " +
                      $"ineffectiveAttempts={_toolsSync.ConsecutiveFailures}/{ToolsSyncStateMachine.MaxReconcileAttempts}{failing})");

        if (_toolsSync.State == ToolsIndicatorState.InSync)
            _toolsReconcileCooldownUntil = DateTime.MinValue;
        else if (_toolsSync.State == ToolsIndicatorState.Syncing)
            ScheduleToolsReconcileRetry();

        UpdateToolsIndicator();
    }

    /// <summary>
    /// Schedule the next reconcile attempt after the state machine's backoff, so retries are spaced out
    /// instead of tight-looped. One-shot: it fires once, then re-drives the indicator which (if drift still
    /// stands and the cooldown has elapsed) starts the next reconcile.
    /// </summary>
    private void ScheduleToolsReconcileRetry()
    {
        var backoff = _toolsSync.NextBackoff();
        _toolsReconcileCooldownUntil = DateTime.UtcNow + backoff;
        FileLog.Write($"[MainWindow] tools auto-reconcile retry scheduled in {backoff.TotalSeconds:0}s");

        _toolsReconcileRetryTimer?.Stop();
        _toolsReconcileRetryTimer = new DispatcherTimer { Interval = backoff };
        _toolsReconcileRetryTimer.Tick += async (_, _) =>
        {
            _toolsReconcileRetryTimer?.Stop();
            try
            {
                await DriveToolsSyncAsync();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] tools auto-reconcile retry FAILED: {ex.Message}");
            }
        };
        _toolsReconcileRetryTimer.Start();
    }

    /// <summary>
    /// Combine the cached readiness with the live gateway state into the status screen.
    /// "Healthy" requires every check green AND the gateway not in an error state. When
    /// healthy the screen is quiet (all-clear); otherwise it lists only the failing checks.
    /// Cheap and idempotent: called after a refresh and on every gateway change.
    /// </summary>
    private void ApplyHomeHealth()
    {
        // The rail's cc-* tools indicator lives outside the home view, so paint it first and
        // unconditionally (it must hide itself when there is no status yet, too).
        UpdateToolsIndicator();

        if (_lastHomeStatus is not { } status) return;

        var healthy = status.AllReady && !_gatewayError;
        var summary = _gatewayMonitor?.Status == GatewayConnectionStatus.Connected
            ? "Gateway connected - ready to work"
            : "Ready to start a session";
        // When all tools pass, show the count (and any optional not-installed) quietly here rather than
        // as an alarm, so a healthy machine reads "24 of 29 tools passing - 5 not installed".
        if (_lastToolHealth is { } h && h.Total > 0)
        {
            summary = $"{h.Pass} of {h.Total} tools passing";
            if (h.NotBuilt > 0) summary += $" - {h.NotBuilt} not installed";
        }
        HomeView.SetStatus(status, healthy, _gatewayError, summary);
    }

    // Indicator palettes (issue #829). Inline one-off colors for this single rail control, as the
    // VisualStyle guide permits for one-offs; kept here as named brushes so each state reads clearly.
    private static readonly IBrush SyncOrangeBorder = new SolidColorBrush(Color.Parse("#E0822E"));
    private static readonly IBrush SyncOrangeBackground = new SolidColorBrush(Color.Parse("#3A2B17"));
    private static readonly IBrush SyncOrangeText = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush SyncOrangeSub = new SolidColorBrush(Color.Parse("#C99A6A"));
    private static readonly IBrush WarnAmberBorder = new SolidColorBrush(Color.Parse("#F0B848"));
    private static readonly IBrush WarnAmberBackground = new SolidColorBrush(Color.Parse("#3A331B"));
    private static readonly IBrush WarnAmberText = new SolidColorBrush(Color.Parse("#F0B848"));
    private static readonly IBrush WarnAmberSub = new SolidColorBrush(Color.Parse("#B59868"));
    private static readonly IBrush AttentionRedBorder = new SolidColorBrush(Color.Parse("#E0574A"));
    private static readonly IBrush AttentionRedBackground = new SolidColorBrush(Color.Parse("#3A1E1B"));
    private static readonly IBrush AttentionRedText = new SolidColorBrush(Color.Parse("#F0746A"));
    private static readonly IBrush AttentionRedSub = new SolidColorBrush(Color.Parse("#C98A82"));
    private static readonly IBrush GlyphForeground = new SolidColorBrush(Color.Parse("#1E1E1E"));

    /// <summary>
    /// Paint the rail's cc-* tools indicator from the active state machine (issue #829). InSync hides the
    /// badge; Syncing shows the orange "Syncing tools..." state with a live progress spinner; Warning is the
    /// legacy passive amber warning (auto-update off); NeedsAttention is the red "Tools need attention"
    /// to-do after repeated reconcile failures. The Warning/NeedsAttention states are clickable (open
    /// Settings on the Tools tab); the transient Syncing state is not a to-do, so its cursor is the arrow.
    /// </summary>
    private void UpdateToolsIndicator()
    {
        var toolsCheck = _lastHomeStatus?.Checks.FirstOrDefault(c => c.Title == HomeStatusBuilder.ToolsRowTitle);
        var detail = toolsCheck?.Detail ?? "";

        // Never say the same thing twice. When the status screen is up it already carries the tools
        // row, so a rail badge repeating it is the same problem reported in two places at once - which
        // is how the very first frame came to show one Python failure as two red alarms.
        if (HomeView.IsVisible)
        {
            ToolsIndicator.IsVisible = false;
            ToolsIndicatorSpinner.IsVisible = false;
            return;
        }

        switch (_toolsSync.State)
        {
            case ToolsIndicatorState.Syncing:
                ToolsIndicator.IsVisible = true;
                ToolsIndicator.Background = SyncOrangeBackground;
                ToolsIndicator.BorderBrush = SyncOrangeBorder;
                ToolsIndicator.Cursor = new Cursor(StandardCursorType.Arrow);
                ToolsIndicatorDot.Fill = SyncOrangeBorder;
                ToolsIndicatorGlyph.IsVisible = false;
                ToolsIndicatorLabel.Text = "Syncing tools...";
                ToolsIndicatorLabel.Foreground = SyncOrangeText;
                ToolsIndicatorSub.Text = "updating the DevThrottle tools";
                ToolsIndicatorSub.Foreground = SyncOrangeSub;
                ToolsIndicatorSpinner.IsVisible = true;
                ToolTip.SetTip(ToolsIndicator, "Bringing the DevThrottle tools back in sync...");
                break;

            case ToolsIndicatorState.NeedsAttention:
                ToolsIndicator.IsVisible = true;
                ToolsIndicator.Background = AttentionRedBackground;
                ToolsIndicator.BorderBrush = AttentionRedBorder;
                ToolsIndicator.Cursor = new Cursor(StandardCursorType.Hand);
                ToolsIndicatorDot.Fill = AttentionRedBorder;
                ToolsIndicatorGlyph.IsVisible = true;
                ToolsIndicatorSpinner.IsVisible = false;
                ToolsIndicatorLabel.Foreground = AttentionRedText;
                ToolsIndicatorSub.Foreground = AttentionRedSub;

                // "Tools need attention" is true but useless when the fault is that PATH points somewhere
                // else: the tools ARE fine, they just belong to a different install. Naming the real thing
                // is what stops an agent - and the owner - blaming the network for an hour.
                if (_lastFleetToolCheck is { Verdict: FleetToolVerdict.CannotReachGateway } fleetFault)
                {
                    ToolsIndicatorLabel.Text = "Sessions cannot reach the fleet";
                    ToolsIndicatorSub.Text = fleetFault.IsDifferentInstall
                        ? "the command line on your PATH is from another install"
                        : fleetFault.Detail;
                    ToolTip.SetTip(ToolsIndicator,
                        "Agents in your sessions will report \"cannot connect to DevThrottle\",\n" +
                        "even though this Director's own Gateway connection is healthy.\n\n" +
                        $"Your PATH gives: {fleetFault.ResolvedPath}\n" +
                        $"This Director is: {fleetFault.ExpectedBinDir}\n\n" +
                        "Click to open Settings and repair it.");
                }
                else
                {
                    ToolsIndicatorLabel.Text = "Tools need attention";
                    ToolsIndicatorSub.Text = string.IsNullOrEmpty(detail) ? "click to open Settings and repair" : detail;
                    // Not "automatic sync did not resolve it": red is now also reached for a tool that is
                    // installed and simply does not work, where no sync was ever the right answer and none
                    // was attempted. The sub-label above carries the specific reason (issue #1045).
                    ToolTip.SetTip(ToolsIndicator,
                        "The DevThrottle tools are not all working.\nClick to open Settings and repair the tools.");
                }
                break;

            case ToolsIndicatorState.Warning:
                ToolsIndicator.IsVisible = true;
                ToolsIndicator.Background = WarnAmberBackground;
                ToolsIndicator.BorderBrush = WarnAmberBorder;
                ToolsIndicator.Cursor = new Cursor(StandardCursorType.Hand);
                ToolsIndicatorDot.Fill = WarnAmberBorder;
                ToolsIndicatorGlyph.IsVisible = true;
                ToolsIndicatorLabel.Text = HomeStatusBuilder.ToolsRowTitle;
                ToolsIndicatorLabel.Foreground = WarnAmberText;
                ToolsIndicatorSub.Text = detail;
                ToolsIndicatorSub.Foreground = WarnAmberSub;
                ToolsIndicatorSpinner.IsVisible = false;
                ToolTip.SetTip(ToolsIndicator,
                    $"Some DevThrottle tools are missing or failing ({detail}).\nClick to open Settings and download/repair the tools.");
                break;

            case ToolsIndicatorState.InSync:
            default:
                ToolsIndicator.IsVisible = false;
                ToolsIndicatorSpinner.IsVisible = false;
                break;
        }
    }

    private async void ToolsIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // The orange Syncing state is a transient progress signal, not a to-do - a click does nothing
            // (the Director is already fixing the drift automatically). Only the actionable Warning /
            // NeedsAttention states open Settings.
            if (_toolsSync.State == ToolsIndicatorState.Syncing)
            {
                FileLog.Write("[MainWindow] ToolsIndicator clicked while Syncing - ignored (auto-reconcile in progress)");
                return;
            }

            FileLog.Write("[MainWindow] ToolsIndicator clicked -> opening Settings on the Tools tab");
            await OpenSettingsAsync(onToolsTab: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ToolsIndicator_PointerPressed FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Title-bar suffix identifying which instance this window is (e.g. " -- Company B").
    /// Only shown when MORE THAN ONE instance is registered on this machine - that is the
    /// suffix's entire purpose. A single-instance install shows no suffix, so a new user
    /// never sees an internal instance name in the title bar.
    /// </summary>
    private static string InstanceTitleSuffix()
    {
        try
        {
            if (NamedInstanceRegistry.List().Count <= 1)
                return "";
        }
        catch (Exception ex)
        {
            // Same rule as Program.ResolveInstance: instance resolution must never stop the app.
            // An unreadable registry costs the cosmetic suffix, not the window.
            FileLog.Write($"[MainWindow] InstanceTitleSuffix: registry read FAILED, showing no suffix: {ex.Message}");
            return "";
        }
        var name = InstanceContext.DisplayName ?? InstanceContext.Slug;
        return string.IsNullOrWhiteSpace(name) ? "" : $" -- {name}";
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

    /// <summary>
    /// Everything that must be true of a workspace BEFORE anything is closed, checked as one answer.
    ///
    /// It is separate from the load, and it runs first, because loading a workspace CLOSES THE RUNNING
    /// FLEET. Anything discovered after that point is discovered too late: the sessions the user had are
    /// already gone, and the choice left is between a half-started workspace and an empty Director.
    /// </summary>
    /// <param name="workspace">The workspace about to be started.</param>
    /// <returns>Every reason it cannot be started, or an empty list.</returns>
    private static IReadOnlyList<string> WorkspaceCannotStartBecause(WorkspaceDocument workspace)
    {
        var problems = new List<string>();

        foreach (var seat in workspace.Seats.OrderBy(x => x.SortOrder))
        {
            var raw = (seat.Agent ?? "").Trim();
            if (raw.Length > 0
                && (raw.Contains(',')
                    || !Enum.TryParse<AgentKind>(raw, ignoreCase: true, out var kind)
                    || !Enum.IsDefined(kind)))
                problems.Add($"\"{seat.Name}\" names the agent \"{raw}\", which this build cannot run.");

            // The repository is checked HERE and not at create time, because create time is after the
            // close. A moved or unmounted repository is the ordinary way this fails - a workspace saved
            // months ago naming a worktree that has since been removed.
            if (string.IsNullOrWhiteSpace(seat.RepoPath))
                problems.Add($"\"{seat.Name}\" names no repository.");
            else if (!Directory.Exists(seat.RepoPath))
                problems.Add($"\"{seat.Name}\" names the repository {seat.RepoPath}, which is not there.");

            // FIELDS THIS PATH CANNOT HONOUR. The schema carries a seat's mission, role, controller,
            // workflow run and opening prompt, and this cold start passes none of them to CreateSession -
            // so starting such a seat produces a session with the right name and the wrong identity, and
            // nothing about it looks wrong afterwards. Refusing is the honest answer until the seeded
            // start of Phase 5 can honour them.
            var unhonoured = new List<string>();
            if (!string.IsNullOrWhiteSpace(seat.Role)) unhonoured.Add("role");
            if (seat.Mission is not null && (seat.Mission.Id is not null || seat.Mission.Name is not null))
                unhonoured.Add("mission");
            if (!string.IsNullOrWhiteSpace(seat.ReportsTo)) unhonoured.Add("controller");
            if (!string.IsNullOrWhiteSpace(seat.WorkflowRunId)) unhonoured.Add("workflow run");
            if (!string.IsNullOrWhiteSpace(seat.OpeningPrompt)) unhonoured.Add("opening prompt");
            if (unhonoured.Count > 0)
                problems.Add(
                    $"\"{seat.Name}\" carries a {string.Join(", ", unhonoured)}, which starting a " +
                    "workspace cold cannot set. Starting it would give you a session with the right name " +
                    "and the wrong identity.");
        }

        return problems;
    }

    /// <summary>
    /// Start every seat in a workspace (issue #2722). The workspace itself came from the Gateway; this
    /// only starts what it names.
    ///
    /// This is the COLD start - each seat begins with no history. Starting a seat SEEDED by its own
    /// handover, which is what a restart needs, is Phase 5 of issue #2719 and is not this path.
    ///
    /// IT NEVER REPORTS A SUCCESS IT DID NOT ACHIEVE. The window used to say "Workspace loaded"
    /// unconditionally - after the running fleet had been closed, and whether or not anything came up in
    /// its place. A destructive operation that partially completes and reports success is the worst shape
    /// this product has: the user has lost what they had, has not got what they asked for, and has been
    /// told it worked.
    /// </summary>
    /// <param name="workspace">The workspace to start. Already checked by
    /// <see cref="WorkspaceCannotStartBecause"/> - this is called only after the fleet has been closed.</param>
    private async Task LoadWorkspaceAsync(WorkspaceDocument workspace)
    {
        FileLog.Write($"[MainWindow] LoadWorkspaceAsync: '{workspace.Name}' with {workspace.Seats.Count} seats");

        var progress = new WorkspaceProgressDialog(workspace.Name);
        progress.Show(this);

        try
        {
            var sorted = workspace.Seats.OrderBy(s => s.SortOrder).ToList();
            int total = sorted.Count;
            var started = 0;
            var failed = new List<string>();

            for (int i = 0; i < total; i++)
            {
                var entry = sorted[i];
                FileLog.Write($"[MainWindow] LoadWorkspaceAsync: creating session {i + 1}/{total}: {entry.RepoPath}");

                progress.UpdateProgress(i + 1, total, entry.Name);

                // Issue #1635: start the seat on the agent it was saved with. Without the agentKind the
                // overload default silently made every restored session Claude Code. The value was
                // checked before anything closed, so this cannot throw here.
                var vm = CreateSession(entry.RepoPath, claudeArgs: entry.AgentArgs,
                    agentKind: ResolveSeatAgent(entry));
                if (vm != null)
                {
                    vm.Rename(entry.Name, entry.Color);
                    SaveSessionToHistory(vm);
                    started++;
                }
                else
                {
                    // CreateSession swallows a launch failure and returns null. Counting it is what
                    // turns "the loop finished" into "every seat came up", which are not the same
                    // sentence and used to produce the same screen.
                    var why = _lastSessionCreateError ?? "the session did not start";
                    failed.Add($"{entry.Name}: {why}");
                    FileLog.Write($"[MainWindow] LoadWorkspaceAsync: seat '{entry.Name}' did NOT start: {why}");
                }

                // Delay between sessions to prevent Claude Code settings corruption
                if (i < total - 1)
                    await Task.Delay(2500);
            }

            if (failed.Count == 0)
            {
                progress.SetComplete();
                FileLog.Write($"[MainWindow] LoadWorkspaceAsync: workspace '{workspace.Name}' loaded");
                await Task.Delay(600);
            }
            else
            {
                progress.SetIncomplete(started, total, string.Join("  |  ", failed));
                FileLog.Write(
                    $"[MainWindow] LoadWorkspaceAsync: workspace '{workspace.Name}' INCOMPLETE - " +
                    $"{started}/{total} started; failures: {string.Join("; ", failed)}");

                // Held open, because the fleet that was there is gone and this is the only place the
                // user is told which seats did not replace it.
                await Task.Delay(8000);
            }
        }
        finally
        {
            progress.Close();
        }
    }

    /// <summary>
    /// Which agent command line a seat starts on.
    ///
    /// A value that does not parse REFUSES rather than substituting Claude Code. The old local-file
    /// feature substituted, and that is the exact defect the agent field was added to fix (issue
    /// #1635): a seat that comes back on the wrong agent is not the session that was saved, and
    /// nothing about it looks wrong afterwards.
    ///
    /// The Gateway refuses to STORE an unknown agent, so this cannot be reached by anything it holds.
    /// It stays because the value crosses a version boundary - a workspace written by a newer build may
    /// name an agent this one cannot run - and the honest answer there is to say so, not to guess.
    /// </summary>
    private static AgentKind ResolveSeatAgent(WorkspaceSeat seat)
    {
        var raw = (seat.Agent ?? "").Trim();
        if (raw.Length == 0) return AgentKind.ClaudeCode;

        // TryParse alone accepts "999" (no such member) AND "ClaudeCode, Codex" (which combines into a
        // real one), so a comma is refused first and IsDefined follows. A session started on either would
        // be a process launched from a value nobody chose.
        if (!raw.Contains(',')
            && Enum.TryParse<AgentKind>(raw, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
            return kind;

        FileLog.Write($"[MainWindow] ResolveSeatAgent: unknown agent '{raw}' for {seat.RepoPath}");
        throw new InvalidOperationException(
            $"The seat '{seat.Name}' names the agent '{raw}', which this build cannot run. " +
            "It was probably written by a newer version - update this Director, or change the seat's " +
            "agent in the workspace.");
    }

    /// <summary>
    /// The workspace catalog for this Director: the Gateway it is connected to (issue #2722).
    /// Workspaces are not stored on this machine, so there is nothing behind this but the Gateway.
    /// </summary>
    private static IWorkspaceCatalog WorkspaceCatalog()
        => new GatewayWorkspaceCatalog(
            (global::Avalonia.Application.Current as App)?.ControlApiHost);

    /// <summary>
    /// Push any workspaces this machine saved before they lived on the Gateway up to it, once.
    /// Returns null on success, or the reason it could not be done.
    ///
    /// The reason is RETURNED rather than logged and forgotten, because a swallowed import failure
    /// shows the user a workspace list that is simply missing their saved work, with nothing said
    /// about why - a list that reads as complete and is not. The dialog puts the reason on screen.
    ///
    /// It runs on a background thread: the import reads and renames files, and this is called from a
    /// menu click, so doing it inline would freeze the window before anything had even been shown.
    /// </summary>
    private static async Task<string?> ImportLegacyWorkspacesAsync(IWorkspaceCatalog catalog)
    {
        try
        {
            var result = await Task.Run(() => LegacyWorkspaceImport.RunOnceAsync(catalog));
            if (result.Refused.Count == 0) return null;

            // A refused file is a workspace the user saved that is NOT in the list they are about to
            // look at. Saying nothing makes that list read as complete.
            return "Some workspaces saved on this machine before they moved to the Gateway were not " +
                   "imported and are still on disk: " + string.Join("; ", result.Refused);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ImportLegacyWorkspacesAsync FAILED: {ex.Message}");
            return "The workspaces saved on this machine before they moved to the Gateway could not " +
                   $"be imported: {ex.Message} They are still on disk and will be tried again.";
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
                    SourceControlView.Detach();
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

    /// <summary>
    /// Construct the <see cref="IAgent"/> strategy for the given agent kind, for the launches where
    /// no entry was picked by hand (workspace restore, the quick-launch paths). Issue #1050: the
    /// executable comes from the configured agent entry, so these launches find an agent the
    /// onboarding wizard installed off PATH exactly as <see cref="CreateAgentForEntry"/> does.
    /// </summary>
    private IAgent CreateAgent(AgentKind agentKind) =>
        AgentLaunchDefaults.CreateAgentForKind(agentKind, _sessionManager.Options);

    /// <summary>
    /// Build a catalog agent (issue #490) whose executable path is the selected entry's configured
    /// path, not the legacy per-type machine path. The agents read their path from
    /// <see cref="AgentOptions"/>, so we hand them a per-launch copy of the running options with
    /// only this entry's path slotted into the matching property; all other options (buffer sizes,
    /// keys, default Claude args) are preserved. A blank entry path falls through to the running
    /// options' default for that type so a not-yet-configured entry still launches its standard
    /// binary. RawCli is handled by the caller via <see cref="RawCliAgent"/> and never reaches here.
    /// </summary>
    private IAgent CreateAgentForEntry(AgentKind agentKind, string entryExecutablePath) =>
        AgentPluginRegistry.CreateAgentWithPathOverride(agentKind, _sessionManager.Options, entryExecutablePath);

    /// <summary>Create a session using a pre-built <see cref="IAgent"/> (e.g. a
    /// <see cref="RawCliAgent"/> constructed by the dialog).</summary>
    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId, string? userArgs, IAgent agent, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agent.Kind}, exe={agent.ExecutablePath}, group={groupId?.ToString() ?? "none"}, resume={resumeSessionId ?? "null"}");
        _lastSessionCreateError = null;
        try
        {
            // Session origin (devthrottle_internal issue #982): human at the desktop, true BY
            // CONSTRUCTION - this method only ever runs in response to this machine's own New Session
            // UI. Stamped pre-launch, because the session's first roster push (which is what creates
            // the durable history row) can leave before create even returns.
            var session = _sessionManager.CreateSession(repoPath, agent, userArgs, SessionBackendType.ConPty, resumeSessionId, groupId, groupRole, groupName,
                beforeLaunch: s => s.StampOrigin(SessionOrigin.DesktopHuman));
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

    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId = null, string? claudeArgs = null, AgentKind agentKind = AgentKind.ClaudeCode, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agentKind}, group={groupId?.ToString() ?? "none"}, resume={resumeSessionId ?? "null"}, args={claudeArgs ?? "default"}");
        _lastSessionCreateError = null;
        try
        {
            IAgent agent = CreateAgent(agentKind);
            // Session origin (issue #982): human at the desktop, by construction - see the sibling
            // overload above.
            var session = _sessionManager.CreateSession(repoPath, agent, claudeArgs, SessionBackendType.ConPty, resumeSessionId, groupId, groupRole, groupName,
                beforeLaunch: s => s.StampOrigin(SessionOrigin.DesktopHuman));
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
                "Director could not start the GitHub Actions session.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Human-readable name and install guidance for an agent CLI, shown when its
    /// executable cannot be found on PATH.
    /// </summary>
    private static (string DisplayName, string InstallHint) AgentInstallInfo(AgentKind kind)
    {
        var plugin = AgentPluginRegistry.Get(kind);
        return (plugin.DisplayName, plugin.Detection.InstallHint);
    }

    internal void SelectSession(SessionViewModel? vm)
    {
        // Selecting a session returns to its terminal, dismissing an on-demand status view.
        if (vm != null && _statusRequested)
        {
            _statusRequested = false;
            HomeView.IsVisible = false;
        }

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
        if (RepositoriesOverlay.IsVisible)
        {
            RepositoriesOverlay.IsVisible = false;
            SetRepositoriesActive(false);
        }

        if (vm == _activeSession) return;

        // Save prompt text and selected tab for outgoing session
        if (_activeSession != null)
        {
            SavePendingPrompt(_activeSession.Session);
            _activeSession.Session.SelectedTabName = _activeLeftTab;
            FileLog.Write($"[MainWindow] SelectSession: saved prompt and tab={_activeLeftTab} for {_activeSession.Session.Id}");

            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            _activeSession.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
            _activeSession.Session.OnIsTranscribingChanged -= OnActiveSessionTranscribingChanged;
            TerminalHost.Detach();
            SourceControlView.Detach();
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
            SourceControlView.Detach();
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
        // Lock the compose surface while this session is transcribing a dictated utterance in the
        // background, so a second Speak/Send/Queue cannot fire into a session mid-transcribe.
        vm.Session.OnIsTranscribingChanged += OnActiveSessionTranscribingChanged;

        // Update header
        SetSessionHeaderVisible(true);
        UpdateSessionHeader();

        // Attach terminal
        PlaceholderText.IsVisible = false;
        TerminalDock.IsVisible = true;
        TerminalHost.Attach(vm.Session);
        UpdateScrollBar();

        // Attach source control (hide tab if no .git). The Worktrees page renders the background
        // repository monitor's model - the same brain as the Repositories home.
        if (global::Avalonia.Application.Current is App scApp)
            SourceControlView.Attach(scApp.RepositoryMonitor, vm.Session.RepoPath);
        UpdateSourceControlTabVisibility(vm.Session.RepoPath);

        // Show prompt bar
        PromptBarBorder.IsVisible = true;

        // Restore prompt text for incoming session - and which of its characters were dictated (ruling
        // R20). The box's own hook will hear the new text later (it is posted, not raised inline) and find
        // nothing to change, because the record is told the text and its spans here, first.
        PromptInput.Text = vm.Session.PendingPromptText ?? "";
        PromptInput.CaretIndex = PromptInput.Text.Length;
        _composerProvenance.Restore(PromptInput.Text, vm.Session.PendingPromptSpokenSpans);

        // Restore last selected tab. The Session/Agent tabs, the Voice/History tabs, and the
        // Wingman tab were removed. Normalize any persisted values from older builds and default
        // to Terminal.
        var tabName = vm.Session.SelectedTabName;
        if (string.Equals(tabName, "Session", StringComparison.Ordinal) ||
            string.Equals(tabName, "Agent", StringComparison.Ordinal) ||
            string.Equals(tabName, "Voice", StringComparison.Ordinal) ||
            string.Equals(tabName, "History", StringComparison.Ordinal) ||
            string.Equals(tabName, "Wingman", StringComparison.Ordinal))
            tabName = "Terminal";
        if (string.IsNullOrEmpty(tabName)) tabName = "Terminal";
        if (tabName != _activeLeftTab)
            SwitchLeftTab(tabName);

        // Switch document tabs to new session
        SwitchDocumentTabsToSession(vm.Session.Id);

        // Refresh right panel for new session
        RefreshQueuePanel();

        // Apply the incoming session's transcribing lock (usually unlocked; locked if you switch to a
        // session that is still transcribing a dictated utterance in the background).
        ApplyComposeLock(vm.Session.IsTranscribing);

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

    /// <summary>True while the on-screen session is transcribing a dictated utterance in the
    /// background (the Speak-Send fire-and-forget window). The compose actions and their keyboard
    /// shortcuts no-op during this brief window so a second action cannot race the dictated prompt.</summary>
    private bool IsActiveSessionTranscribing() => _activeSession?.Session.IsTranscribing == true;

    /// <summary>Fires (possibly off the UI thread) when the active session's transcribing flag flips.
    /// Marshals to the UI thread and locks or unlocks the compose surface.</summary>
    private void OnActiveSessionTranscribingChanged(bool isTranscribing)
    {
        Dispatcher.UIThread.Post(() => ApplyComposeLock(isTranscribing));
    }

    /// <summary>
    /// Lock (or unlock) the prompt-bar compose surface for the active session. While a dictated
    /// utterance transcribes and submits in the background, the input box, Send, Speak, Queue
    /// and Handover are disabled, and the action bar's Clear context / History are disabled
    /// via <see cref="Controls.SessionActionBar.SetTranscribingLock"/> - Stop and Interrupt stay live.
    /// The keyboard shortcuts (Ctrl+H / Ctrl+Enter / Ctrl+Shift+Enter) are guarded separately by
    /// <see cref="IsActiveSessionTranscribing"/> so they no-op too. Unlocks automatically when the
    /// background send clears the flag.
    /// </summary>
    private void ApplyComposeLock(bool locked)
    {
        PromptInput.IsEnabled = !locked;
        BtnSend.IsEnabled = !locked;
        BtnSpeak.IsEnabled = !locked;
        BtnQueuePrompt.IsEnabled = !locked;
        BtnHandover.IsEnabled = !locked;
        ActionBar.SetTranscribingLock(locked);
        FileLog.Write($"[MainWindow] ApplyComposeLock: locked={locked}");
    }

    private void OnActiveSessionActivityChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(UpdateSessionHeader);
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
        SourceControlView.Detach();
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

        // The menu is rebuilt on every open, so a plain AlphaMode.IsEnabled check below is
        // enough to gate alpha-only items - no AlphaMode.Changed rewiring is needed here.

        // --- Section 1: the session's own identity and lifecycle ---

        var rename = new MenuItem { Header = "Rename" };
        ToolTip.SetTip(rename, "Give this session a memorable name shown in the list.");
        rename.Click += (_, _) => ShowRenameDialog(vm);

        // On-hold toggle: parks the session out of the needs-you rotation and paints its
        // list strip dark blue so you can see at a glance which sessions you've set aside.
        //
        // The lengths come from the Gateway-owned cache, read here because it never blocks - this menu is
        // rebuilt on every open and owes feedback in under 100ms. SnoozeMenuModel decides what to say.
        var snoozeMenu = SnoozeMenuModel.Build(
            vm.IsOnHold,
            (global::Avalonia.Application.Current as App)?.ControlApiHost?.SnoozeOptions.Current);

        var hold = new MenuItem { Header = snoozeMenu.ToggleHeader };
        ToolTip.SetTip(hold, vm.IsOnHold
            ? "Unsnooze this session and return it to the \"Your Turn\" rotation."
            : "Snooze this session so it drops out of the \"Your Turn\" rotation and is marked dark blue.");
        // Null length = "use my default", which is exactly what the plain click means.
        hold.Click += (_, _) => ToggleSessionHold(vm);

        // "Snooze for" - the other lengths, so a different length for THIS session is one step instead of
        // a trip to Settings and back. No choices means this desktop has not learned the lengths yet, and
        // the submenu is left off entirely rather than shown empty.
        MenuItem? snoozeFor = null;
        if (snoozeMenu.Choices.Count > 0)
        {
            snoozeFor = new MenuItem { Header = "Snooze for" };
            ToolTip.SetTip(snoozeFor, "Snooze this session for a specific length, instead of your default.");
            foreach (var choice in snoozeMenu.Choices)
            {
                var item = new MenuItem { Header = choice.Header };
                var chosen = choice.Minutes;
                item.Click += (_, _) => SetSessionSnoozeFor(vm, chosen);
                snoozeFor.Items.Add(item);
            }
        }

        // --- Section 2: open the session's repository in an external tool ---

        var openExplorer = new MenuItem { Header = "Open in Explorer" };
        ToolTip.SetTip(openExplorer, "Open this session's repository folder in File Explorer.");
        openExplorer.Click += (_, _) => OpenInExplorer(vm);

        var openVsCode = new MenuItem { Header = "Open in VS Code" };
        ToolTip.SetTip(openVsCode, "Open this session's repository in Visual Studio Code.");
        openVsCode.Click += (_, _) => OpenInVsCode(vm);

        // --- Section 3: advanced / power-user actions ---

        // Copy a full handover block (session name + id, plus this Director's identity
        // and version) to the clipboard so it can be handed to another agent (e.g. via
        // the Control API) to locate, recall from memory, and talk to this exact session.
        var copyId = new MenuItem { Header = "Copy Handover Info" };
        ToolTip.SetTip(copyId, "Copy this session's name, id and Director identity so another agent can find and talk to it.");
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

        // Save this live session as a reusable named session (issue #508): captures its repository,
        // agent and current name so it can be relaunched in one click from the New Session dialog's
        // Named Sessions tab. Saving from a running session means the repo and agent are real and
        // verified - the preset is valid the moment it is created.
        var saveNamed = new MenuItem { Header = "Save as named session" };
        ToolTip.SetTip(saveNamed, "Save this session's repository, agent and name as a reusable preset for one-click relaunch.");
        saveNamed.Click += async (_, _) =>
        {
            try
            {
                await SaveSessionAsNamedAsync(vm);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] Save as named session FAILED: {ex.Message}");
                ShowNotification("Could not save the named session");
            }
        };

        var relink = new MenuItem { Header = "Relink Session..." };
        ToolTip.SetTip(relink, "Recovery: re-point this row at a different underlying conversation transcript.");
        relink.Click += (_, _) => _ = ShowRelinkDialog(vm);

        // --- Section 4: close ---

        var close = new MenuItem { Header = "Close Session" };
        ToolTip.SetTip(close, "Close this session and remove it from the list.");
        close.Click += (_, _) => _ = CloseSessionAsync(vm);

        menu.Items.Add(rename);
        menu.Items.Add(hold);
        if (snoozeFor is not null) menu.Items.Add(snoozeFor);
        menu.Items.Add(new Separator());
        menu.Items.Add(openExplorer);
        menu.Items.Add(openVsCode);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyId);
        menu.Items.Add(saveNamed);
        menu.Items.Add(relink);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);

        menu.Open(button);
    }

    /// <summary>
    /// Save a live session as a named session (issue #508): name = the session's own name, plus its
    /// repository, its agent (resolved from <see cref="Session.AgentKind"/> back to a registered
    /// agent-entry id), and its colour. Confirms before overwriting an existing preset of the same
    /// name. The preset then appears on the New Session dialog's Named Sessions tab for one-click
    /// relaunch. Throws on failure; the caller (the menu Click handler) reports it.
    /// </summary>
    private async Task SaveSessionAsNamedAsync(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] SaveSessionAsNamedAsync: session={vm.Session.Id}");

        var name = (vm.DisplayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowNotification("Rename the session first, then save it as a named session.");
            return;
        }

        var repoPath = vm.Session.RepoPath;
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            ShowNotification("This session has no repository path to save.");
            return;
        }

        var store = new NamedSessionStore();
        var slug = NamedSessionStore.ToSlug(name);

        // A preset with this name already exists - confirm before overwriting it.
        if (store.Exists(slug))
        {
            var overwrite = await MessageBox.ShowConfirmAsync(this,
                "Named session exists",
                $"A named session called \"{name}\" already exists. Overwrite it?",
                "Overwrite", "Cancel");
            if (!overwrite)
            {
                FileLog.Write("[MainWindow] SaveSessionAsNamedAsync: user declined overwrite");
                return;
            }
        }

        // Resolve the session's agent kind back to a registered agent-entry id so the preset
        // relaunches with the same agent. No matching entry -> empty id (the preset shows
        // "Unavailable" until the agent is re-registered), which is the designed failure direction.
        var agentId = AgentEntryStore.ReadCurrentEntries()
            .FirstOrDefault(en => en.Enabled && en.Type == vm.Session.AgentKind)?.Id ?? "";

        var now = DateTimeOffset.UtcNow;
        var existing = store.Load(slug);
        var definition = new NamedSessionDefinition
        {
            Name = name,
            RepoPath = repoPath,
            AgentId = agentId,
            Color = vm.Session.CustomColor,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        if (store.Save(definition))
        {
            FileLog.Write($"[MainWindow] SaveSessionAsNamedAsync: saved \"{name}\" slug={slug} repo={repoPath} agent={agentId}");
            ShowNotification($"Saved \"{name}\" as a named session");
        }
        else
        {
            ShowNotification("Could not save the named session");
        }
    }

    /// <summary>
    /// The plain Snooze/Unsnooze click: toggles the session's hold using the user's DEFAULT length (a null
    /// length tells the Gateway to apply it).
    /// </summary>
    private void ToggleSessionHold(SessionViewModel vm) => SetSessionHold(vm, !vm.Session.OnHold, null);

    /// <summary>
    /// A "Snooze for" choice: hold this session for a specific length instead of the default. Always a
    /// hold (never an unsnooze) - picking a length while already snoozed re-arms the timer to the new
    /// length, which is the point of offering the submenu while snoozed.
    /// </summary>
    private void SetSessionSnoozeFor(SessionViewModel vm, int minutes) => SetSessionHold(vm, true, minutes);

    private async void SetSessionHold(SessionViewModel vm, bool target, int? snoozeMinutes)
    {
        // Snooze Length mission (Phase 3): snooze is Gateway-owned. Instead of setting Session.OnHold
        // in-process (which gave no timer), drive the Gateway hold seam so this snooze gets the same
        // Gateway-owned timer the phone and cockpit get. The Gateway records the snooze-until AND forwards
        // the hold back DOWN to this Director, which sets OnHold - so we never set it locally here.
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;

        // No Gateway -> no snooze (owner rule, no-fallback): require a VERIFIED connection, which proves
        // BOTH legs the round-trip needs (Director->Gateway to record, Gateway->Director to forward back).
        if (host?.GatewayMonitor.Status != GatewayConnectionStatus.Connected || host.GatewayHold is null)
        {
            ShowNotification("You need to be connected to a Gateway to use snooze.");
            return;
        }

        // Immediate feedback (<100ms); the Gateway round-trip runs async off the UI thread - it is NOT
        // always loopback (the Gateway may be on another machine over Tailscale), so it may be slow.
        var forLength = snoozeMinutes is null ? "" : $" for {Core.Configuration.SnoozeLengthText.Format(snoozeMinutes.Value)}";
        ShowNotification(target ? $"Snoozing {vm.DisplayName}{forLength}..." : $"Waking {vm.DisplayName}...");
        try
        {
            await host.GatewayHold.RecordHoldAsync(vm.Session.Id.ToString(), target, snoozeMinutes);
            FileLog.Write(
                $"[MainWindow] SetSessionHold via Gateway: session={vm.Session.Id}, onHold={target}, "
                + $"snoozeMinutes={(snoozeMinutes is null ? "default" : snoozeMinutes.ToString())}");
            ShowNotification(target ? $"{vm.DisplayName} snoozed{forLength}" : $"{vm.DisplayName} taken off snooze");
        }
        catch (Exception ex)
        {
            // Fail loud: no local OnHold set, so nothing diverges from the Gateway's truth.
            FileLog.Write($"[MainWindow] SetSessionHold FAILED: session={vm.Session.Id}: {ex.Message}");
            ShowNotification($"Could not snooze {vm.DisplayName} - {ex.Message}");
        }
    }

    /// <summary>
    /// Copies a full handover block to the clipboard: the session's display name and
    /// stable ID plus the identity of the Director hosting it (Director ID, machine,
    /// version). There is no endpoint line: the Director listens on nothing, and anything
    /// that wants this session reaches it through the Gateway by the ids in this block.
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
            $"Director ID: {app?.ControlApiHost?.DirectorId ?? "(Director host not started)"}",
            $"Machine: {Environment.MachineName}",
            $"Version: {version}",
        };

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
            }

            ShowNotification($"Session relinked to {dialog.SelectedSessionId[..8]}...");
        }
        else
        {
            FileLog.Write("[MainWindow] ShowRelinkDialog: cancelled");
        }
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

    private async Task CloseSessionAsync(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] CloseSessionAsync: session={vm.Session.Id}");

        if (_activeSession == vm)
        {
            vm.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            vm.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            vm.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
            TerminalHost.Detach();
            SourceControlView.Detach();
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
            // ONE builder, the SessionManager's (internal#625 phase 3). A hand-rolled initializer
            // used to live here and silently dropped every field it did not name - CreatedAt,
            // Number, HistoryEntryId, WorkingDirectory, mission and workflow attachment, the
            // prompt queue and a dozen more - which is why every row in sessions.json read
            // 0001-01-01 while the crash journal two lines below carried the real timestamp.
            // The rail order still wins: SyncPromptTextToSessions stamps Session.SortOrder from
            // the list index on the UI thread before the debounce, and SaveCurrentState orders
            // by it.
            _sessionManager.SaveCurrentState(app.SessionStateStore);

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
            SavePendingPrompt(_activeSession.Session);
            _activeSession.Session.SelectedTabName = _activeLeftTab;
        }
    }

    /// <summary>The compose box's text AND its provenance onto the session, text first because setting the
    /// text clears the spans (ruling R20). Runs on the UI thread.</summary>
    private void SavePendingPrompt(Session session)
    {
        var text = PromptInput.Text ?? "";
        // The provenance hears the box's changes on the dispatcher; make sure it has heard this text before its
        // spans are saved as describing it.
        _composerProvenance.TextChanged(text);
        session.PendingPromptText = text;
        session.PendingPromptSpokenSpans = _composerProvenance.Spans;
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
    /// <summary>
    /// Open the Cockpit (issue #1105). Clicking this while the gateway was unreachable used to produce FIVE
    /// stacked "Cannot Open Cockpit" windows, because the button ran an eight-second probe with no state
    /// change and no guard: for eight seconds nothing happened, so the user clicked again, and every click
    /// started its own probe ending in its own modal.
    ///
    /// THE DECISION THIS BEHAVIOUR RESTS ON, since the issue asked for it in writing. The issue offered
    /// "open the browser anyway, we know the Cockpit URL" as the fastest option. WE DO NOT KNOW IT. The
    /// Gateway owns the Cockpit URL and hands it back from GET /cockpit; the Director only knows the gateway
    /// BASE address, and composing a Cockpit URL from it is precisely the dumb-client violation that made
    /// the old Learn button point at a route that did not exist. So the probe cannot be skipped - the probe
    /// IS how we learn where to send the user.
    ///
    /// What was actually wrong was never the probe; it was the eight seconds of silence around it. So: the
    /// button says what it is doing before the network call starts, a second click cannot start a second
    /// probe, the timeout is four seconds rather than eight (comfortably longer than a real tailnet round
    /// trip, short enough not to read as broken), and the failure offers Retry instead of a bare OK, with
    /// the address it tried and where to change it.
    /// </summary>
    private async void BtnCockpit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        await BusyAction.RunAsync(button, () => OpenCockpitWithFeedbackAsync(), "Opening...", owner: this,
            failureTitle: "Cannot Open Cockpit");
    }

    /// <summary>
    /// Attempt to open the Cockpit, offering Retry for as long as the user wants one.
    ///
    /// A LOOP rather than a recursive call: Retry re-runs exactly the same attempt, and someone clicking it
    /// twenty times against a gateway that is still down should not be twenty stack frames deep by the end.
    /// </summary>
    private async Task OpenCockpitWithFeedbackAsync()
    {
        while (await TryOpenCockpitOnceAsync())
        {
            FileLog.Write("[MainWindow] BtnCockpit_Click: user chose Retry");
        }
    }

    /// <summary>One attempt, plus the dialog for each way it can fail. Returns true when the user asked to
    /// retry.</summary>
    private async Task<bool> TryOpenCockpitOnceAsync()
    {
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(GatewayConfig.Load());
        FileLog.Write($"[MainWindow] BtnCockpit_Click: asking gateway for Cockpit URL, baseUrl={baseUrl}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            // The entire fetch -> select -> OPEN decision lives in OpenCockpitAsync, off this async-void
            // handler: it fetches the DTO, and when the Gateway hands back a URL it opens THAT url verbatim
            // through the injected OpenUrlInBrowser. This handler keeps NO cockpit-URL logic and makes NO
            // browser-open call of its own, so there is nothing left here for a future edit to quietly
            // re-compose (e.g. appending "/learn"). It only decides which DIALOG to show when nothing opened.
            var url = await OpenCockpitAsync(
                () => http.GetFromJsonAsync<global::CcDirector.Gateway.Contracts.CockpitInfoDto>(baseUrl + "/cockpit"),
                OpenUrlInBrowser);
            if (url is null)
            {
                FileLog.Write($"[MainWindow] BtnCockpit_Click: gateway at {baseUrl} returned no Tailscale URL (Tailscale unavailable); opened nothing. cc-director never opens a localhost URL.");
                await new MessageDialog(
                    "Cannot Open Cockpit",
                    "Tailscale is unavailable on this machine, so there is no tailnet URL for the " +
                    "Cockpit. Bring Tailscale up and try again. Director never opens a localhost " +
                    "URL because it would only work on this one machine.")
                    .ShowDialog<bool?>(this);
            }

            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnCockpit_Click FAILED (baseUrl={baseUrl}): {ex.Message}");
            // The "is the Gateway tray app running on THIS machine?" hint only makes sense for
            // the loopback default. For a configured remote gateway the failure is about
            // reachability (the remote gateway is down, or the tailnet is unreachable).
            //
            // Retry rather than a bare OK: the user wanted the Cockpit, and a gateway that was briefly
            // unreachable is the common case. The retry runs through this same method, so it is guarded by
            // the same busy button and cannot stack dialogs either.
            var retry = await new ConfirmDialog(
                "Cannot Open Cockpit",
                BuildGatewayUnreachableMessage(baseUrl, ex.Message)
                    + "\n\nYou can change the gateway address in Settings, under Gateway.",
                confirmLabel: "Retry",
                cancelLabel: "Close")
                .ShowDialog<bool?>(this);

            return retry == true;
        }
    }

    // The dumb client opens EXACTLY the Url the Gateway hands back on GET /cockpit - it never composes a
    // path onto it (the Gateway owns the URL, CLAUDE.md rule 7). This seam pins that: OpenCockpitAsync
    // opens SelectCockpitOpenUrl(info), and a desktop test reddens if a subpath is ever appended (the
    // regression that made the old Learn button point at the non-route {base}/cockpit/learn once Url
    // became {base}/cockpit). Pure, so it is unit-testable without a UI thread.
    internal static string? SelectCockpitOpenUrl(global::CcDirector.Gateway.Contracts.CockpitInfoDto info)
        => info.Url;

    // The whole fetch -> select -> OPEN decision for the Cockpit button, lifted OFF the async-void
    // BtnCockpit_Click handler so no cockpit-URL logic AND no browser-open call are left inside it to
    // mutate. It fetches the CockpitInfoDto, and when the Gateway hands back a URL it OPENS that URL -
    // info.Url VERBATIM via SelectCockpitOpenUrl - through the injected open action; it opens nothing and
    // returns null when the Gateway hands back no URL (Tailscale down self-hosted) so the caller can say
    // so. Because the open() call itself lives HERE, a desktop test injects a fake open that captures its
    // argument and reddens if the opened URL ever gains a subpath - the exact consumer-boundary regression
    // that appending "/learn" at the browser boundary would be (CLAUDE.md rule 7). Static, fetch-injected
    // and open-injected, so it is unit-testable without a UI thread, a live Gateway, or a real browser.
    internal static async Task<string?> OpenCockpitAsync(
        Func<Task<global::CcDirector.Gateway.Contracts.CockpitInfoDto?>> fetch,
        Action<string?> open)
    {
        var info = await fetch();
        var url = info is { } i ? SelectCockpitOpenUrl(i) : null;
        if (url is { } u)
        {
            FileLog.Write($"[MainWindow] OpenCockpitAsync: opening {u}");
            open(u);
        }
        return url;
    }

    // Builds the "could not reach the gateway" message for the Cockpit button (#475). The "is the
    // Gateway tray app running on THIS machine?" hint only makes sense for the loopback default; for a
    // configured remote gateway the failure is about reachability (the remote gateway is down, or the
    // tailnet is unreachable). Pure string building, so it is unit-testable without a UI thread.
    internal static string BuildGatewayUnreachableMessage(string baseUrl, string error)
    {
        var hint = CockpitUrlResolver.IsLocalhostDefault(baseUrl)
            ? "\n\nIs the Gateway tray app (devthrottle-gateway) running on this machine?"
            : "\n\nIs the Gateway running on that machine and reachable over your tailnet?";
        return $"Could not reach the gateway at {baseUrl}: {error}{hint}";
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
        // Issue #820: prefix the per-session header title with the three-digit number when present.
        HeaderSessionName.Text = _activeSession.HasNumber
            ? $"{_activeSession.NumberBadge}  {_activeSession.DisplayName}"
            : _activeSession.DisplayName;
        HeaderActivityLabel.Text = _activeSession.ActivityLabel;

        // Issue internal#1340: which agent and which MODEL the selected session is running, in the same
        // words as the rail badge because both read the one fold. The badge is always shown for a session -
        // the fold's absent states are sentences ("no model yet" / "model not reported"), not blanks, so
        // there is no case where hiding it is the honest answer.
        HeaderAgentModelText.Text = $"{_activeSession.AgentLabel} | {_activeSession.ModelLabel}";
        ToolTip.SetTip(HeaderAgentModelBadge, _activeSession.AgentModelTooltip);
        HeaderAgentModelBadge.IsVisible = true;

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
            // Locked while the session transcribes a dictated utterance in the background: swallow the
            // keystroke so Ctrl+H cannot open a second Speak dialog mid-transcribe.
            if (IsActiveSessionTranscribing())
            {
                FileLog.Write("[MainWindow] Ctrl+H ignored: session transcribing");
                e.Handled = true;
                return;
            }
            FileLog.Write("[MainWindow] Ctrl+H -> BtnSpeak_Click");
            e.Handled = true;
            BtnSpeak_Click(this, new RoutedEventArgs());
        }
    }

    // True while a SpeakDialog is on screen. The dialog is modal, but BtnSpeak_Click is async void
    // and there is a window between this handler firing and the modal actually appearing; without this
    // guard a second Ctrl+H (or Speak click) in that window opens a SECOND dictation box. Set
    // synchronously before the first await and cleared when the dialog closes, so exactly one box can
    // ever be open.
    private bool _speakDialogOpen;

    private async void BtnSpeak_Click(object? sender, RoutedEventArgs e)
    {
        // Locked out while the active session is still transcribing a previous dictation in the
        // background: no second Speak into a session mid-transcribe (guards the click AND Ctrl+H).
        if (IsActiveSessionTranscribing())
        {
            FileLog.Write("[MainWindow] BtnSpeak_Click ignored: session transcribing");
            return;
        }
        // Never a second dictation box: refuse to open one while one is already open (guards the click
        // AND Ctrl+H, which both route through here).
        if (_speakDialogOpen)
        {
            FileLog.Write("[MainWindow] BtnSpeak_Click ignored: Speak dialog already open");
            return;
        }
        _speakDialogOpen = true;
        // Desktop dictation. Opens SpeakDialog which captures audio via NAudio
        // (BatchDictationRecorder), sends the completed audio to the Gateway transcription owner, then
        // returns the corrected transcript which we insert into PromptInput.
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
            // The box appears IMMEDIATELY: no pre-show network wait. SpeakDialog gates hosted-AI
            // readiness itself from its "GETTING READY" state (OnDialogOpenedAsync runs the same
            // DesktopHostedAiGate check), so a pre-show EnsureReadyAsync here would only duplicate that
            // 2-second credit read and delay the dialog appearing.
            FileLog.Write("[MainWindow] BtnSpeak_Click: opening SpeakDialog");
            // Snapshot the caret BEFORE opening the dialog. Focus moves to the
            // dialog, and on some controls CaretIndex can be reset to 0 after
            // focus loss, which would cause inserted text to land at position 0
            // (effectively prepending instead of inserting at the user's caret).
            var existingTextBefore = PromptInput.Text ?? "";
            var caretBefore = PromptInput.CaretIndex;
            if (caretBefore < 0 || caretBefore > existingTextBefore.Length)
                caretBefore = existingTextBefore.Length;
            // Capture the session the user is dictating INTO now, so a fire-and-forget Send submits
            // to the right session even if the user switches away while it transcribes in background.
            var target = _activeSession?.Session;
            var dlg = new global::CcDirector.Avalonia.Voice.SpeakDialog(options)
            {
                // Immediate (fire-and-forget) Send needs a target session to submit into; enable it
                // whenever we have one so pressing Send releases the screen at once.
                EnableBackgroundSend = target is not null,
            };
            await dlg.ShowDialog(this);

            // Immediate Send: the dialog handed us the still-capturing recorder and closed at once. The
            // screen is already released; transcribe + submit in the background while the session shows
            // orange "Transcribing...". Send behaves exactly like the Insert button followed by Enter:
            // the dictation is dropped at the caret we snapshotted, inside any typed text (via the same
            // DictationText.InsertAt the Insert button uses), then submitted. The dialog was modal, so
            // the box could not change since we snapshotted it; clear it now so the user does not see -
            // or re-send - already-committed text. On ANY failure the words are put back in the compose
            // box (OnDictationFailed) so they are never lost.
            if (dlg.IsBackgroundSend && dlg.BackgroundRecorder is not null && target is not null)
            {
                var recorder = dlg.BackgroundRecorder;
                var composerText = existingTextBefore;
                var caret = caretBefore;
                // Split the typed text at the caret so the dictation lands exactly where the user's
                // caret was, dropping neither the typed part nor the spoken part.
                var before = composerText[..caret];
                var after = composerText[caret..];
                PromptInput.Text = "";
                EnsureDictationInfra(options);
                FileLog.Write($"[MainWindow] BtnSpeak_Click: background dictation send to session {target.Id}, composer chars={composerText.Length}, caret={caret}");
                // Whether a restored composition would still be one untouched transcript (ruling R20): only
                // then does putting it back in the box register it as dictation for a later Send.
                var spokenAlone = SpokenTurnRule.IsSpokenAlone(before, dlg.BackgroundPrefix, after);
                _ = global::CcDirector.Avalonia.Voice.BackgroundDictationSend.RunAsync(
                    recorder, dlg.BackgroundPrefix, target, _dictationTranscriber!,
                    submit: (text, origin, provenance) => global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => SubmitDictatedTextAsync(target, text, origin, provenance)),
                    before: before,
                    after: after,
                    onFailed: (err, composedText) => global::Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => OnDictationFailed(target, composerText, composedText, err, spokenAlone)));
                return;
            }

            // Insert, or a blocking Send when there was no target session: the dialog already
            // transcribed and returned the text. Insert it at the caret we snapshotted (joining with
            // anything already typed) and submit only if the user chose Send.
            var transcript = dlg.ResultText;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                FileLog.Write("[MainWindow] BtnSpeak_Click: dialog returned no text (cancelled or errored)");
                return;
            }
            InsertTranscriptIntoPromptInputAt(transcript!, caretBefore);
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
        finally
        {
            // The dialog has closed (ShowDialog has returned); allow the next one to open.
            _speakDialogOpen = false;
        }
    }

    /// <summary>
    /// Insert transcript text at the given caret index in PromptInput. Adds
    /// whitespace separators when needed so the new words do not smush
    /// against existing characters. Caller is expected to snapshot the caret
    /// BEFORE any focus change (e.g. before opening a modal dialog), because
    /// CaretIndex on a TextBox that has just lost focus can be 0.
    /// </summary>
    private int InsertIntoPromptInputAt(string text, int caret)
    {
        var existing = PromptInput.Text ?? "";
        if (caret < 0 || caret > existing.Length) caret = existing.Length;
        var suffixLen = existing.Length - caret;
        var composed = global::CcDirector.Avalonia.Voice.DictationText.InsertAt(existing, caret, text);
        PromptInput.Text = composed;
        // Caret lands right after the inserted content: the untouched suffix is still at the tail, so
        // the insertion ends at composed.Length - suffixLen.
        PromptInput.CaretIndex = composed.Length - suffixLen;
        PromptInput.Focus();
        // Where the inserted characters themselves begin: at the caret, after the one separating space
        // InsertAt adds when the character before the caret is not whitespace.
        return composed.IndexOf(text, caret, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE COMPOSE BOX'S OWN RECORD OF WHAT CAME FROM A MICROPHONE (ruling R20). PromptInput is a plain
    /// text box; once a transcript was inserted nothing remembered it, so the ordinary Send stamped the
    /// turn typed under a comment calling the composer typed "by construction" - false the moment
    /// dictation was inserted, and the reason 25 of the owner's 655 typed turns in one week carried a
    /// stored transcript verbatim. Every transcript inserted is registered here; the box's text-changed
    /// hook forgets one the text no longer holds; Send asks it what the text IS.
    /// </summary>
    internal readonly SpokenTurnRule.ComposerProvenance _composerProvenance = new();

    /// <summary>Insert a TRANSCRIPT at the caret and record exactly which characters of the box are now
    /// spoken. Internal so the route test can insert a dictation the way the Speak dialog's Insert does.</summary>
    internal void InsertTranscriptIntoPromptInputAt(string transcript, int caret)
    {
        var at = InsertIntoPromptInputAt(transcript, caret);
        _composerProvenance.Inserted(PromptInput.Text ?? "", transcript, at);
    }

    /// <summary>
    /// Submit a background-dictated message straight into a specific session (fire-and-forget Send,
    /// spec section 10). Unlike <see cref="SendPrompt"/> this does NOT read the compose box - it
    /// targets the session the user dictated into (captured when the Speak dialog opened), so it lands
    /// in the right session even if the user has since switched away or typed something else. Keeps
    /// the important parts of the normal send: a history snapshot (a rewind point) and the shared
    /// submit path. Runs on the UI thread.
    /// </summary>
    private async Task SubmitDictatedTextAsync(Session target, string text, InputOrigin origin, SubmissionProvenance provenance)
    {
        // The text arrives already projected for the wire by BackgroundDictationSend, with its spoken spans
        // over that exact text; it is not reshaped here, or the spans would describe a different string.
        if (string.IsNullOrWhiteSpace(text)) return;

        FileLog.Write($"[MainWindow] SubmitDictatedTextAsync: {text.Length} chars to session {target.Id}, origin={origin.Modality}/{origin.Surface}, spans={provenance.SpokenSpans.Count}");

        // Rewind point, exactly like SendPrompt.
        target.InitializeHistory();
        target.History?.TakeSnapshot();

        // DevThrottle Stats: the origin BackgroundDictationSend decided by the one rule (ruling R20) -
        // DesktopVoice for the transcript alone, DesktopTyped for typed text composed around it. This
        // used to stamp every background dictation DesktopVoice, mixture or not.
        await target.SendTextAsync(text, provenance, origin: origin);
    }

    // ===== Fire-and-forget dictation delivery =====
    //
    // A desktop Send/Speak dictation either goes into its session now or fails loudly at once - there is
    // NO durable queue, NO background retry, and NO readiness pre-check. BackgroundDictationSend
    // transcribes off the UI thread and submits straight through the echo-verified terminal submit; that
    // submit itself is the only arbiter of "the session took the text" (the old ActivityState gate
    // rejected healthy idle sessions because that state lags real silence by 10 seconds - see issue
    // #1308). On failure the words are put back in the compose box and reported with a modal, so
    // nothing the user said is ever lost. The recorded WAV itself is saved to disk before the
    // transcription attempt (DictationRecordingStore) and deleted once the words are safe; when
    // transcription fails there is no text to restore, so the file is kept and the failure modal
    // names its path - the audio is the only remaining copy of what was said.

    private global::CcDirector.Core.Transcription.IDictationTranscriber? _dictationTranscriber;
    private bool _dictationInfraReady;

    /// <summary>
    /// Build the dictation transcriber once (idempotent). Needs <see cref="AgentOptions"/> for the
    /// transcriber's method/dictionary resolution, so it is built the first time a dictation is sent.
    /// </summary>
    private void EnsureDictationInfra(AgentOptions options)
    {
        if (_dictationInfraReady) return;
        _dictationTranscriber = new global::CcDirector.Core.Transcription.DictationTranscriber(options);
        _dictationInfraReady = true;
        FileLog.Write("[MainWindow] dictation transcriber ready");
    }

    /// <summary>
    /// A fire-and-forget dictation could not be delivered: transcription failed, or the submit into the
    /// session's composer failed. There is no queue and no retry - put the words back in the compose box
    /// so nothing is lost, and report it with a modal so the failure is impossible to miss (the old
    /// silent hold-notice everyone missed is gone). <paramref name="composedText"/> is the full composed
    /// turn (typed text with the transcript inserted at the caret) when transcription succeeded but the
    /// submit failed; null when the failure happened before a transcript existed, in which case only the
    /// typed <paramref name="composerText"/> can be restored. Runs on the UI thread.
    /// </summary>
    private async void OnDictationFailed(Session target, string composerText, string? composedText, string error, bool spokenAlone)
    {
        try
        {
            var restore = composedText ?? composerText;
            if (!string.IsNullOrEmpty(restore))
            {
                if (_activeSession?.Session == target)
                {
                    // A restored composition that is one untouched transcript is still dictation when it is
                    // sent later from the box (ruling R20); a mixture, or typed text alone, is typed.
                    if (composedText is not null && spokenAlone)
                        InsertTranscriptIntoPromptInputAt(restore, 0);
                    else
                        InsertIntoPromptInputAt(restore, 0);
                }
                else
                {
                    // The restored text goes in front of whatever that session already held; when it is one
                    // untouched transcript its characters are recorded as spoken there too (ruling R20), so
                    // switching to that session and pressing Send counts it as the dictation it was.
                    target.PendingPromptText = global::CcDirector.Avalonia.Voice.DictationText.Join(restore, target.PendingPromptText ?? "");
                    if (composedText is not null && spokenAlone)
                        target.PendingPromptSpokenSpans = new[] { new SpokenTurnRule.SpokenSpan(0, restore.Length) };
                }
            }
            var whatSurvived = composedText is not null
                ? "The transcribed text has been put in the message box - review it and press Send when you are ready."
                : "Any text you had typed has been put back - dictate again when you are ready.";
            await MessageBox.ShowAsync(this, "Dictation not sent",
                $"Your dictation was not sent: {error}\n\nNothing was queued. {whatSurvived}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnDictationFailed FAILED: {ex.Message}");
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

        // On the alternate screen the snapshot reports the recovered alt-screen
        // scrollback (issue #761). Until a full-screen agent has repainted any lines
        // off the top there is nothing to scroll, so hide the bar -- a visible-but-dead
        // scrollbar reads as "scrolling is broken". Once history exists, show it so the
        // user can scroll back through the running agent's transcript.
        if (TerminalHost.IsOnAlternateScreen && snap.ScrollbackCount == 0)
        {
            TerminalScrollBar.IsVisible = false;
            return;
        }
        TerminalScrollBar.IsVisible = true;

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
            // GAP 1 CLOSED - THE ROLE GLYPH IS NO LONGER STAMPED FROM HERE, AND MUST NOT BE AGAIN.
            //
            // This line used to read `vm.ResolvedRole = _sessionManager.ResolveLocalRole(vm.Session)` -
            // the Director resolving a role for itself on every list rebuild. The colour read the
            // Gateway's stamp while the glyph read that local guess, so one row could contradict itself;
            // and the resolver saw only THIS Director's roster, so a controller on another machine was
            // invisible to it. SessionViewModel.ResolvedRole now derives from Session.GatewayResolvedRole
            // directly, which is the same fact the colour folds, so there is nothing left to stamp: the
            // rail tracks the role through OnGatewayResolvedRoleChanged -> RaiseFoldProjection, exactly
            // as it tracks every other Gateway-owned fact. ResolveLocalRole itself is deleted.
            //
            // THE DECISION, RECORDED WHERE IT WAS MADE: before the first stamp arrives, NOTHING shows. No
            // badge until the Gateway says. That was the open question that deferred this fix, and the
            // Architect settled it - the Director resolves nothing, and "no answer yet" is not a lie,
            // whereas a local guess is. Do not reintroduce a default role here to fill the gap.
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

        // The selected configured agent entry (issue #490) is the source of truth for the launch:
        // its type/path/preset/model/args build the command line, replacing the legacy per-type
        // lookup. No enabled entry means there is nothing to launch.
        var selectedEntry = dialog.SelectedAgentEntry;
        if (selectedEntry is null)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: no agent entry selected (none configured); aborting");
            await MessageBox.ShowAsync(this,
                "No agent configured",
                "There are no enabled agents to launch.\n\nAdd one in Settings > Agents, then try again.");
            return;
        }

        // Per-entry preset/model/args resolve to the same effective command line the Tools/Agents
        // page previews (issue #436's shared resolver). The dialog's run-without-approval checkbox
        // then applies on top, because it is a per-session choice, not a stored preset.
        //
        // One catalog-driven block, not a branch per agent: this was three near-identical arms
        // covering Claude, Cursor and Copilot, and EVERY other agent silently fell through to the
        // else and launched asking for approval on each tool call. The rule is the same for all of
        // them, so it is written once and reads the flag from the catalog.
        var entryArgs = selectedEntry.ToToolConfig().ResolveEffectiveCommandLineArguments().Trim();

        var launchArgs = entryArgs;
        if (agentKind == AgentKind.ClaudeCode && dialog.EnableRemoteControl)
            launchArgs = $"remote-control {launchArgs}".Trim();

        var unattendedArg = AgentToolCatalog.UnattendedPermissionArg(agentKind);
        if (dialog.BypassPermissions && unattendedArg is not null)
        {
            // Never add a second permission flag to a line that already settles the question - an
            // entry deliberately set to "Bypass permissions" keeps exactly that, and the default
            // "Automatic" preset is not doubled.
            var alreadyDecided = AgentToolCatalog.KnownPermissionArgs(agentKind)
                .Any(arg => launchArgs.Contains(arg, StringComparison.OrdinalIgnoreCase));
            if (!alreadyDecided)
                launchArgs = $"{launchArgs} {unattendedArg}".Trim();
        }

        var agentArgs = launchArgs.Length > 0 ? launchArgs : null;

        // Build the IAgent from the entry. RawCli uses the entry's executable + its raw args
        // (the dialog seeds the Custom CLI boxes from the entry, so the user-edited boxes win).
        // Catalog agents (Claude/Pi/Codex/Gemini/OpenCode) read their executable path from a
        // per-launch options copy carrying THIS entry's ExecutablePath (issue #490) so two
        // entries of the same type with different paths each launch their own binary.
        IAgent agent;
        if (agentKind == AgentKind.RawCli)
        {
            var customCmd = dialog.SelectedCustomCommand;
            if (string.IsNullOrWhiteSpace(customCmd))
            {
                FileLog.Write("[MainWindow] ShowNewSessionDialog: RawCli selected but no command; aborting");
                return;
            }
            // RawCli carries its own command line on the agent; agentArgs is not reused for it.
            agentArgs = null;
            agent = new RawCliAgent(customCmd, string.IsNullOrWhiteSpace(dialog.SelectedCustomArgs) ? null : dialog.SelectedCustomArgs);
        }
        else
        {
            agent = CreateAgentForEntry(agentKind, selectedEntry.ExecutablePath);
        }

        FileLog.Write($"[MainWindow] ShowNewSessionDialog: path={dialog.SelectedPath}, agent={agentKind}, exe={agent.ExecutablePath}, resume={resumeSessionId ?? "null"}, bypassPermissions={dialog.BypassPermissions}, remoteControl={dialog.EnableRemoteControl}");

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
                errorBody = $"Director could not find '{agentExe}' on PATH.\n\n"
                    + "Make sure the command is installed and on your PATH, or supply an absolute path.";
            }
            else
            {
                var (agentName, installHint) = AgentInstallInfo(agentKind);
                errorTitle = $"{agentName} is not installed";
                errorBody = $"Director could not start a {agentName} session because its command line tool "
                    + $"could not be found.\n\nLooked for: {agentExe}\n\n{installHint}\n\n"
                    + "If it is installed in a non-standard location, set its path in config.json.";
            }
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: agent {agentKind} executable '{agentExe}' not found on PATH; aborting launch");
            await MessageBox.ShowAsync(this, errorTitle, errorBody);
            return;
        }

        var vm = CreateSession(dialog.SelectedPath, resumeSessionId, agentArgs, agent);
        if (vm == null)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: CreateSession returned null; showing failure dialog");
            await MessageBox.ShowAsync(this,
                "Could not start session",
                "Director could not start the session.\n\n"
                + (_lastSessionCreateError ?? "See the Director log for details."));
            return;
        }

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
        else if (dialog.SelectedNamedSession is { } named)
        {
            // Named session (issue #508): the name (and optional colour) were chosen when the item
            // was saved, so apply them directly and skip the rename prompt - launching is one click.
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: named session launch - name={named.Name}, color={named.Color ?? "(none)"}");
            vm.Session.CustomName = named.Name;
            if (!string.IsNullOrWhiteSpace(named.Color))
                vm.Session.CustomColor = named.Color;
            vm.NotifyDisplayChanged();
            SaveSessionToHistory(vm);
            _ = CaptureStartupTextAsync(vm.Session);
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

        // Ruthless default menu (2026-06-15): only items known to work are visible by
        // default. Anything experimental or not yet verified is gated behind alpha mode
        // (AlphaMode.IsEnabled) rather than deleted, so it stays recoverable and can be
        // un-gated once proven. The menu is rebuilt on AlphaMode.Changed.
        var alpha = AlphaMode.IsEnabled;
        var menu = new NativeMenu();

        // ===== File =====
        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu.Items.Add(Item("New Session", () => BtnNewSession_Click(this, new RoutedEventArgs()),
            new KeyGesture(Key.N, KeyModifiers.Control)));
        // Settings lives here so it is reachable from the Home (empty-state) page, where
        // the session toolbar (which also has a Settings button) is hidden.
        file.Menu.Items.Add(Item("Settings...", () => BtnSettings_Click(this, new RoutedEventArgs()),
            new KeyGesture(Key.OemComma, KeyModifiers.Control)));
        file.Menu.Items.Add(new NativeMenuItemSeparator());

        // ----- Named director instances -----
        file.Menu.Items.Add(Item("Create named director instance...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Create named director instance");
            var dlg = new CreateInstanceDialog();
            var ok = await dlg.ShowDialog<bool?>(this);
            if (ok == true && dlg.CreatedInstance is not null)
            {
                if (dlg.LaunchAfter)
                    InstanceProcess.Launch(dlg.CreatedInstance.Name);
                else
                    ShowNotification($"Created director \"{dlg.CreatedInstance.DisplayName}\". " +
                                     "Launch it from File → Switch director.");
            }
        }));
        file.Menu.Items.Add(Item("Rename this director...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Rename this director");
            var slug = InstanceContext.Slug;
            var current = NamedInstanceRegistry.Get(slug)?.DisplayName
                          ?? InstanceContext.DisplayName ?? slug;
            var dlg = new RenameDirectorDialog(slug, current);
            var ok = await dlg.ShowDialog<bool?>(this);
            if (ok == true && dlg.NewDisplayName is not null)
                ShowNotification($"Renamed to \"{dlg.NewDisplayName}\". Restart this director to update the title bar.");
        }));
        file.Menu.Items.Add(Item("Switch director...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Switch director");
            var dlg = new SelectDirectorDialog("Switch to which director?");
            var ok = await dlg.ShowDialog<bool?>(this);
            if (ok != true) return;
            if (dlg.WantsNew)
            {
                var create = new CreateInstanceDialog();
                var created = await create.ShowDialog<bool?>(this);
                if (created == true && create.CreatedInstance is not null && create.LaunchAfter)
                    InstanceProcess.Launch(create.CreatedInstance.Name);
                return;
            }
            if (dlg.LaunchSlug is not null)
                InstanceProcess.Launch(dlg.LaunchSlug);
        }));
        file.Menu.Items.Add(new NativeMenuItemSeparator());

        file.Menu.Items.Add(Item("Save Workspace...", async () =>
        {
            var sessionData = _sessions.Select(vm => new SessionData(
                vm.DisplayName, vm.Session.RepoPath, vm.Session.CustomName,
                vm.Session.CustomColor, vm.Session.ClaudeArgs,
                // Issue #1635: record WHICH agent this session runs. Without it the agent is lost here, at
                // save time, and the session can only come back as the CreateSession default.
                vm.Session.AgentKind.ToString()));
            var catalog = WorkspaceCatalog();
            var importProblem = await ImportLegacyWorkspacesAsync(catalog);
            var dialog = new SaveWorkspaceDialog(catalog, sessionData, importProblem);
            await dialog.ShowDialog<bool?>(this);
        }));
        file.Menu.Items.Add(Item("Load Workspace...", async () =>
        {
            var catalog = WorkspaceCatalog();
            var importProblem = await ImportLegacyWorkspacesAsync(catalog);
            var dialog = new LoadWorkspaceDialog(catalog, importProblem);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.SelectedWorkspace != null)
            {
                // CHECKED BEFORE ANYTHING IS CLOSED. Loading a workspace destroys the running fleet, so
                // every reason it could fail has to be known while the user still has what they had.
                var problems = WorkspaceCannotStartBecause(dialog.SelectedWorkspace);
                if (problems.Count > 0)
                {
                    FileLog.Write(
                        $"[MainWindow] Load Workspace REFUSED for '{dialog.SelectedWorkspace.Name}': " +
                        string.Join("; ", problems));
                    ShowNotification(
                        $"Not starting \"{dialog.SelectedWorkspace.Name}\" - your sessions are untouched. " +
                        string.Join("  ", problems));
                    return;
                }

                if (_sessions.Count > 0) await CloseAllSessionsAsync();
                await LoadWorkspaceAsync(dialog.SelectedWorkspace);
            }
        }));
        file.Menu.Items.Add(Item("Clear Workspace", async () =>
        {
            if (_sessions.Count == 0) return;
            await CloseAllSessionsAsync();
        }));
        // THE DRAIN (issue #2723). Deliberately beside the workspace items rather than under a restart
        // menu: a drain PRODUCES a workspace, and the restart itself is not done from here - the drain
        // hands over the command for it and stops.
        file.Menu.Items.Add(Item("Drain this Director for restart...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Drain this Director");
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            var dialog = new DrainDirectorDialog(
                host, InstanceContext.DisplayName ?? InstanceContext.Slug ?? Environment.MachineName);
            await dialog.ShowDialog(this);
        }));
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Open Logs", () =>
        {
            var logDir = Path.GetDirectoryName(FileLog.CurrentLogPath);
            if (logDir != null && Directory.Exists(logDir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = logDir, UseShellExecute = true });
        }));
        if (alpha)
        {
            // Debug/utility file openers - useful for development, noise for users.
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
        }
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Exit", Close));
        menu.Items.Add(file);

        // ===== Developer (alpha only) =====
        // The "Session" menu was retired: Repositories moved to the pinned sidebar entry (#507),
        // which left only alpha developer tools. Those now live under a Developer menu, so the
        // normal menu bar is just File / View / Help.
        if (alpha)
        {
            var dev = new NativeMenuItem("Developer") { Menu = new NativeMenu() };
            dev.Menu.Items.Add(Item("Accounts...", async () =>
            {
                FileLog.Write("[MainWindow] Menu: Accounts");
                var dialog = new AccountsDialog(AppRef().ClaudeAccountStore);
                await dialog.ShowDialog<bool?>(this);
            }));
            dev.Menu.Items.Add(Item("Show Reviews", async () =>
            {
                FileLog.Write("[MainWindow] Menu: Show Reviews");
                var dialog = new TurnReviewDialog();
                await dialog.ShowDialog(this);
            }));
            menu.Items.Add(dev);
        }

        // ===== View =====
        var view = new NativeMenuItem("View") { Menu = new NativeMenu() };
        view.Menu.Items.Add(Item("Status", ShowStatusView));
        view.Menu.Items.Add(new NativeMenuItemSeparator());
        view.Menu.Items.Add(Item("Toggle Right Panel", () => RightPanelToggle_Click(this, new RoutedEventArgs())));
        view.Menu.Items.Add(Item("Reset Terminal View", () => TabBarRefreshButton_Click(this, new RoutedEventArgs())));
        menu.Items.Add(view);

        // ===== Browsers =====
        // Top-level on purpose (Browsers feature, slice 2): the drivable-browser capability is a
        // headline feature and the menu entry is how it advertises itself. Both items land on
        // Settings > Browsers - the rail group is the everyday launch surface.
        var browsers = new NativeMenuItem("Browser profiles") { Menu = new NativeMenu() };
        browsers.Menu.Items.Add(Item("New Browser Profile...", () => _ = OpenSettingsAsync(onBrowsersTab: true, openBrowserCreate: true)));
        browsers.Menu.Items.Add(Item("Manage Browser Profiles...", () => _ = OpenSettingsAsync(onBrowsersTab: true)));
        menu.Items.Add(browsers);

        // ===== Tools (alpha only - none of these are verified working yet) =====
        if (alpha)
        {
            var tools = new NativeMenuItem("Tools") { Menu = new NativeMenu() };
            // Communications and Connections (Browser Connections) are v1-excluded overlays
            // (issue 570, part of the #357 MVP cutdown). They are gated behind the alpha flag
            // explicitly here so they stay hidden in a default install even if the broader Tools
            // menu is later un-gated for v1. They open the CommsOverlay / ConnectionsOverlay
            // respectively.
            if (alpha)
            {
                tools.Menu.Items.Add(Item("Communications", () => BtnComms_Click(this, new RoutedEventArgs())));
                tools.Menu.Items.Add(Item("Connections", () => BtnConnections_Click(this, new RoutedEventArgs())));
                tools.Menu.Items.Add(new NativeMenuItemSeparator());
            }
            tools.Menu.Items.Add(Item("Claude View...", () => BtnClaudeView_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(Item("MCP Servers...", () => BtnMcpServers_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(Item("Agent Templates...", () => BtnAgentTemplates_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(Item("Claude Code Settings...", () => BtnClaudeConfig_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(new NativeMenuItemSeparator());
            tools.Menu.Items.Add(Item("Transcription Component Preview...", () => BtnTranscriptionPreview_Click(this, new RoutedEventArgs())));
            menu.Items.Add(tools);
        }

        // ===== Help =====
        var help = new NativeMenuItem("Help") { Menu = new NativeMenu() };
        help.Menu.Items.Add(Item("Documentation", () =>
        {
            FileLog.Write("[MainWindow] Menu: Documentation -> https://devthrottle.com/docs");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://devthrottle.com/docs")
                { UseShellExecute = true });
        }));
        help.Menu.Items.Add(Item("Send Feedback...", () => BtnFeedback_Click(this, new RoutedEventArgs())));
        help.Menu.Items.Add(new NativeMenuItemSeparator());
        help.Menu.Items.Add(Item("About Director", () => BtnHelp_Click(this, new RoutedEventArgs())));
        menu.Items.Add(help);

        NativeMenu.SetMenu(this, menu);
    }

    // ==================== TOP APP BAR ====================

    private async void BtnFeedback_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnFeedback_Click: opening feedback dialog");
        var dialog = new FeedbackDialog(this);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true)
            ShowNotification("Thank you. Your feedback has been submitted.");
    }

    private async void BtnTranscriptionPreview_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnTranscriptionPreview_Click: opening transcription component preview");
        try
        {
            var dialog = new global::CcDirector.Avalonia.Controls.TranscriptionComponentPreviewDialog();
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnTranscriptionPreview_Click FAILED: {ex}");
        }
    }

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
        await OpenSettingsAsync(onGatewayTab: false);
    }

    /// <summary>
    /// Open the CC Director Settings dialog. When <paramref name="onGatewayTab"/> is true the
    /// dialog is shown on the Gateway tab (issue #442: the no-Gateway needs-attention indicator
    /// routes here so the user lands on the field they must set). <paramref name="onBrowsersTab"/>
    /// lands on the Browsers tab (the rail group and the Browsers menu route here), and
    /// <paramref name="openBrowserCreate"/> also opens its inline new-browser panel.
    /// </summary>
    private async Task OpenSettingsAsync(
        bool onGatewayTab = false, bool onToolsTab = false,
        bool onBrowsersTab = false, bool openBrowserCreate = false)
    {
        FileLog.Write($"[MainWindow] OpenSettingsAsync: onGatewayTab={onGatewayTab}, onToolsTab={onToolsTab}, onBrowsersTab={onBrowsersTab}");
        var dialog = new SettingsDialog(ReloadScreenshotsPanelAsync);
        // Live-sync the pinned rail group with every change made on the Browsers tab, so the rail
        // behind the open dialog never shows a browser that was just renamed or removed.
        dialog.BrowsersView.Changed += (_, _) => _ = BrowsersRail.RefreshAsync();

        // Hand the Tools tab the verdict this window already reached, rather than letting it probe
        // again: two surfaces re-deriving the same answer is how they come to disagree about one
        // machine. The callback re-drives the rail badge the moment a repair lands, so the badge does
        // not sit red behind a dialog reporting the fault fixed.
        dialog.EmbeddedToolsView.ShowFleetToolStatus(
            _lastFleetToolCheck,
            async () =>
            {
                await RefreshFleetToolReachabilityAsync();
                await DriveToolsSyncAsync();
            },
            // The verdict above is whatever the last tools health pass reached, and that pass is
            // computed once per run - so it can be minutes old and describe a machine an intervening
            // repair has already healed. The page re-asks on load and repaints, which is the
            // difference between "still broken" and "fixed a while ago" for the person reading it.
            async () =>
            {
                await RefreshFleetToolReachabilityAsync();
                return _lastFleetToolCheck;
            });

        if (onGatewayTab)
            dialog.SelectGatewayTab();
        else if (onToolsTab)
            dialog.SelectToolsTab();
        else if (onBrowsersTab)
            dialog.SelectBrowsersTab(openBrowserCreate);
        await dialog.ShowDialog<bool?>(this);

        // The Tools tab can download/repair tools while the dialog is open, so re-run the health
        // check on close - this clears the rail indicator once the toolset is whole again.
        _lastToolHealth = null;
        _ = RefreshToolHealthAsync(force: true);

        // The Browsers tab can create/rename/remove/sign-in browsers; repaint the rail group so the
        // pinned rows match the moment the dialog closes.
        _ = BrowsersRail.RefreshAsync();
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

    // Per-session subscriptions so the needs-you count updates the instant any session's triage
    // verdict moves, not just on the 15s timer. Keyed by VM so we can unsubscribe on remove.
    //
    // This listens to the VIEW-MODEL's NeedsYou property, NOT to the raw Session.OnStatusColorChanged
    // event. The count is folded from hold + dictation + activity + overlays, and only ONE of those
    // raises OnStatusColorChanged - so hooking that event alone left the header stale until the 15s
    // git timer happened to fire. Snoozing a red session visibly left "1 need you" above a grey
    // "Snoozed" row for up to fifteen seconds. SessionViewModel raises NeedsYou from every handler
    // that can move the verdict, so subscribing to the property is what makes the count prompt.
    private readonly Dictionary<SessionViewModel, global::System.ComponentModel.PropertyChangedEventHandler> _needsYouHandlers = new();

    private void OnSessionsCollectionChanged(object? sender, global::System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == global::System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (var kv in _needsYouHandlers) kv.Key.PropertyChanged -= kv.Value;
            _needsYouHandlers.Clear();
            foreach (var vm in _sessions) SubscribeNeedsYou(vm);
        }
        else
        {
            if (e.OldItems != null)
                foreach (SessionViewModel vm in e.OldItems)
                    if (_needsYouHandlers.TryGetValue(vm, out var h)) { vm.PropertyChanged -= h; _needsYouHandlers.Remove(vm); }
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
        global::System.ComponentModel.PropertyChangedEventHandler h = (_, args) =>
        {
            if (args.PropertyName is not (null or nameof(SessionViewModel.NeedsYou))) return;
            Dispatcher.UIThread.Post(UpdateNeedsYouCount);
        };
        _needsYouHandlers[vm] = h;
        vm.PropertyChanged += h;
    }

    // Count of sessions that need you, shown beside the SESSIONS header, so you get a top-level
    // "is anything waiting on me?" signal without scanning the list. Hidden at zero.
    //
    // Counts the SHARED FOLD's triage verdict (SessionViewModel.NeedsYou -> SessionOrdering.Classify),
    // which is the same rule the phone's web-push badge counts by (WebPushNeedsYouNotifier) - so the
    // header and the phone cannot disagree about how many sessions want you.
    //
    // This used to count the RAW cooked colour, `s.Session.StatusColor == "red"`, with no hold check,
    // no role, and no overlays. A snoozed session is still genuinely at a turn end, so its raw colour
    // stays "red" - which is why a session that rendered a grey dot labelled "Snoozed" was counted
    // under a header reading "1 need you". Do not reach past the fold to the raw colour again.
    private void UpdateNeedsYouCount()
    {
        var n = _sessions.Count(s => s.NeedsYou);
        SessionsNeedYouText.Text = n > 0 ? $"{n} need you" : "";
        SessionsNeedYouText.IsVisible = n > 0;
    }

    private bool _commsInitialized;

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

    private void BtnRepositories_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRepositories_Click: opening Repositories view");

        // Close other center overlays first.
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

        if (global::Avalonia.Application.Current is App app)
        {
            RepositoriesView.Attach(app.RepositoryMonitor, app.RootDirectoryStore, app.StartRepositoryRescan);
            RepositoriesOverlay.IsVisible = true;
            SetRepositoriesActive(true);
        }
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it
    }

    private void BtnRepositoriesClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRepositoriesClose_Click: closing Repositories view");
        RepositoriesOverlay.IsVisible = false;
        SetRepositoriesActive(false);
        UpdateHomeVisibility();
    }

    /// <summary>Highlight the pinned Repositories entry while its view is open.</summary>
    private void SetRepositoriesActive(bool active)
    {
        BtnRepositories.Background = new global::Avalonia.Media.SolidColorBrush(
            global::Avalonia.Media.Color.Parse(active ? "#094771" : "#2A2A2A"));
    }

    /// <summary>Show the safe-to-reap worktree count on the pinned Repositories entry.</summary>
    private void UpdateRepositoriesBadge()
    {
        var monitor = (global::Avalonia.Application.Current as App)?.RepositoryMonitor;
        if (monitor is null)
            return;
        int reap = monitor.Snapshot().Sum(s => s.WorktreesSafeToReap);
        RepositoriesBadgeText.Text = reap.ToString();
        RepositoriesBadge.IsVisible = reap > 0;
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
        DocumentPanel.IsVisible = isDocTab;

        // The shared prompt bar belongs to the terminal-style tabs.
        if (_activeSession != null)
            PromptBarBorder.IsVisible = true;

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

    /// <summary>Mirror the worktree safe-to-reap count onto the Source Control tab as a red badge.</summary>
    private void OnOrphanedWorktreeCountChanged(int count)
    {
        SourceControlOrphanBadgeText.Text = count.ToString();
        SourceControlOrphanBadge.IsVisible = count > 0;
    }

    /// <summary>
    /// The live sessions on THIS machine and their working directories, used so the Worktrees page
    /// can flag a worktree a session is running in. Prefers the Gateway's fleet list (which spans
    /// every Director slot on the machine, closing the cross-slot gap); falls back to this Director's
    /// own sessions when no Gateway is connected. Best-effort - never throws.
    /// </summary>
    private async Task<IReadOnlyList<Core.Git.LiveSessionRef>> GetLiveSessionsOnThisMachineAsync(CancellationToken ct)
    {
        var machine = Environment.MachineName;
        try
        {
            var app = global::Avalonia.Application.Current as App;
            var fleetTask = app?.ControlApiHost?.ListFleetSessionsAsync(ct);
            if (fleetTask != null)
            {
                var fleet = await fleetTask;
                var refs = fleet
                    .Where(s => IsSessionAlive(s)
                                && string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(s.RepoPath))
                    .Select(s => new Core.Git.LiveSessionRef { RepoPath = s.RepoPath, Label = FleetSessionLabel(s) })
                    .ToList();
                return refs;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] GetLiveSessionsOnThisMachineAsync fleet query failed, using local sessions: {ex.Message}");
        }

        // No Gateway (or the fleet call failed): fall back to this Director's own sessions.
        return _sessions
            .Where(vm => !string.IsNullOrWhiteSpace(vm.Session.RepoPath))
            .Select(vm => new Core.Git.LiveSessionRef
            {
                RepoPath = vm.Session.RepoPath,
                Label = vm.Session.Number is int n ? $"{vm.DisplayName} (#{n})" : vm.DisplayName,
            })
            .ToList();
    }

    /// <summary>
    /// The AUTHORITATIVE machine-wide live-session roster for the destructive worktree reaper
    /// (issue 516). Unlike <see cref="GetLiveSessionsOnThisMachineAsync"/> - which is best-effort for
    /// display and silently downgrades a fleet failure to this Director's own sessions - this one
    /// FAILS CLOSED: it requires the fleet source and lets a fleet-query failure propagate, so the
    /// reaper aborts rather than act on a partial roster that omits sessions in other Director slots
    /// on this machine. This Director's own sessions are always included as a floor, because they are
    /// known-alive and in use even if fleet registration lags.
    /// </summary>
    private async Task<IReadOnlyList<Core.Git.LiveSessionRef>> GetAuthoritativeLiveSessionsAsync(CancellationToken ct)
    {
        var machine = Environment.MachineName;

        // This Director's own sessions - always known and always in use. Keyed by normalized path.
        var byPath = new Dictionary<string, Core.Git.LiveSessionRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var vm in _sessions)
        {
            if (string.IsNullOrWhiteSpace(vm.Session.RepoPath))
                continue;
            var key = Core.Git.WorktreeReaperService.NormalizePath(vm.Session.RepoPath);
            byPath[key] = new Core.Git.LiveSessionRef
            {
                RepoPath = vm.Session.RepoPath,
                Label = vm.Session.Number is int n ? $"{vm.DisplayName} (#{n})" : vm.DisplayName,
            };
        }

        var app = global::Avalonia.Application.Current as App;
        var fleetTask = app?.ControlApiHost?.ListFleetSessionsWithReachabilityAsync(ct);
        if (fleetTask is null)
            throw new InvalidOperationException(
                "cannot confirm the machine-wide session roster: no fleet source is available. " +
                "Removing worktrees is refused until the roster can be confirmed.");

        // A fleet-query failure PROPAGATES here (fail closed) - it is never downgraded to local-only.
        var (fleet, reachability) = await fleetTask;

        // FAIL CLOSED on an INCOMPLETE roster (inspection): the Gateway returns 200 while silently
        // dropping (or serving stale) the sessions of a Director whose tunnel is stale. A worktree is
        // a local folder, so only Directors ON THIS MACHINE can have sessions in it; if any of them is
        // not fully Online, this machine's roster may be missing a live session and the reap must not
        // act on it.
        var degraded = DegradedSameMachineDirectors(reachability, machine);
        if (degraded.Count > 0)
            throw new InvalidOperationException(
                $"cannot confirm the machine-wide session roster: {degraded.Count} Director(s) on this machine are not fully reachable " +
                $"({string.Join(", ", degraded)}), so a live session could be missing from the roster. " +
                "Removing worktrees is refused until the roster can be confirmed.");

        foreach (var s in fleet)
        {
            if (!IsSessionAlive(s)
                || !string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(s.RepoPath))
                continue;
            var key = Core.Git.WorktreeReaperService.NormalizePath(s.RepoPath);
            if (!byPath.ContainsKey(key))
                byPath[key] = new Core.Git.LiveSessionRef { RepoPath = s.RepoPath, Label = FleetSessionLabel(s) };
        }

        return byPath.Values.ToList();
    }

    /// <summary>
    /// The Directors ON THIS MACHINE that the Gateway reports as NOT fully Online (Wobbly or
    /// Offline), each as "id (state)". A non-empty result means this machine's session roster may be
    /// missing a live session, so the destructive reaper must fail closed. Pure and testable.
    ///
    /// A STOPPED Director is NOT degraded, and this is a fail-closed guard so the exclusion has to be
    /// argued rather than assumed. The question here is "could this machine be running a session I cannot
    /// see". A Director in the stopped state told the Gateway it was shutting down and then closed its
    /// tunnel: the process is gone, so it owns no live session, and there is nothing about it that could be
    /// missing from the roster. Wobbly and Offline are different - those are Directors that may well be
    /// alive and merely unheard, which is exactly the doubt this guard exists for.
    ///
    /// Without the exclusion, one orderly sibling shutdown disabled every worktree removal on the machine
    /// until that registration aged out a day later - and <see cref="Gateway.Contracts.RosterCompleteness"/>
    /// was meanwhile calling the same roster COMPLETE. Two guards reading one roster must not disagree
    /// about whether it can be trusted.
    /// </summary>
    internal static List<string> DegradedSameMachineDirectors(
        IEnumerable<Gateway.Contracts.DirectorReachabilityDto> reachability, string machine)
        => reachability
            .Where(r => string.Equals(r.MachineName, machine, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(r.State, Gateway.Contracts.DirectorReachabilityDto.StateOnline, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(r.State, Gateway.Contracts.DirectorReachabilityDto.StateStopped, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.DirectorId} ({r.State})")
            .ToList();

    /// <summary>A session is genuinely alive when it has not exited/failed and did not crash.</summary>
    private static bool IsSessionAlive(Gateway.Contracts.SessionDto s) =>
        !string.Equals(s.Status, "Exited", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)
        && !s.Crashed;

    private static string FleetSessionLabel(Gateway.Contracts.SessionDto s)
    {
        var name = string.IsNullOrWhiteSpace(s.Name) ? "session" : s.Name;
        return s.Number is int n ? $"{name} (#{n})" : name;
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
        // The box's provenance follows its text (ruling R20): every change to the box passes through here, and
        // the record moves or forgets its spoken ranges accordingly. The caret says WHERE the change happened
        // when the text alone cannot (the same words twice over, one copy deleted).
        _composerProvenance.TextChanged(text, PromptInput.CaretIndex);

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
        var agentKind = _activeSession?.Session.AgentKind ?? AgentKind.ClaudeCode;
        var available = _slashCommandProvider.GetCommands(agentKind, repoPath);

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
        SlashCommandDocSource.Text = selected.Source switch
        {
            "project" => "Project",
            "global" => "Global",
            "builtin" => selected.DriverKind?.ToString() ?? "Built in",
            _ => selected.Source
        };
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

        if (_activeSession?.Session.AgentKind != AgentKind.ClaudeCode)
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

    /// <summary>
    /// 1 while a prompt send is in flight (issue #1107). The send was SAFE BY ACCIDENT: it clears
    /// PromptInput.Text before its first await, so a second click hit the empty-text early return above.
    /// That works, but nothing said it was load-bearing, and an edit that moved the clear below an await -
    /// a perfectly reasonable-looking change - would have silently reintroduced double-send.
    /// The guard is explicit now so the property does not depend on the order of two unrelated lines.
    /// </summary>
    private int _sendPromptInFlight;

    private async void SendPrompt()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text)) return;

        if (Interlocked.CompareExchange(ref _sendPromptInFlight, 1, 0) != 0)
        {
            FileLog.Write("[MainWindow] SendPrompt ignored: a send is already in flight");
            return;
        }

        try
        {
            await SendPromptCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _sendPromptInFlight, 0);
        }
    }

    internal async Task SendPromptCoreAsync()
    {
        // Re-checked rather than assumed from the caller: this method must stand on its own, which is the
        // whole point of not relying on a distant line to hold a property true.
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text)) return;
        // Locked out while the session transcribes a dictated utterance in the background, so a
        // typed Send cannot race the incoming dictated prompt (guards the button AND Ctrl+Enter).
        if (IsActiveSessionTranscribing())
        {
            FileLog.Write("[MainWindow] SendPrompt ignored: session transcribing");
            return;
        }

        // THE COMPOSITION IS TAKEN HERE, WHOLE: the text and what it is (ruling R20), read together from the
        // box BEFORE the box is cleared. The fix-round inspector found the origin asked for after the clear;
        // that happened to work only because the toolkit posts the box's text-changed hook rather than raising
        // it inline, and the hook would have forgotten the dictation had it run first. Nothing between reading
        // the box and asking its provenance may yield. DesktopVoice when the box holds one inserted transcript
        // and nothing else, DesktopTyped otherwise - never from which button was pressed.
        var boxText = PromptInput.Text ?? "";
        _composerProvenance.TextChanged(boxText, PromptInput.CaretIndex);
        var origin = _composerProvenance.OriginFor(boxText);
        // The text as sent (newlines to spaces - the agent's prompt is one line - and trimmed) AND the spoken
        // ranges moved to where they stand in that text, from one projection (source logging, 2026-09-05).
        var (text, spokenSpans) = _composerProvenance.ForSend();
        if (string.IsNullOrEmpty(text)) return;
        var provenance = new SubmissionProvenance(SubmissionRoutes.DesktopComposer, SubmissionIdentityKinds.LocalUser, null, spokenSpans);

        // Intercept slash commands and show native dialogs
        if (TryHandleSlashCommand(text))
        {
            PromptInput.Text = "";
            return;
        }

        FileLog.Write($"[MainWindow] SendPrompt: {text.Length} chars to session {_activeSession.Session.Id}, origin={origin.Modality}/{origin.Surface}");

        PromptInput.Text = "";
        _composerProvenance.Reset();

        // Clear saved prompt text so switching away and back shows empty box
        _activeSession.Session.PendingPromptText = string.Empty;

        // Snapshot the JSONL before sending so we can rewind to this point
        _activeSession.Session.InitializeHistory();
        _activeSession.Session.History?.TakeSnapshot();

        // Notify user when large input is redirected to a temp file. Name the
        // active session's actual agent, not a hardcoded "Claude Code".
        if (CcDirector.Core.Input.LargeInputHandler.IsLargeInput(text))
        {
            var agentName = AgentPluginRegistry.Get(_activeSession.Session.AgentKind).DisplayName;
            ShowNotification(CcDirector.Core.Input.LargeInputHandler.FormatRedirectNotice(agentName, text.Length));
        }
        else
        {
            ClearNotification();
        }

        // Check if this is an interactive TUI command
        var isInteractiveCommand = _activeSession.Session.AgentKind == AgentKind.ClaudeCode
            && text.StartsWith("/")
            && InteractiveTuiCommands.Contains(text.TrimStart('/'));

        // Backends send Enter (CR/LF) explicitly after the text -- don't append a submit
        // newline here. Appending one used to trip LargeInputHandler's multi-line check
        // and route short single-line prompts through a temp file.
        await _activeSession.Session.SendTextAsync(text, provenance, origin: origin);

        if (isInteractiveCommand)
        {
            EnterInteractiveTuiMode(_activeSession.Session);
        }
        else
        {
            PromptInput.Focus();
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
        // Locked out while the session transcribes a dictated utterance in the background (guards the
        // Queue button AND Ctrl+Shift+Enter).
        if (IsActiveSessionTranscribing())
        {
            FileLog.Write("[MainWindow] QueueCurrentPrompt ignored: session transcribing");
            return;
        }

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

    private async void BtnHandover_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnHandover_Click");
        if (_activeSession == null)
        {
            FileLog.Write("[MainWindow] BtnHandover_Click: no active session");
            return;
        }

        await _activeSession.Session.SendTextAsync("/handover", SubmissionProvenance.FrameworkText(), SendSource.Framework);
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

    // ==================== AUTO-UPDATE STATUS ====================
    //
    // Issue #1030. This panel used to hide itself whenever there was "nothing to report", and it only
    // ever knew what the update service had just told it. The result was that up to date, has not
    // checked yet, downloading, downloaded and waiting for a restart, and a check that failed because
    // a release's downloads had not been attached yet all looked like the same thing: a version number
    // that did not change. The owner concluded auto-update was broken while it was carrying his machine
    // from 1.8.0 to 1.8.6, and nothing in the product could have told him otherwise.
    //
    // So the panel is now ALWAYS VISIBLE and says which of those it is, and none of the code here
    // decides what any of it means. UpdateStatusFold computes the words, the colors and the one action
    // that is actually available; this renders them. That is critical rule 7, and it is not a style
    // preference: a client that works out for itself what a state means will, the first time it meets a
    // combination nobody thought of, draw something plausible instead of something true.

    /// <summary>Icons: a ring while busy, a check when settled, a cross on a problem, a dot when idle.</summary>
    private const string UpdateIconRing = GatewayIconRing;
    private const string UpdateIconCheck = GatewayIconCheck;
    private const string UpdateIconCross = GatewayIconCross;
    private const string UpdateIconDot = "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M8,3 A5,5 0 1 1 8,13 A5,5 0 1 1 8,3 Z";

    /// <summary>The status last painted, so a click knows which action the panel was offering.</summary>
    private CcDirector.Core.Update.UpdateStatusView? _updateStatus;

    /// <summary>
    /// Repaints the panel on a slow tick. Most of what it says is only true relative to NOW - "checked
    /// 4 minutes ago" - and the launcher writes its decisions into the shared record from another
    /// process entirely, so nothing here would otherwise learn that an install was held or rolled back.
    /// </summary>
    private DispatcherTimer? _updateStatusTimer;

    /// <summary>Start painting the update status, and keep it current. Called once from startup.</summary>
    private void StartUpdateStatusDisplay()
    {
        RefreshUpdateStatus();
        _updateStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateStatusTimer.Tick += (_, _) => RefreshUpdateStatus();
        _updateStatusTimer.Start();
    }

    /// <summary>
    /// Ask for the current status and render it, field for field. The only judgement here is layout.
    /// </summary>
    public void RefreshUpdateStatus()
    {
        try
        {
            var status = CcDirector.Core.Update.UpdateStatusBoard.Current();
            if (status is null)
            {
                // The updater has not been constructed yet - a window or two of startup. Say nothing
                // rather than inventing a status; this is the only moment the panel is not shown.
                UpdateIndicator.IsVisible = false;
                return;
            }

            _updateStatus = status;

            UpdateIndicatorIcon.Data = Geometry.Parse(IconFor(status.Icon));
            UpdateIndicatorIcon.Fill = Brush.Parse(status.Accent);
            UpdateIndicator.Background = Brush.Parse(status.Background);
            UpdateIndicator.BorderBrush = Brush.Parse(status.Border);
            UpdateIndicatorLabel.Text = status.Headline;
            UpdateIndicatorLabel.Foreground = Brush.Parse(status.Accent);
            UpdateIndicatorSub.Text = status.Detail;

            var action = status.CanInstallNow ? status.InstallNowLabel
                       : status.CanCheckNow ? status.CheckNowLabel
                       : null;
            UpdateIndicatorAction.Text = action ?? "";
            UpdateIndicatorAction.IsVisible = action is not null;
            UpdateIndicatorAction.Foreground = Brush.Parse(status.Accent);

            ToolTip.SetTip(UpdateIndicator, status.Tooltip);
            UpdateIndicator.Cursor = new Cursor(action is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            UpdateIndicator.IsVisible = true;

            if (status.Busy && status.PercentComplete is { } percent)
                ShowUpdateDownloadProgress(status, percent);
            else
                HideDownloadProgress();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RefreshUpdateStatus FAILED: {ex.Message}");
        }
    }

    private static string IconFor(string icon) => icon switch
    {
        "ring" => UpdateIconRing,
        "check" => UpdateIconCheck,
        "cross" => UpdateIconCross,
        _ => UpdateIconDot,
    };

    /// <summary>
    /// Note that a build has finished downloading. What happens NEXT is the launcher's to decide, so
    /// this says only what is certainly true and points at the panel for the rest. It used to promise
    /// "installs next time you open the app", which stopped being how it works when the launcher took
    /// ownership of the install (issue #1033) - it now happens without anyone opening anything, as soon
    /// as this Director is idle.
    /// </summary>
    public void ShowUpdateReady(string version)
    {
        FileLog.Write($"[MainWindow] ShowUpdateReady: {version}");
        ShowNotification($"Director {version} downloaded. It installs on its own once no sessions are running.");
        RefreshUpdateStatus();
    }

    /// <summary>
    /// A phase event from the update service. The board has already been told; this only makes the
    /// panel repaint immediately rather than at the next tick, so a check a person just asked for
    /// visibly starts.
    /// </summary>
    public void OnUpdateProgress(CcDirector.Core.Update.UpdateProgress p)
    {
        RefreshUpdateStatus();
    }

    private void ShowUpdateDownloadProgress(CcDirector.Core.Update.UpdateStatusView status, int percent)
    {
        NotificationIcon.IsVisible = false;
        NotificationText.Text = status.Detail;
        NotificationProgress.IsVisible = true;
        NotificationProgress.IsIndeterminate = false;
        NotificationProgress.Value = percent;
        NotificationProgressMeta.Text = $"{percent}%";
        NotificationProgressMeta.IsVisible = true;
        NotificationBar.IsVisible = true;
    }

    private void HideDownloadProgress()
    {
        NotificationProgress.IsVisible = false;
        NotificationProgress.IsIndeterminate = false;
        NotificationProgressMeta.IsVisible = false;
    }

    /// <summary>
    /// Take whichever action the fold offered. The panel never chooses between them and never invents
    /// one: if the fold offered nothing, a click does nothing, because there was nothing that could be
    /// done. That is the difference between this and a button that sits there looking available.
    /// </summary>
    private void UpdateIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var status = _updateStatus;
            if (status is null) return;

            if (status.CanInstallNow)
            {
                FileLog.Write("[MainWindow] update panel clicked - asking the launcher to install now");
                ShowNotification("Asking the launcher to install the update and restart the Director...");
                _ = Task.Run(async () =>
                {
                    var result = await CcDirector.Core.Update.LauncherRestartClient.RequestRestartAsync();
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowNotification(result.Message);
                        RefreshUpdateStatus();
                    });
                });
                return;
            }

            if (status.CanCheckNow)
            {
                FileLog.Write("[MainWindow] update panel clicked - on-demand check");
                _ = Task.Run(async () =>
                {
                    await CcDirector.Core.Update.UpdateStatusBoard.CheckNowAsync();
                    Dispatcher.UIThread.Post(RefreshUpdateStatus);
                });
                return;
            }

            FileLog.Write($"[MainWindow] update panel clicked in state {status.State}, which offers no action - ignoring");
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

        // Full panel and the slim strip share the Auto column; swap which one is visible.
        // The collapse chevron lives in the panel header; the expand chevron in the strip.
        RightPanel.IsVisible = _rightPanelExpanded;
        RightPanelCollapsedStrip.IsVisible = !_rightPanelExpanded;
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

        await _screenshotReloadGate.WaitAsync();
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
        finally
        {
            _screenshotReloadGate.Release();
        }
    }

    private static List<ScreenshotViewModel> LoadScreenshotViewModels(string directory)
    {
        return Directory.GetFiles(directory)
            .Where(f => ScreenshotExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(50)
            .Select(f => new ScreenshotViewModel(f))
            .ToList();
    }

    // Deletes every screenshot image file in the folder from disk and returns how many were removed.
    // Clear All must delete the files, not just empty the in-memory list: the folder watcher re-reads
    // the directory on the next change and any surviving file reappears in the panel (issue #1494).
    private static int DeleteAllScreenshots(string directory)
    {
        var files = Directory.GetFiles(directory)
            .Where(f => ScreenshotExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        var deleted = 0;
        foreach (var file in files)
        {
            File.Delete(file);
            deleted++;
        }
        return deleted;
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

    /// <summary>
    /// Refresh re-reads the CONFIGURED folder, not just the files in the folder resolved at startup.
    /// <see cref="RefreshScreenshots"/> re-lists the cached <c>_screenshotsDirectory</c>, which is the
    /// right thing for the file watcher but useless after the folder itself changes - a user who set
    /// the folder in the wizard clicked Refresh and kept getting the old, empty one. This is the
    /// button the user reaches for when the panel looks wrong, so it re-resolves and re-watches.
    /// </summary>
    private void BtnRefreshScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRefreshScreenshots_Click");
        _ = ReloadScreenshotsPanelAsync();
    }

    private async void BtnClearScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearScreenshots_Click");
        try
        {
            if (_screenshotsDirectory == null)
                return;

            // Deleting the files is permanent, so warn before destroying data (issue #1494).
            var confirm = new ConfirmDialog(
                "Clear all screenshots?",
                "This permanently deletes every screenshot in this Director's screenshots folder from disk. This cannot be undone.",
                confirmLabel: "Delete All");
            if (await confirm.ShowDialog<bool>(this) != true)
                return;

            var deleted = await Task.Run(() => DeleteAllScreenshots(_screenshotsDirectory));
            FileLog.Write($"[MainWindow] BtnClearScreenshots_Click: deleted {deleted} file(s)");

            // Re-read from disk so the panel reflects the now-empty folder even if the watcher's
            // debounced refresh has not fired yet.
            await RefreshScreenshots();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnClearScreenshots_Click FAILED: {ex.Message}");
            await new MessageDialog("Cannot Clear Screenshots", ex.Message).ShowDialog<bool?>(this);
        }
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

        // Issue #1107, item 6. The OwnedWindows check below guards owned WINDOWS; it does not guard the
        // async gap around the Task.Run further down, so two clicks opened two browser tabs. Minor in
        // consequence, identical in shape to the Cockpit bug.
        await BusyAction.RunAsync(btn, () => CreateIssueFromScreenshotAsync(filePath), "Opening...",
            owner: this, failureTitle: "Cannot Create GitHub Issue");
    }

    private async Task CreateIssueFromScreenshotAsync(string filePath)
    {
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

        // Detach terminal and source control
        TerminalHost.Detach();
        SourceControlView.Detach();
        _activeSession = null;

        // Stop git status polling
        _sessionGitTimer?.Stop();

        // Stop repainting the update status
        _updateStatusTimer?.Stop();

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

        await session.SendTextAsync(prompt, SubmissionProvenance.FrameworkText(), SendSource.Framework);
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
