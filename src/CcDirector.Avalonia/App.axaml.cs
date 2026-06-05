using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CcDirector.ControlApi;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Scheduler;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Update;
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
    public SchedulerService? Scheduler { get; private set; }
    public UpdateService? Updater { get; private set; }

    public bool SandboxMode { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void RegisterServices()
    {
        base.RegisterServices();
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
            LoadConfiguration();

            // Run all heavy initialization on background thread, then swap to main window
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await Task.Run(() => InitializeServices(splash));

                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                splash.Close();

                StartUpdateService(mainWindow);
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

        DetectAbnormalTermination();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            FileLog.Write($"[App] UNHANDLED DOMAIN EXCEPTION (isTerminating={args.IsTerminating}): {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Write($"[App] UNOBSERVED TASK EXCEPTION: {args.Exception}");
            args.SetObserved();
        };

        // Avalonia UI-thread exceptions are NOT caught by AppDomain.UnhandledException
        // when they originate in dispatcher-posted callbacks or binding/render paths.
        // Without this, an exception in a Dispatcher.UIThread.Post lambda (e.g. the
        // CleanView pending-question sync or wingman status callback) can vanish
        // the process with no log line. Marking Handled=true keeps the app alive so
        // the user sees the consequence in the UI instead of a silent disappearance.
        global::Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            FileLog.Write($"[App] UNHANDLED UI-THREAD EXCEPTION: {args.Exception}");
            args.Handled = true;
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

        UpdateSplashStatus(splash, "Starting scheduler...");
        StartScheduler(log);
    }

    /// <summary>
    /// Construct the auto-updater and kick off a background "check for updates"
    /// shortly after the main window is shown. Inert for dev/slot builds (the
    /// UpdaterEnabled assembly marker is only emitted by CI release builds).
    /// Never blocks the UI thread; failures only log.
    /// </summary>
    private void StartUpdateService(MainWindow mainWindow)
    {
        try
        {
            var enabled = typeof(App).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Any(a => a.Key == "UpdaterEnabled" &&
                          string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));

            var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var options = new UpdateOptions
            {
                Enabled = enabled,
                CurrentVersion = current,
                InstallTarget = UpdateInstaller.InstallTarget(),
            };

            Updater = new UpdateService(options);
            Updater.UpdateStaged += staged =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => mainWindow.ShowUpdateReady(staged.Version));

            FileLog.Write($"[App] StartUpdateService: enabled={enabled}, current={current}, target={options.InstallTarget}");

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await Updater.CheckAndStageAsync();   // initial Director self-check (inert if !enabled)
                if (!enabled) return;                 // dev/slot build: no periodic auto-update

                // Periodic silent auto-update of the per-user tier (Director self + tools), on the
                // configured cadence. Tools aren't locked, so they swap in place; the Director self-update
                // stages and applies at the next restart. All failures only log. Re-reads the config each
                // cycle so toggling autoUpdate.enabled / intervalHours takes effect without a restart.
                var layout = CcDirector.Setup.Engine.InstallLayout.Default();
                while (true)
                {
                    var cfg = CcDirector.Setup.Engine.AutoUpdateConfig.Load(layout);
                    if (cfg.Enabled)
                    {
                        try
                        {
                            await Updater.CheckAndStageAsync();
                            var toolResult = await new CcDirector.Setup.Engine.ToolUpdater(layout).RefreshAsync();
                            FileLog.Write($"[App] tool auto-update: updated={toolResult.Updated}, failed={toolResult.Failed}");
                        }
                        catch (Exception ex)
                        {
                            FileLog.Write($"[App] auto-update cycle FAILED: {ex.Message}");
                        }
                    }
                    // When enabled, wait the configured interval; when disabled, re-poll the config hourly.
                    await Task.Delay(cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1));
                }
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] StartUpdateService FAILED: {ex.Message}");
        }
    }

    private void StartScheduler(Action<string> log)
    {
        try
        {
            var tickInterval = TimeSpan.FromMinutes(5);
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Scheduler", out var section)
                        && section.TryGetProperty("TickIntervalMinutes", out var t)
                        && t.TryGetInt32(out var mins)
                        && mins > 0)
                    {
                        tickInterval = TimeSpan.FromMinutes(mins);
                    }
                }
                catch (Exception ex)
                {
                    log($"Scheduler: failed to read TickIntervalMinutes from appsettings.json: {ex.Message}");
                }
            }

            var statePath = Path.Combine(CcStorage.ToolConfig("director"), "scheduler-state.json");
            var leaderIdentityPath = Path.Combine(CcStorage.ToolConfig("director"), "scheduler-leader.json");
            var runnersConfigPath = RunnersConfig.DefaultPath();
            Scheduler = new SchedulerService(
                tickInterval: tickInterval,
                statePath: statePath,
                leaderIdentityPath: leaderIdentityPath,
                runnersConfigPath: runnersConfigPath);

            var runners = RunnersConfig.LoadOrSeed(log: log);
            foreach (var runner in runners) Scheduler.RegisterRunner(runner);

            Scheduler.Start();
            log($"Scheduler started (tickInterval={tickInterval}, runners={Scheduler.Queue.Runners.Count}, configPath={RunnersConfig.DefaultPath()}, statePath={statePath})");
        }
        catch (Exception ex)
        {
            log($"Scheduler failed to start: {ex.Message}");
        }
    }

    private void StartControlApi(Action<string> log)
    {
        try
        {
            // Clean semver on the wire (gateway registration / status surfaces).
            var version = AppVersion.Semver;
            Func<Task> requestShutdown = () =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                        lifetime.Shutdown();
                });
                return Task.CompletedTask;
            };

            ControlApiHost = new ControlApiHost(SessionManager, version, requestShutdown, repositoryRegistry: RepositoryRegistry, schedulerAccessor: () => Scheduler);

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

            if (Scheduler != null)
            {
                try { Scheduler.Stop(); Scheduler.Dispose(); }
                catch (Exception ex) { log($"Scheduler stop error: {ex.Message}"); }
            }

            ClaudeUsageService?.Dispose();
            BackupCleaner?.Dispose();
            NulFileWatcher?.Dispose();
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

    // Crash-sentinel probe. InstanceRegistration writes a per-Director JSON file
    // at startup and deletes it on clean shutdown. A surviving file means the
    // previous Director with that PID died abnormally (force-kill, native crash,
    // power loss). We log + clean those up at startup so abnormal terminations
    // become visible in the log instead of silent disappearances. The matching
    // log file is found by PID so forensics can pick up where they left off.
    private void DetectAbnormalTermination()
    {
        try
        {
            var instancesDir = InstanceRegistration.InstancesDirectory;
            if (!Directory.Exists(instancesDir)) return;

            var ourPid = Environment.ProcessId;
            int stale = 0;
            foreach (var path in Directory.EnumerateFiles(instancesDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("pid", out var pidProp))
                        continue;
                    var pid = pidProp.GetInt32();
                    if (pid == ourPid) continue;

                    bool alive;
                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(pid);
                        alive = !proc.HasExited;
                    }
                    catch (ArgumentException) { alive = false; }
                    catch (InvalidOperationException) { alive = false; }

                    if (alive) continue;

                    var directorId = doc.RootElement.TryGetProperty("directorId", out var idProp)
                        ? idProp.GetString() ?? "?" : "?";
                    var startedAt = doc.RootElement.TryGetProperty("startedAt", out var s)
                        ? s.GetString() ?? "?" : "?";
                    var logPath = FindLogForPid(pid);
                    FileLog.Write(
                        $"[App] STALE INSTANCE (abnormal termination): directorId={directorId}, " +
                        $"pid={pid}, startedAt={startedAt}, log={logPath ?? "<not found>"}, file={path}");
                    try { File.Delete(path); } catch (Exception ex) {
                        FileLog.Write($"[App] DetectAbnormalTermination: failed to delete stale {path}: {ex.Message}");
                    }
                    stale++;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[App] DetectAbnormalTermination: failed to inspect {path}: {ex.Message}");
                }
            }
            FileLog.Write($"[App] DetectAbnormalTermination: scanned {instancesDir}, stale={stale}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] DetectAbnormalTermination FAILED: {ex.Message}");
        }
    }

    private static string? FindLogForPid(int pid)
    {
        try
        {
            var logDir = CcStorage.ToolLogs("director");
            if (!Directory.Exists(logDir)) return null;
            // Logs are named director-YYYY-MM-DD-{pid}.log. There can be more than
            // one if the PID was rolled over a day boundary; pick the newest.
            var match = Directory.EnumerateFiles(logDir, $"director-*-{pid}.log")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            return match;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] FindLogForPid({pid}) FAILED: {ex.Message}");
            return null;
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

            if (doc.RootElement.TryGetProperty("Voice", out var voiceSection))
            {
                if (voiceSection.TryGetProperty("OpenAiKey", out var keyProp))
                    Options.OpenAiKey = keyProp.GetString();
                if (voiceSection.TryGetProperty("TtsVoice", out var voiceProp))
                    Options.TtsVoice = voiceProp.GetString() ?? Options.TtsVoice;
                if (voiceSection.TryGetProperty("TtsModel", out var modelProp))
                    Options.TtsModel = modelProp.GetString() ?? Options.TtsModel;
            }

            if (doc.RootElement.TryGetProperty("Chat", out var chatSection)
                && chatSection.TryGetProperty("SessionRepoPath", out var repoProp))
            {
                Options.ChatSessionRepoPath = repoProp.GetString();
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
