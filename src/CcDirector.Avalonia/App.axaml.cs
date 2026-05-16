using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CcDirector.ControlApi;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Hooks;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Engine;

namespace CcDirector.Avalonia;

public partial class App : Application
{
    // All null! fields are initialized in OnFrameworkInitializationCompleted before any other code accesses them
    public SessionManager SessionManager { get; private set; } = null!;
    public AgentOptions Options { get; private set; } = null!;
    public List<RepositoryConfig> Repositories { get; private set; } = new();
    public RepositoryRegistry RepositoryRegistry { get; private set; } = null!;
    public RootDirectoryStore RootDirectoryStore { get; private set; } = null!;
    public IDirectorServer DirectorServer { get; private set; } = null!;
    public EventRouter EventRouter { get; private set; } = null!;
    public SessionStateStore SessionStateStore { get; private set; } = null!;
    public RecentSessionStore RecentSessionStore { get; private set; } = null!;
    public SessionHistoryStore SessionHistoryStore { get; private set; } = null!;
    public NulFileWatcher? NulFileWatcher { get; private set; }
    public BackupCleaner BackupCleaner { get; private set; } = null!;
    public ClaudeAccountStore ClaudeAccountStore { get; private set; } = null!;
    public ClaudeUsageService ClaudeUsageService { get; private set; } = null!;
    public WorkspaceStore WorkspaceStore { get; private set; } = null!;
    public EngineHost? EngineHost { get; private set; }
    public ControlApiHost? ControlApiHost { get; private set; }

    public bool SandboxMode { get; private set; }

