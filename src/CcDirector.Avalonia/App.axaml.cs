using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CcDirector.ControlApi;
using CcDirector.Core.Account;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Scheduler;
using CcDirector.Core.Sessions;
using CcDirector.Core.Settings;
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

    /// <summary>
    /// Durable crash journal of this Director's live sessions (issue #212 L5). Null until the
    /// Control API has started and a DirectorId is known. Updated whenever the session set
    /// changes; deleted on clean shutdown, so a surviving file means an abnormal death.
    /// </summary>
    public DirectorCrashJournal? CrashJournal { get; private set; }
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

            // Run all heavy initialization on background thread, then boot straight to the main window.
            // The whole path is guarded so a startup failure here surfaces a visible error dialog and a
            // crash file -- never a stuck splash or a silent vanish (issue #242). Without this guard an
            // exception in InitializeServices/ShowMainWindow is swallowed by the dispatcher's
            // UnhandledException handler (Handled=true) and the user sees a frozen splash forever.
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Task.Run(() => InitializeServices(splash));

                    // No account startup gate (issue #651): the account lives entirely on the Gateway now,
                    // so the Director never gates its own startup, never signs in, and never opens a browser
                    // loopback sign-in. It always boots straight to the main window. The read-only Account
                    // panel in Settings reads the Gateway's /account/status for display only.
                    FileLog.Write("[CcDirector] Booting straight to the main window (account is managed by the Gateway, issue #651)");

                    ShowMainWindow(desktop);
                    splash.Close();

                    // The build came up healthy: clear any pending post-update health check so a
                    // successful update is trusted and never rolled back (issue #242).
                    UpdateInstaller.MarkCurrentBuildHealthy();
                }
                catch (Exception ex)
                {
                    HandleFatalStartupError(desktop, splash, ex);
                }
            }, global::Avalonia.Threading.DispatcherPriority.Background);

            desktop.ShutdownRequested += (_, _) => OnShutdown(msg => FileLog.Write($"[CcDirector] {msg}"));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates and shows the main window in place of the current window (the splash at startup), with
    /// no restart. The caller is responsible for closing the previous window. Starts the update service.
    /// Must be called on the UI thread.
    ///
    /// There is no startup gate and no first-run consent step any more (issue #651): the account lives
    /// entirely on the Gateway, so nothing in startup blocks the main window and the Director boots
    /// straight to it.
    /// </summary>
    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var mainWindow = new MainWindow();
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        FileLog.Write("[CcDirector] Main window shown");

        StartUpdateService(mainWindow);
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

        // Crash-recovery roster (issue #212 L5): claim any crash journal left by a Director
        // that died abnormally, so its interrupted sessions are recorded for recovery instead
        // of vanishing (as ten did on 2026-06-06). Detection only logs + preserves here; the
        // Cockpit "Interrupted sessions" surface and restore skill (later workstreams) consume
        // the claimed .dirty.json files.
        try
        {
            var dirty = DirectorCrashJournal.DetectAndClaim(Environment.ProcessId);
            if (dirty.Count > 0)
                FileLog.Write($"[App] Crash recovery: {dirty.Count} Director(s) died abnormally with " +
                    $"{dirty.Sum(d => d.Data.Sessions.Count)} recoverable session(s) total. See [DirectorCrashJournal] lines above.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] Crash-journal detection FAILED: {ex.Message}");
        }

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

        // Gateway Centralization Phase 2 migration (issue #642): the Gateway is the single account
        // authority, so the Director holds NO credential of its own (issue #651: the Director no longer
        // builds a local credential service or startup gate at all). Delete any stale local credential
        // blob an older build left behind, with a log line. A failure here only logs (it must never
        // block startup).
        UpdateSplashStatus(splash, "Checking account...");
        try
        {
            var deleted = DevThrottleCredentialMigration.DeleteStaleDirectorCredential();
            log(deleted
                ? "DevThrottle credential migration: deleted a stale Director credential blob (Gateway is the authority now, issue #642)"
                : "DevThrottle credential migration: no stale Director credential blob to delete (issue #642)");
        }
        catch (Exception ex)
        {
            log($"DevThrottle credential migration FAILED (ignored, the Director holds no credential regardless): {ex.Message}");
        }

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
            Updater.ProgressChanged += progress =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => mainWindow.OnUpdateProgress(progress));

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

            // commDispatcherAccessor (issue #329): resolved per request because the Engine
            // starts after this host and its dispatcher appears only after tool discovery.
            ControlApiHost = new ControlApiHost(SessionManager, version, requestShutdown, repositoryRegistry: RepositoryRegistry, schedulerAccessor: () => Scheduler, commDispatcherAccessor: () => EngineHost?.Dispatcher);

            _ = Task.Run(async () =>
            {
                try
                {
                    var port = await ControlApiHost.StartAsync();
                    log($"Control API listening on http://127.0.0.1:{port} (directorId={ControlApiHost.DirectorId})");

                    // Open this Director's crash journal now that its id is known (issue #212 L5).
                    // Seed it immediately so even a session-less Director records its presence;
                    // PersistSessionState refreshes the roster on every change after that.
                    CrashJournal = new DirectorCrashJournal(
                        ControlApiHost.DirectorId, Environment.ProcessId,
                        Environment.MachineName, Environment.UserName, DateTimeOffset.UtcNow);
                    CrashJournal.Update(Array.Empty<DirectorCrashJournalSession>());

                    // Fire the best-effort Director-startup telemetry once the Control API has a
                    // stable DirectorId (issue #632). This already runs off the UI thread inside this
                    // Task.Run, so it cannot delay the main window; failures are swallowed and logged.
                    await FireDirectorStartupTelemetryAsync(ControlApiHost.DirectorId, log).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log($"Control API failed to start: {ex.Message}");
                    // Surface the degraded state to the UI (loud sidebar indicator). The
                    // session-state services still started (StartSessionStateServices runs before
                    // the bind), so the local badge keeps working -- but remote/Gateway/Cockpit
                    // access is down, and that must not be silent.
                    ControlApiHost.ReportStartupFailure(ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            log($"Control API setup FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Fires the single best-effort Director-startup telemetry event (issue #632). Runs off the UI
    /// thread (its only caller is the Control API startup <c>Task.Run</c>), so a slow or failing report
    /// never delays the main window. A failure is swallowed and logged - it must never fail startup -
    /// and a missing Gateway URL is a logged no-op inside the reporter.
    /// </summary>
    private static async Task FireDirectorStartupTelemetryAsync(string directorId, Action<string> log)
    {
        try
        {
            var reporter = new DevThrottleDirectorStartupTelemetryReporter();
            await reporter.ReportStartupAsync(directorId).ConfigureAwait(false);
            log($"Director-startup telemetry reported (directorId={directorId})");
        }
        catch (Exception ex)
        {
            // Best-effort: a telemetry failure must never affect startup. Swallow + log only.
            log($"Director-startup telemetry FAILED (best-effort, ignored): {ex.Message}");
        }
    }

    private static void UpdateSplashStatus(SplashScreen splash, string text)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() => splash.StatusText.Text = text);
    }

    /// <summary>
    /// Surface a fatal startup error visibly instead of leaving a stuck splash or vanishing
    /// silently (issue #242). Logs the full exception, writes a findable crash file, closes the
    /// splash, shows the user a Win32 error dialog with the crash-file path, then shuts down.
    /// A Win32 MessageBox is used because the Avalonia app is in a broken state and may not be
    /// able to show its own window. Runs on the UI thread (called from the boot continuation).
    /// </summary>
    private static void HandleFatalStartupError(IClassicDesktopStyleApplicationLifetime desktop, SplashScreen splash, Exception ex)
    {
        FileLog.Write($"[CcDirector] FATAL startup error: {ex}");
        var crashPath = WriteStartupCrashFile(ex);

        try { splash.Close(); } catch (Exception closeEx) { FileLog.Write($"[CcDirector] Splash close after fatal error FAILED: {closeEx.Message}"); }

        var logPath = FileLog.CurrentLogPath ?? "(log path unavailable)";
        MessageBoxW(IntPtr.Zero,
            "Director failed to start:\n\n" +
            $"{ex.Message}\n\n" +
            $"Log file:\n{logPath}\n\n" +
            (crashPath is null ? "" : $"Crash details:\n{crashPath}"),
            "Director - Startup error", MB_OK | MB_ICONERROR | MB_TOPMOST);

        desktop.Shutdown(1);
    }

    /// <summary>
    /// Write a startup crash report to the director log directory so a startup failure leaves a
    /// findable trail even when the UI never came up. Best-effort; returns the path or null.
    /// </summary>
    private static string? WriteStartupCrashFile(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "logs", "director");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(path, $"[startup] {DateTime.Now:o}\n\n{ex}\n");
            return path;
        }
        catch
        {
            return null; // never let crash-reporting itself throw
        }
    }

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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

            // Clean shutdown: delete the crash journal so this exit is NOT seen as a crash
            // by the next Director's recovery scan (issue #212 L5).
            try { CrashJournal?.MarkClean(); }
            catch (Exception ex) { log($"CrashJournal MarkClean error: {ex.Message}"); }

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

            // Shared per-machine config first (config.json: comm_manager.email_tools)...
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

            // ...then the per-install appsettings.json next to the exe, which WINS over the
            // shared config: an isolated test Director (issue #329) points its own appsettings
            // at a test communications DB and a mock channel tool without ever touching the
            // shared per-machine config.json other Directors on this box read.
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
                    if (engineSection.TryGetProperty("BinDirectory", out var binDir))
                        engineOptions.BinDirectory = binDir.GetString() ?? engineOptions.BinDirectory;
                    if (engineSection.TryGetProperty("EmailToolNames", out var toolNames))
                    {
                        var tools = new List<string>();
                        foreach (var tool in toolNames.EnumerateArray())
                        {
                            var val = tool.GetString();
                            if (val != null) tools.Add(val);
                        }
                        if (tools.Count > 0) engineOptions.EmailToolNames = tools;
                        FileLog.Write($"[App] EmailToolNames from appsettings (overrides config.json): [{string.Join(", ", tools)}]");
                    }
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
                if (agentSection.TryGetProperty("PiPath", out var pp))
                    Options.PiPath = pp.GetString() ?? Options.PiPath;
                if (agentSection.TryGetProperty("CodexPath", out var cop))
                    Options.CodexPath = cop.GetString() ?? Options.CodexPath;
                if (agentSection.TryGetProperty("GeminiPath", out var gp))
                    Options.GeminiPath = gp.GetString() ?? Options.GeminiPath;
                if (agentSection.TryGetProperty("OpenCodePath", out var ocp))
                    Options.OpenCodePath = ocp.GetString() ?? Options.OpenCodePath;
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

            ApplyConfiguredToolPaths();
            ApplyConfiguredToolPresets();
            ApplyConfiguredVoiceSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply user-editable tool paths from cc-director config.json. appsettings.json remains
    /// the legacy/default source, but Settings > Tools writes to config.json so users can fix
    /// paths live without editing the install directory.
    /// </summary>
    private void ApplyConfiguredToolPaths()
    {
        FileLog.Write("[App] ApplyConfiguredToolPaths");
        try
        {
            var root = CcDirectorConfigService.ReadRaw();
            var agent = root["agent"] as System.Text.Json.Nodes.JsonObject
                ?? root["Agent"] as System.Text.Json.Nodes.JsonObject;
            if (agent is null)
            {
                FileLog.Write("[App] ApplyConfiguredToolPaths: no agent section in config.json");
                return;
            }

            var claude = ReadToolPath(agent, "claude_path", "ClaudePath");
            if (!string.IsNullOrWhiteSpace(claude))
                ToolDetectionService.SetConfiguredPath(AgentKind.ClaudeCode, Options, claude);

            var pi = ReadToolPath(agent, "pi_path", "PiPath");
            if (!string.IsNullOrWhiteSpace(pi))
                ToolDetectionService.SetConfiguredPath(AgentKind.Pi, Options, pi);

            var codex = ReadToolPath(agent, "codex_path", "CodexPath");
            if (!string.IsNullOrWhiteSpace(codex))
                ToolDetectionService.SetConfiguredPath(AgentKind.Codex, Options, codex);

            var gemini = ReadToolPath(agent, "gemini_path", "GeminiPath");
            if (!string.IsNullOrWhiteSpace(gemini))
                ToolDetectionService.SetConfiguredPath(AgentKind.Gemini, Options, gemini);

            var openCode = ReadToolPath(agent, "opencode_path", "OpenCodePath");
            if (!string.IsNullOrWhiteSpace(openCode))
                ToolDetectionService.SetConfiguredPath(AgentKind.OpenCode, Options, openCode);

            FileLog.Write($"[App] ApplyConfiguredToolPaths: claude={Options.ClaudePath}, pi={Options.PiPath}, codex={Options.CodexPath}, gemini={Options.GeminiPath}, opencode={Options.OpenCodePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] ApplyConfiguredToolPaths FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Apply the machine-level per-tool command-line preset and default model from
    /// config.json (issue #391). The Tools page persists each tool's selected preset,
    /// optional args override, and default model under <c>agent.tools.&lt;key&gt;</c>; this
    /// resolves Claude Code's effective command line (preset/override plus <c>--model</c>
    /// when a default model is set) into <see cref="AgentOptions.DefaultClaudeArgs"/>, which
    /// is exactly what <see cref="CcDirector.Core.Agents.ClaudeAgent"/> launches with.
    /// When no per-tool config exists the catalog default (Standard, no skip-permissions)
    /// applies, so a fresh install never auto-skips permissions.
    /// </summary>
    private void ApplyConfiguredToolPresets()
    {
        FileLog.Write("[App] ApplyConfiguredToolPresets");
        try
        {
            var claudeConfig = AgentToolConfig.Load(AgentKind.ClaudeCode);
            Options.DefaultClaudeArgs = claudeConfig.ResolveEffectiveCommandLineArguments();
            FileLog.Write($"[App] ApplyConfiguredToolPresets: claude preset={claudeConfig.PresetName}, model={(string.IsNullOrWhiteSpace(claudeConfig.DefaultModel) ? "<none>" : claudeConfig.DefaultModel)}, defaultArgs='{Options.DefaultClaudeArgs}'");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] ApplyConfiguredToolPresets FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Overlay voice settings from cc-director config.json onto the running options. appsettings.json
    /// (read in <see cref="LoadConfiguration"/>) is the install-dir default; config.json is the
    /// user-editable runtime source that Settings > Voice writes to, so a key set in the UI survives
    /// app updates that overwrite the install directory. config.json wins when present.
    /// </summary>
    private void ApplyConfiguredVoiceSettings()
    {
        FileLog.Write("[App] ApplyConfiguredVoiceSettings");
        try
        {
            var root = CcDirectorConfigService.ReadRaw();
            var voice = root["Voice"] as System.Text.Json.Nodes.JsonObject
                ?? root["voice"] as System.Text.Json.Nodes.JsonObject;
            if (voice is null)
            {
                FileLog.Write("[App] ApplyConfiguredVoiceSettings: no Voice section in config.json");
                return;
            }

            if (voice["OpenAiKey"] is System.Text.Json.Nodes.JsonValue keyVal)
            {
                var key = keyVal.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(key))
                    Options.OpenAiKey = key;
            }

            // Never log the key itself - only whether one is now configured.
            FileLog.Write($"[App] ApplyConfiguredVoiceSettings: openAiKey={(string.IsNullOrWhiteSpace(Options.OpenAiKey) ? "<unset>" : "<set>")}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] ApplyConfiguredVoiceSettings FAILED: {ex.Message}");
            throw;
        }
    }

    private static string? ReadToolPath(System.Text.Json.Nodes.JsonObject agent, string snakeKey, string pascalKey)
    {
        if (agent[snakeKey] is System.Text.Json.Nodes.JsonValue snake)
            return snake.GetValue<string>();
        if (agent[pascalKey] is System.Text.Json.Nodes.JsonValue pascal)
            return pascal.GetValue<string>();
        return null;
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
