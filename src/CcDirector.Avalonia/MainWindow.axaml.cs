using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Skills;
using CcDirector.Core.Utilities;
using FileViewerControls = CcDirector.Avalonia.Controls;

namespace CcDirector.Avalonia;

// ==================== VIEW MODELS ====================

public class HookEventViewModel
{
    private static readonly Dictionary<string, ISolidColorBrush> EventBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stop"] = new SolidColorBrush(Color.Parse("#22C55E")),
        ["Notification"] = new SolidColorBrush(Color.Parse("#F59E0B")),
    };
    private static readonly ISolidColorBrush DefaultBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));

    public string Timestamp { get; }
    public string SessionId { get; }
    public string SessionIdShort { get; }
    public string EventName { get; }
    public string Detail { get; }
    public ISolidColorBrush EventBrush { get; }

    public HookEventViewModel(PipeMessage msg)
    {
        Timestamp = DateTime.Now.ToString("HH:mm:ss");
        EventName = msg.HookEventName ?? "Unknown";
        SessionId = msg.SessionId ?? "";
        SessionIdShort = SessionId.Length > 8 ? SessionId.Substring(0, 8) : SessionId;
        Detail = BuildDetail(msg);
        EventBrush = EventBrushes.GetValueOrDefault(EventName, DefaultBrush);
    }

    private static string BuildDetail(PipeMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.ToolName))
            return msg.ToolName;
        if (!string.IsNullOrEmpty(msg.Message))
            return msg.Message;
        if (!string.IsNullOrEmpty(msg.Prompt))
            return msg.Prompt.Length > 100 ? msg.Prompt.Substring(0, 100) + "..." : msg.Prompt;
        if (!string.IsNullOrEmpty(msg.Reason))
            return msg.Reason;
        return "";
    }
}

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
    private readonly ObservableCollection<HookEventViewModel> _hookEvents = new();
    private readonly List<HookEventViewModel> _allHookEvents = new();
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

        AddHandler(DragDrop.DropEvent, PromptInput_Drop);
        AddHandler(DragDrop.DragOverEvent, PromptInput_DragOver);

        TerminalHost.ScrollChanged += OnTerminalScrollChanged;
        TerminalHost.ViewFileRequested += OnTerminalViewFileRequested;
        TerminalScrollBar.PropertyChanged += TerminalScrollBar_PropertyChanged;

        SessionList.AddHandler(DragDrop.DragOverEvent, SessionList_DragOver);
        SessionList.AddHandler(DragDrop.DropEvent, SessionList_Drop);
        SessionList.AddHandler(PointerPressedEvent, SessionList_PointerPressed, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        HookEventList.ItemsSource = _hookEvents;
        QueueItemsList.ItemsSource = _queueItems;
        ScreenshotList.ItemsSource = _screenshots;

        // Wire up hook event routing
        app.EventRouter.OnRawMessage += OnHookEventReceived;

        // Subscribe to session registration for ClaudeSessionId persistence
        _sessionManager.OnClaudeSessionRegistered += OnClaudeSessionRegistered;

        // Sessions created via the Control API (web Manager) need to be wrapped
        // into the Avalonia sidebar so the desktop user can interact with them too.
        _sessionManager.OnSessionCreated += OnExternalSessionCreated;

        // Wire source control view file event
        GitChangesView.ViewFileRequested += OnGitViewFileRequested;

        // Wire session browser resume event
        SessionBrowserView.SessionResumeRequested += OnSessionBrowserResumeRequested;

        // Wire clean view rewind event
        CleanView.RewindRequested += OnCleanViewRewindRequested;

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
        _ = ShowStartupWorkspacePicker();

        // Start session git status polling (15s interval)
        _sessionGitTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _sessionGitTimer.Tick += async (_, _) => await RefreshSessionGitStatusAsync();
        _sessionGitTimer.Start();
    }

    private void SetBuildInfo()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath != null && File.Exists(exePath))
            {
                var buildTime = File.GetLastWriteTime(exePath);
                BuildInfoText.Text = $"Build: {buildTime:HH:mm:ss}";
                ToolTip.SetTip(BuildInfoText, $"Built: {buildTime:yyyy-MM-dd HH:mm:ss}\nPath: {exePath}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SetBuildInfo FAILED: {ex.Message}");
            BuildInfoText.Text = "Build: unknown";
        }
    }

    // ==================== WORKSPACE STARTUP ====================

    private async Task ShowStartupWorkspacePicker()
    {
        var app = (App)global::Avalonia.Application.Current!;

        if (app.SkipWorkspacePicker)
        {
            FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: suppressed by --skip-workspace-picker");
            return;
        }

        if (!app.WorkspaceStore.LoadAll().Any())
        {
            FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: no saved workspaces");
            return;
        }

        FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: showing workspace picker");

        var dialog = new LoadWorkspaceDialog(app.WorkspaceStore, startupMode: true);
        dialog.SetOwner(this);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result != true || dialog.SelectedWorkspace == null)
        {
            FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: user skipped");
            return;
        }

        await LoadWorkspaceAsync(dialog.SelectedWorkspace);
    }

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
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionCreated FAILED: {ex.Message}");
            }
        });
    }

    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId = null, string? claudeArgs = null, AgentKind agentKind = AgentKind.ClaudeCode)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agentKind}, resume={resumeSessionId ?? "null"}, args={claudeArgs ?? "default"}");
        try
        {
            IAgent agent = agentKind switch
            {
                AgentKind.Pi => new PiAgent(_sessionManager.Options),
                AgentKind.Codex => new CodexAgent(_sessionManager.Options),
                AgentKind.Gemini => new GeminiAgent(_sessionManager.Options),
                _ => new ClaudeAgent(_sessionManager.Options)
            };
            var session = _sessionManager.CreateSession(repoPath, agent, claudeArgs, SessionBackendType.ConPty, resumeSessionId);
            FileLog.Write($"[MainWindow] CreateSession: session created, id={session.Id}, pid={session.ProcessId}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            FileLog.Write($"[MainWindow] CreateSession: added to UI");
            return vm;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] CreateSession FAILED: {ex.Message}");
            return null;
        }
    }

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
            TerminalHost.Detach();
            GitChangesView.Detach();
            CleanView.Detach();
        }

        _activeSession = vm;

        if (vm == null)
        {
            SessionHeaderBanner.IsVisible = false;
            PlaceholderText.IsVisible = true;
            TerminalGrid.IsVisible = false;
            PromptBarBorder.IsVisible = false;
            TabBarRefreshButton.IsVisible = false;
            TabBarCaptureButton.IsVisible = false;
            GitChangesView.Detach();
            CleanView.Detach();
            return;
        }

        // Subscribe to metadata and activity changes for header updates
        vm.Session.OnClaudeMetadataChanged += OnActiveSessionMetadataChanged;
        vm.Session.OnActivityStateChanged += OnActiveSessionActivityChanged;

        // Update header
        SessionHeaderBanner.IsVisible = true;
        UpdateSessionHeader();

        // Attach terminal
        PlaceholderText.IsVisible = false;
        TerminalGrid.IsVisible = true;
        TerminalHost.Attach(vm.Session);
        UpdateScrollBar();

        // Attach source control (hide tab if no .git)
        GitChangesView.Attach(vm.Session.RepoPath);
        UpdateSourceControlTabVisibility(vm.Session.RepoPath);

        // Attach clean view (Agent tab)
        CleanView.Attach(vm.Session);

        // Show prompt bar and refresh button
        PromptBarBorder.IsVisible = true;
        TabBarRefreshButton.IsVisible = _activeLeftTab == "Terminal";
        TabBarCaptureButton.IsVisible = _activeLeftTab == "Terminal";

        // Restore prompt text for incoming session
        PromptInput.Text = vm.Session.PendingPromptText ?? "";
        PromptInput.CaretIndex = PromptInput.Text.Length;

        // Restore last selected tab
        var tabName = vm.Session.SelectedTabName;
        if (!string.IsNullOrEmpty(tabName) && tabName != _activeLeftTab)
            SwitchLeftTab(tabName);

        // Switch document tabs to new session
        SwitchDocumentTabsToSession(vm.Session.Id);

        // Refresh right panel for new session
        RefreshHookEventsPanel();
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
    }

    private async Task CloseAllSessionsAsync()
    {
        FileLog.Write("[MainWindow] CloseAllSessionsAsync");
        if (_activeSession != null)
        {
            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
        }
        TerminalHost.Detach();
        GitChangesView.Detach();
        CleanView.Detach();
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

        SessionHeaderBanner.IsVisible = false;
        PlaceholderText.IsVisible = true;
        TerminalGrid.IsVisible = false;
        PromptBarBorder.IsVisible = false;

        FileLog.Write($"[MainWindow] CloseAllSessionsAsync: removed {snapshots.Count} session(s)");
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

        var separator1 = new Separator();

        var openJsonl = new MenuItem { Header = "Open .jsonl in Explorer" };
        openJsonl.Click += (_, _) => OpenSessionJsonl(vm);

        var separator2 = new Separator();

        var openExplorer = new MenuItem { Header = "Open in Explorer" };
        openExplorer.Click += (_, _) => OpenInExplorer(vm);

        var openVsCode = new MenuItem { Header = "Open in VS Code" };
        openVsCode.Click += (_, _) => OpenInVsCode(vm);

        var relink = new MenuItem { Header = "Relink Session..." };
        relink.Click += (_, _) => _ = ShowRelinkDialog(vm);

        var separator3 = new Separator();

        var close = new MenuItem { Header = "Close Session" };
        close.Click += (_, _) => _ = CloseSessionAsync(vm);

        menu.Items.Add(rename);
        menu.Items.Add(relink);
        menu.Items.Add(separator1);
        menu.Items.Add(openJsonl);
        menu.Items.Add(separator2);
        menu.Items.Add(openExplorer);
        menu.Items.Add(openVsCode);
        menu.Items.Add(separator3);
        menu.Items.Add(close);

        menu.Open(button);
    }

    private async void ShowRenameDialog(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] ShowRenameDialog: session={vm.Session.Id}, name={vm.DisplayName}");
        var dialog = new RenameSessionDialog(vm.DisplayName, vm.Session.CustomColor);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true)
        {
            vm.Rename(dialog.SessionName, dialog.SelectedColor);
            PersistSessionState();
            UpdateSessionHistory(vm);

            if (_activeSession == vm)
                UpdateSessionHeader();

            FileLog.Write($"[MainWindow] ShowRenameDialog: confirmed, name={dialog.SessionName}, color={dialog.SelectedColor ?? "null"}");
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

    private async Task CloseSessionAsync(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] CloseSessionAsync: session={vm.Session.Id}");

        if (_activeSession == vm)
        {
            vm.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            vm.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            TerminalHost.Detach();
            GitChangesView.Detach();
            CleanView.Detach();
            _activeSession = null;

            SessionHeaderBanner.IsVisible = false;
            PlaceholderText.IsVisible = true;
            TerminalGrid.IsVisible = false;
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
                SortOrder = i,
            });
            app.SessionStateStore.Save(persisted);
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

    private void UpdateSessionHeader()
    {
        if (_activeSession == null) return;

        var session = _activeSession.Session;
        HeaderSessionName.Text = _activeSession.DisplayName;
        HeaderActivityLabel.Text = _activeSession.ActivityLabel;

        // Apply custom header color if set
        if (!string.IsNullOrWhiteSpace(session.CustomColor))
        {
            var color = Color.Parse(session.CustomColor);
            SessionHeaderBanner.Background = new SolidColorBrush(color);
        }
        else
        {
            SessionHeaderBanner.Background = new SolidColorBrush(Color.Parse("#007ACC"));
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

        // Session IDs
        var claudeId = session.ClaudeSessionId;
        if (!string.IsNullOrEmpty(claudeId))
        {
            HeaderSessionId.Text = claudeId.Length > 12 ? claudeId[..12] + "..." : claudeId;
            HeaderDirectorId.Text = session.Id.ToString()[..8];
            HeaderSessionIdPanel.IsVisible = true;
        }
        else
        {
            HeaderSessionId.Text = "Not linked";
            HeaderDirectorId.Text = session.Id.ToString()[..8];
            HeaderSessionIdPanel.IsVisible = true;
        }

        UpdateHeaderVerification(_activeSession);
    }

    private static readonly ISolidColorBrush VerifiedBadgeBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly ISolidColorBrush WarningBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private void UpdateHeaderVerification(SessionViewModel vm)
    {
        if (vm.IsVerified)
        {
            HeaderVerificationBadge.Background = VerifiedBadgeBrush;
            HeaderVerificationText.Text = "OK";
            HeaderVerificationBadge.IsVisible = true;
            BtnRelink.IsVisible = false;
        }
        else if (vm.HasVerificationWarning)
        {
            HeaderVerificationBadge.Background = WarningBadgeBrush;
            HeaderVerificationText.Text = "!";
            HeaderVerificationBadge.IsVisible = true;
            BtnRelink.IsVisible = true;
        }
        else
        {
            HeaderVerificationBadge.IsVisible = false;
            BtnRelink.IsVisible = true;
        }

        var tooltip = vm.VerificationStatusText;
        if (!string.IsNullOrEmpty(vm.VerifiedFirstPrompt))
            tooltip += $"\n\nFirst prompt: {vm.VerifiedFirstPrompt}";
        ToolTip.SetTip(HeaderVerificationBadge, tooltip);
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

    private void BtnRefreshTerminal_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRefreshTerminal_Click");
        RefreshTerminal();
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

        // Find the target session under the drop point
        var pos = e.GetPosition(SessionList);
        int toIndex = GetSessionDropIndex(pos);

        if (fromIndex < toIndex)
            toIndex--;

        toIndex = Math.Max(0, Math.Min(toIndex, _sessions.Count - 1));

        if (fromIndex != toIndex)
        {
            FileLog.Write($"[MainWindow] SessionList_Drop: moving session from {fromIndex} to {toIndex}");
            _sessions.Move(fromIndex, toIndex);
            SessionList.SelectedItem = draggedVm;
            PersistSessionState();
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

        if (result != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: cancelled");
            return;
        }

        var resumeSessionId = dialog.SelectedResumeSessionId;
        var agentKind = dialog.SelectedAgentKind;

        // Build agent arguments. Claude flags don't apply to Pi.
        string agentArgs;
        if (agentKind == AgentKind.ClaudeCode)
        {
            var claudeArgs = "";
            if (dialog.EnableRemoteControl)
                claudeArgs = "remote-control ";
            if (dialog.BypassPermissions)
                claudeArgs += "--dangerously-skip-permissions ";
            agentArgs = claudeArgs.Trim();
        }
        else
        {
            agentArgs = string.Empty;
        }

        FileLog.Write($"[MainWindow] ShowNewSessionDialog: path={dialog.SelectedPath}, agent={agentKind}, resume={resumeSessionId ?? "null"}, bypassPermissions={dialog.BypassPermissions}, remoteControl={dialog.EnableRemoteControl}");

        var vm = CreateSession(dialog.SelectedPath, resumeSessionId, agentArgs, agentKind);
        if (vm == null) return;

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

    private void BtnAppMenu_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnAppMenu_Click");
        _ = ShowAppMenu();
    }

    private async Task ShowAppMenu()
    {
        var app = (App)global::Avalonia.Application.Current!;

        var menu = new ContextMenu();

        var saveWorkspace = new MenuItem { Header = "Save Workspace..." };
        saveWorkspace.Click += async (_, _) =>
        {
            var sessionData = _sessions.Select(vm => new SessionData(
                vm.DisplayName,
                vm.Session.RepoPath,
                vm.Session.CustomName,
                vm.Session.CustomColor,
                vm.Session.ClaudeArgs));
            var dialog = new SaveWorkspaceDialog(app.WorkspaceStore, sessionData);
            await dialog.ShowDialog<bool?>(this);
        };

        var loadWorkspace = new MenuItem { Header = "Load Workspace..." };
        loadWorkspace.Click += async (_, _) =>
        {
            var dialog = new LoadWorkspaceDialog(app.WorkspaceStore);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.SelectedWorkspace != null)
            {
                if (_sessions.Count > 0)
                    await CloseAllSessionsAsync();
                await LoadWorkspaceAsync(dialog.SelectedWorkspace);
            }
        };

        var clearWorkspace = new MenuItem { Header = "Clear Workspace" };
        clearWorkspace.Click += async (_, _) =>
        {
            if (_sessions.Count == 0) return;
            await CloseAllSessionsAsync();
        };

        var separator1 = new Separator();

        var openLogs = new MenuItem { Header = "Open Logs" };
        openLogs.Click += (_, _) =>
        {
            var logDir = Path.GetDirectoryName(FileLog.CurrentLogPath);
            if (logDir != null && Directory.Exists(logDir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
        };

        var separator2 = new Separator();

        var repositories = new MenuItem { Header = "Repositories..." };
        repositories.Click += async (_, _) =>
        {
            FileLog.Write("[MainWindow] Menu: Repositories");
            var dialog = new RepositoryManagerDialog(app.RootDirectoryStore);
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
        };

        var accounts = new MenuItem { Header = "Accounts..." };
        accounts.Click += async (_, _) =>
        {
            FileLog.Write("[MainWindow] Menu: Accounts");
            var dialog = new AccountsDialog(app.ClaudeAccountStore);
            await dialog.ShowDialog<bool?>(this);
        };

        var manager = new MenuItem { Header = "Manager (multi-session)" };
        manager.Click += (_, _) =>
        {
            FileLog.Write("[MainWindow] Menu: Manager");
            // Select the Manager tab in the right panel
            RightPanelTabs.SelectedItem = TabItemManager;
        };

        var separator3 = new Separator();

        var openSessions = new MenuItem { Header = "Open Sessions File" };
        openSessions.Click += (_, _) =>
        {
            FileLog.Write("[MainWindow] Menu: Open Sessions File");
            var filePath = app.SessionStateStore.FilePath;
            if (File.Exists(filePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                    { UseShellExecute = true });
            }
            else
            {
                ShowNotification($"Sessions file not found: {filePath}");
            }
        };

        var openHistory = new MenuItem { Header = "Open History Folder" };
        openHistory.Click += (_, _) =>
        {
            FileLog.Write("[MainWindow] Menu: Open History Folder");
            var folder = app.SessionHistoryStore.FolderPath;
            if (Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = folder, UseShellExecute = true });
            }
            else
            {
                ShowNotification($"History folder not found: {folder}");
            }
        };

        var historyVsCode = new MenuItem { Header = "History in VS Code" };
        historyVsCode.Click += (_, _) =>
        {
            FileLog.Write("[MainWindow] Menu: History in VS Code");
            var folder = app.SessionHistoryStore.FolderPath;
            if (Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("code", $"\"{folder}\"")
                    { UseShellExecute = true });
            }
            else
            {
                ShowNotification($"History folder not found: {folder}");
            }
        };

        menu.Items.Add(saveWorkspace);
        menu.Items.Add(loadWorkspace);
        menu.Items.Add(clearWorkspace);
        menu.Items.Add(separator1);
        menu.Items.Add(repositories);
        menu.Items.Add(accounts);
        menu.Items.Add(manager);
        menu.Items.Add(separator2);
        menu.Items.Add(openLogs);
        menu.Items.Add(openSessions);
        menu.Items.Add(openHistory);
        menu.Items.Add(historyVsCode);

        menu.Open(BtnAppMenu);
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
        FileLog.Write("[MainWindow] BtnSettings_Click");
        var repoPath = _activeSession?.Session.RepoPath;
        var dialog = new ClaudeConfigDialog(repoPath);
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

    private void AgentTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Agent");
    }

    private void TerminalTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Terminal");
    }

    private void SourceControlTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("SourceControl");
    }

    private bool _commsInitialized;

    private async void BtnComms_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnComms_Click: opening Comms overlay");

        // Close connections overlay if open
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }

        CommsOverlay.IsVisible = true;

        if (!_commsInitialized)
        {
            _commsInitialized = true;
            await CommManagerView.InitializeAsync();
            CommManagerView.PendingCountChanged += count =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CommsBadge.IsVisible = count > 0;
                    CommsBadgeText.Text = count.ToString();
                });
            };
        }
        CommManagerView.StartPolling();
    }

    private void BtnCommsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnCommsClose_Click: closing Comms overlay");
        CommsOverlay.IsVisible = false;
        if (_commsInitialized)
            CommManagerView.StopPolling();
    }

    private bool _connectionsInitialized;

    private void BtnConnections_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnConnections_Click: opening Connections overlay");

        // Close comms overlay if open
        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }

        ConnectionsOverlay.IsVisible = true;
        _connectionsInitialized = true;
        ConnectionsView.StartPolling();
    }

    private void BtnConnectionsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnConnectionsClose_Click: closing Connections overlay");
        ConnectionsOverlay.IsVisible = false;
        if (_connectionsInitialized)
            ConnectionsView.StopPolling();
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
        AgentTabButton.Background = tab == "Agent" ? accentBrush : TransparentBrush;
        AgentTabButton.Foreground = tab == "Agent" ? whiteBrush : InactiveTextBrush;
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
        AgentPanel.IsVisible = tab == "Agent";
        TerminalPanel.IsVisible = tab == "Terminal";
        SourceControlPanel.IsVisible = tab == "SourceControl";
        DocumentPanel.IsVisible = isDocTab;

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

        await _activeSession.Session.SendTextAsync(text + "\n");

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
        NotificationBar.IsVisible = true;
    }

    private void ClearNotification()
    {
        NotificationText.Text = string.Empty;
        NotificationIcon.IsVisible = false;
        NotificationBar.IsVisible = false;
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

    // ==================== HOOK EVENTS ====================

    private void OnHookEventReceived(PipeMessage msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var vm = new HookEventViewModel(msg);

            _allHookEvents.Add(vm);

            // Cap at 500 events
            if (_allHookEvents.Count > 500)
                _allHookEvents.RemoveAt(0);

            // Show if matches active session or no session selected
            var activeClaudeId = _activeSession?.Session.ClaudeSessionId;
            if (activeClaudeId == null || vm.SessionId == activeClaudeId)
            {
                _hookEvents.Add(vm);
                HookEventsEmptyText.IsVisible = false;
                HookEventList.IsVisible = true;

                // Auto-scroll to bottom
                if (HookEventList.ItemCount > 0)
                    HookEventList.ScrollIntoView(vm);
            }

            // Refresh Claude metadata on Stop events (end of turn - metadata may have updated)
            if (msg.HookEventName == "Stop" && !string.IsNullOrEmpty(msg.SessionId))
            {
                var session = _sessionManager.GetSessionByClaudeId(msg.SessionId);
                if (session != null)
                {
                    var sessionVm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
                    if (sessionVm != null)
                    {
                        sessionVm.RefreshClaudeMetadata();
                        session.VerifyClaudeSession();
                    }
                }
            }
        });
    }

    private void RefreshHookEventsPanel()
    {
        _hookEvents.Clear();
        var activeClaudeId = _activeSession?.Session.ClaudeSessionId;

        foreach (var evt in _allHookEvents)
        {
            if (activeClaudeId == null || evt.SessionId == activeClaudeId)
                _hookEvents.Add(evt);
        }

        HookEventsEmptyText.IsVisible = _hookEvents.Count == 0;
        HookEventList.IsVisible = _hookEvents.Count > 0;

        if (_hookEvents.Count > 0)
            HookEventList.ScrollIntoView(_hookEvents[_hookEvents.Count - 1]);
    }

    private void BtnClearHookEvents_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearHookEvents_Click");
        _hookEvents.Clear();
        HookEventsEmptyText.IsVisible = true;
        HookEventList.IsVisible = false;
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

    private async Task InitializeScreenshotsPanelAsync()
    {
        FileLog.Write("[MainWindow] InitializeScreenshotsPanelAsync: starting");

        try
        {
            _screenshotsDirectory = await Task.Run(() => ResolveScreenshotsDirectory());

            if (_screenshotsDirectory == null || !Directory.Exists(_screenshotsDirectory))
            {
                FileLog.Write("[MainWindow] InitializeScreenshotsPanelAsync: no screenshots directory found");
                return;
            }

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

    private static string? ResolveScreenshotsDirectory()
    {
        // Check cc-director config first
        try
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "config");
            var configPath = Path.Combine(configDir, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("screenshots", out var ss) &&
                    ss.TryGetProperty("source_directory", out var dir))
                {
                    var path = dir.GetString();
                    if (path != null && Directory.Exists(path))
                        return path;
                }
            }
        }
        catch { /* Non-critical */ }

        // Auto-detect OneDrive Screenshots
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Path.Combine(userProfile, "OneDrive", "Pictures", "Screenshots");
        if (Directory.Exists(oneDrive))
            return oneDrive;

        // Local Pictures/Screenshots
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var local = Path.Combine(pictures, "Screenshots");
        if (Directory.Exists(local))
            return local;

        return null;
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

    private async void ScreenshotCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotCopyPath_Click: {filePath}");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(filePath);
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
        FileLog.Write("[MainWindow] OnClosing");

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

        // Unwire hook events and session registration
        try
        {
            var app = (App)global::Avalonia.Application.Current!;
            app.EventRouter.OnRawMessage -= OnHookEventReceived;
            _sessionManager.OnClaudeSessionRegistered -= OnClaudeSessionRegistered;
            _sessionManager.OnSessionCreated -= OnExternalSessionCreated;
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