    /// <summary>
    /// When true, suppress the startup workspace picker dialog (the modal that asks
    /// "Load workspace?" when at least one saved workspace exists). Useful for
    /// headless / scripted launches such as the Manager dashboard testing flow.
    /// Triggered by the <c>--skip-workspace-picker</c> command-line argument.
    /// </summary>
    public bool SkipWorkspacePicker { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show splash screen IMMEDIATELY -- before any heavy init
            var splash = new SplashScreen();
            desktop.MainWindow = splash;
            splash.Show();

            // Parse command-line arguments (lightweight)
            SandboxMode = desktop.Args?.Contains("--sandbox", StringComparer.OrdinalIgnoreCase) == true;
            SkipWorkspacePicker = desktop.Args?.Contains("--skip-workspace-picker", StringComparer.OrdinalIgnoreCase) == true;
            LoadConfiguration();

            // Run all heavy initialization on background thread, then swap to main window
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await Task.Run(() => InitializeServices(splash));

                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                splash.Close();
            }, global::Avalonia.Threading.DispatcherPriority.Background);

            desktop.ShutdownRequested += (_, _) => OnShutdown(msg => FileLog.Write($"[CcDirector] {msg}"));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeServices(SplashScreen splash)
    {
        RepositoryRegistry = new RepositoryRegistry();
        RepositoryRegistry.Load();
        RepositoryRegistry.SeedFrom(Repositories);

        RootDirectoryStore = new RootDirectoryStore();
        RootDirectoryStore.Load();

        SessionStateStore = new SessionStateStore();

        RecentSessionStore = new RecentSessionStore();
        RecentSessionStore.Load();

        SessionHistoryStore = new SessionHistoryStore();
        MigrateRecentSessionsToHistory();

        FileLog.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            FileLog.Write($"[App] UNHANDLED DOMAIN EXCEPTION (isTerminating={args.IsTerminating}): {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Write($"[App] UNOBSERVED TASK EXCEPTION: {args.Exception}");
            args.SetObserved();
        };

        try
        {
            CcDirector.Core.Storage.CcStorageMigration.EnsureMigrated();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] Storage migration FAILED: {ex}");
        }

        Action<string> log = msg => FileLog.Write($"[CcDirector] {msg}");
        log($"CC Director (Avalonia) starting (SandboxMode={SandboxMode}), log file: {FileLog.CurrentLogPath}");

        UpdateSplashStatus(splash, "Initializing sessions...");
        SessionManager = new SessionManager(Options, log);
        SessionManager.ScanForOrphans();

        // Workspaces replace session restore -- clear persisted data
        SessionStateStore.Clear();

        UpdateSplashStatus(splash, "Starting event system...");
        EventRouter = new EventRouter(SessionManager, log);
        DirectorServer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new DirectorFileEventWatcher(log)
            : new UnixSocketServer(log);
        DirectorServer.OnMessageReceived += EventRouter.Route;
        DirectorServer.Start();
        log($"Hook event server started: {DirectorServer.GetType().Name}");

        _ = InstallHooksAsync(log);

        // NUL files are a Windows-only filesystem quirk (reserved device name).
        // On Unix, "nul" is just a regular filename and creates no problems.
        if (OperatingSystem.IsWindows())
        {
            NulFileWatcher = new NulFileWatcher(log: log);
            NulFileWatcher.OnNulFileDeleted = path => log($"Deleted NUL file: {path}");
            NulFileWatcher.OnDeletionFailed = (path, ex) => log($"Failed to delete NUL file {path}: {ex.Message}");
            NulFileWatcher.Start();
        }

        BackupCleaner = new BackupCleaner(log: log);
        BackupCleaner.OnCorruptedFileDeleted = path => log($"Deleted corrupted backup: {path}");
        BackupCleaner.OnDeletionFailed = (path, ex) => log($"Failed to delete corrupted backup {path}: {ex.Message}");
        BackupCleaner.Start();

        UpdateSplashStatus(splash, "Loading accounts...");
        ClaudeAccountStore = new ClaudeAccountStore();
        ClaudeAccountStore.Load();
        log($"Claude accounts loaded: {ClaudeAccountStore.Accounts.Count}");

        ClaudeUsageService = new ClaudeUsageService(ClaudeAccountStore);
        ClaudeUsageService.Start();
        log("Claude usage service started");

        WorkspaceStore = new WorkspaceStore();
        log("Workspace store initialized");

        UpdateSplashStatus(splash, "Starting engine...");
        StartEngine(log);

        UpdateSplashStatus(splash, "Starting control API...");
        StartControlApi(log);
    }

    private void StartControlApi(Action<string> log)
    {
        try
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            Func<Task> requestShutdown = () =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                        lifetime.Shutdown();
                });
                return Task.CompletedTask;
            };

            ControlApiHost = new ControlApiHost(SessionManager, version, requestShutdown, repositoryRegistry: RepositoryRegistry);

            _ = Task.Run(async () =>
            {
                try
                {
                    var port = await ControlApiHost.StartAsync();
                    log($"Control API listening on http://127.0.0.1:{port} (directorId={ControlApiHost.DirectorId})");
                }
                catch (Exception ex)
                {
                    log($"Control API failed to start: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            log($"Control API setup FAILED: {ex.Message}");
        }
    }

    private static void UpdateSplashStatus(SplashScreen splash, string text)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() => splash.StatusText.Text = text);
    }

    internal void OnShutdown(Action<string> log)
    {
        try
        {
            // All async work runs on the thread pool via Task.Run to avoid
            // deadlocking with the UI thread's SynchronizationContext.
            // Without this, .Wait() blocks the UI thread while the async
            // continuations inside KillAllSessionsAsync/StopAsync need the
            // UI thread to resume -- classic deadlock that kept the process
            // alive for 15-20 seconds after the window closed.

            if (SessionManager != null)
            {
                try
                {
                    var killTask = Task.Run(() => SessionManager.KillAllSessionsAsync());
                    if (!killTask.Wait(TimeSpan.FromSeconds(3)))
                    {
                        log("KillAllSessionsAsync timed out after 3 seconds");
                        ForceKillRemainingProcesses();
                    }
                }
                catch (Exception ex)
                {
                    log($"KillAllSessionsAsync FAILED: {ex.Message}");
                    ForceKillRemainingProcesses();
                }
            }

            if (EngineHost != null)
            {
                try
                {
                    var engineStopTask = Task.Run(() => EngineHost.StopAsync());
                    if (!engineStopTask.Wait(TimeSpan.FromSeconds(2)))
                        log("Engine stop timed out after 2 seconds");
                }
                catch (Exception ex)
                {
                    log($"Engine stop error: {ex.Message}");
                }
                EngineHost.Dispose();
            }

            if (ControlApiHost != null)
            {
                try
                {
                    var stopTask = Task.Run(() => ControlApiHost.StopAsync());
                    if (!stopTask.Wait(TimeSpan.FromSeconds(2)))
                        log("ControlApiHost stop timed out after 2 seconds");
                }
                catch (Exception ex)
                {
                    log($"ControlApiHost stop error: {ex.Message}");
                }
            }

            ClaudeUsageService?.Dispose();
            BackupCleaner?.Dispose();
            NulFileWatcher?.Dispose();
            DirectorServer?.Dispose();
            EventRouter?.Dispose();
            SessionManager?.Dispose();

            FileLog.Write("[CcDirector] Exiting");
            FileLog.Stop();
        }
        finally
        {
            // Force-exit the process so the CLR doesn't linger waiting for
            // finalizers, GC, or stale timer callbacks to wind down.
            Environment.Exit(0);
        }
    }

    private void ForceKillRemainingProcesses()
    {
        if (SessionManager == null) return;

        var pids = SessionManager.GetTrackedProcessIds();
        foreach (var pid in pids)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    FileLog.Write($"[App] Force-killing process {pid}");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException) { }
            catch (Exception ex)
            {
                FileLog.Write($"[App] Failed to force-kill process {pid}: {ex.Message}");
            }
        }
    }

    private async Task InstallHooksAsync(Action<string> log)
    {
        try
        {
            HookRelayScript.EnsureWritten();
            log($"Hook relay script written to {HookRelayScript.ScriptPath}");
            await HookInstaller.InstallAsync(HookRelayScript.ScriptPath, log);
        }
        catch (Exception ex)
        {
            log($"Failed to install hooks: {ex.Message}");
        }
    }

    private void StartEngine(Action<string> log)
    {
        try
        {
            var engineOptions = new EngineOptions();
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("Engine", out var engineSection))
                {
                    if (engineSection.TryGetProperty("CommunicationsDbPath", out var dbPath))
                        engineOptions.CommunicationsDbPath = dbPath.GetString() ?? engineOptions.CommunicationsDbPath;
                    if (engineSection.TryGetProperty("DispatcherPollIntervalSeconds", out var poll))
                        engineOptions.DispatcherPollIntervalSeconds = poll.GetInt32();
                }
            }

            var ccConfigPath = CcStorage.ConfigJson();
            if (File.Exists(ccConfigPath))
            {
                var ccJson = File.ReadAllText(ccConfigPath);
                using var ccDoc = JsonDocument.Parse(ccJson);

                if (ccDoc.RootElement.TryGetProperty("comm_manager", out var cm) &&
                    cm.TryGetProperty("email_tools", out var emailTools))
                {
                    var tools = new List<string>();
                    foreach (var tool in emailTools.EnumerateArray())
                    {
                        var val = tool.GetString();
                        if (val != null) tools.Add(val);
                    }
                    if (tools.Count > 0) engineOptions.EmailToolNames = tools;
                    FileLog.Write($"[App] EmailToolNames from config: [{string.Join(", ", tools)}]");
                }
            }

            EngineHost = new EngineHost(engineOptions);
            EngineHost.OnEvent += e => log($"[Engine] {e.Type}: {e.Message}");
            EngineHost.Start();
            log("Engine started");
        }
        catch (Exception ex)
        {
            log($"Engine failed to start: {ex.Message}");
        }
    }

    private void LoadConfiguration()
    {
        Options = new AgentOptions();
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
            WriteDefaultConfig(configPath);

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Agent", out var agentSection))
            {
                if (agentSection.TryGetProperty("ClaudePath", out var cp))
                    Options.ClaudePath = cp.GetString() ?? "claude";
                if (agentSection.TryGetProperty("DefaultBufferSizeBytes", out var bs))
                    Options.DefaultBufferSizeBytes = bs.GetInt32();
                if (agentSection.TryGetProperty("GracefulShutdownTimeoutSeconds", out var gs))
                    Options.GracefulShutdownTimeoutSeconds = gs.GetInt32();
            }

            if (doc.RootElement.TryGetProperty("Repositories", out var reposSection))
            {
                Repositories = JsonSerializer.Deserialize<List<RepositoryConfig>>(
                    reposSection.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<RepositoryConfig>();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
        }
    }

    private static void WriteDefaultConfig(string configPath)
    {
        const string defaultConfig = """
            {
              "Agent": {
                "ClaudePath": "claude",
                "DefaultBufferSizeBytes": 2097152,
                "GracefulShutdownTimeoutSeconds": 5
              },
              "Repositories": []
            }
            """;

        try
        {
            File.WriteAllText(configPath, defaultConfig);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write default config: {ex.Message}");
        }
    }

    private void MigrateRecentSessionsToHistory()
    {
        var existing = SessionHistoryStore.LoadAll();
        if (existing.Count > 0)
            return;

        var recent = RecentSessionStore.GetRecent();
        if (recent.Count == 0)
            return;

        FileLog.Write($"[App] MigrateRecentSessionsToHistory: migrating {recent.Count} entries");

        foreach (var r in recent)
        {
            SessionHistoryStore.Save(new SessionHistoryEntry
            {
                Id = Guid.NewGuid(),
                CustomName = r.CustomName,
                CustomColor = r.CustomColor,
                RepoPath = r.RepoPath,
                ClaudeSessionId = r.ClaudeSessionId,
                CreatedAt = r.LastUsed,
                LastUsedAt = r.LastUsed,
            });
        }
    }
}
