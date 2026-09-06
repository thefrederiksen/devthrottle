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
using System.Linq;
using CcDirector.Core.Git;
using CcDirector.Core.Instances;
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

    /// <summary>
    /// The always-current model of the repositories under the registered roots. Owned by the app,
    /// scanned in the background from startup; the Repository screen subscribes and renders it.
    /// </summary>
    public RepositoryMonitor RepositoryMonitor { get; } = new(
        cachePath: System.IO.Path.Combine(
            CcDirector.Core.Storage.CcStorage.ToolConfig("director"), "repo-worktree-cache.json"));

    private RepositoryWatcher? _repositoryWatcher;
    public SessionStateStore SessionStateStore { get; private set; } = null!;

    /// <summary>
    /// Durable crash journal of this Director's live sessions (issue #212 L5). Null until the
    /// Control API has started and a DirectorId is known. Updated whenever the session set
    /// changes; deleted on clean shutdown, so a surviving file means an abnormal death.
    /// </summary>
    public DirectorCrashJournal? CrashJournal { get; private set; }

    /// <summary>
    /// The two lifecycle requests this Director answers from outside itself, with no network: "shut
    /// down" and "check for updates now". See <see cref="StartLifecycleSignals"/>.
    /// </summary>
    private CcDirector.Core.Lifecycle.ILifecycleSignalListener? _shutdownSignal;
    private CcDirector.Core.Lifecycle.ILifecycleSignalListener? _updateCheckSignal;
    public RecentSessionStore RecentSessionStore { get; private set; } = null!;
    public SessionHistoryStore SessionHistoryStore { get; private set; } = null!;
    public NulFileWatcher? NulFileWatcher { get; private set; }
    public BackupCleaner BackupCleaner { get; private set; } = null!;
    public ClaudeAccountStore ClaudeAccountStore { get; private set; } = null!;
    public ClaudeUsageService ClaudeUsageService { get; private set; } = null!;
    public WorkspaceStore WorkspaceStore { get; private set; } = null!;
    public EngineHost? EngineHost { get; private set; }
    public ControlApiHost? ControlApiHost { get; private set; }
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
                    // If more than one named instance exists and none was requested on the
                    // command line, ask which one to launch. Choosing a different instance
                    // relaunches it and shuts this (default) process down.
                    if (await MaybePickInstanceAsync(splash))
                    {
                        desktop.Shutdown();
                        return;
                    }

                    await Task.Run(() => InitializeServices(splash));

                    // No account startup gate (issue #651): the account lives entirely on the Gateway now,
                    // so the Director never gates its own startup, never signs in, and never opens a browser
                    // loopback sign-in. It always boots straight to the main window. The read-only Account
                    // panel in Settings reads the Gateway's /account/status for display only.
                    FileLog.Write("[CcDirector] Booting straight to the main window (account is managed by the Gateway, issue #651)");

                    ShowMainWindow(desktop);
                    splash.Close();

                    // The build came up healthy: clear any pending post-update health check so a
                    // successful update is trusted and never rolled back (issue #242). The return value
                    // is the version-change signal (issue #827): true means a Director self-update was
                    // just applied on this boot, the strongest signal the bundled tools manifest changed.
                    var selfUpdateApplied = UpdateInstaller.MarkCurrentBuildHealthy();

                    // Self-heal the cc-* tools in the background (issue #827): install-missing,
                    // purge-drift, repair-broken. Runs OFF the UI thread and fire-and-forget so it NEVER
                    // gates or delays boot (failures only log), gated by tools.autoUpdate.enabled.
                    StartToolReconcile(selfUpdateApplied);
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

        // The FIRST repository rescan starts HERE, not in InitializeServices (ruling R2-8): the
        // MainWindow constructor above wired RepositoryMonitor.LiveSessionsProvider, and the
        // monitor refuses to scan without it. Triggering the scan after the constructor makes
        // the wire-before-scan ordering structural, not incidental.
        StartRepositoryRescan();

        StartUpdateService(mainWindow);
    }

    /// <summary>
    /// Kick off a background rescan of the registered root directories into <see cref="RepositoryMonitor"/>.
    /// Fire-and-forget; results stream into the model as each repository is computed.
    /// </summary>
    public void StartRepositoryRescan()
    {
        var roots = RootDirectoryStore.Roots
            .Select(r => r.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        // The task is observed, never orphaned: a scan that throws (for example the monitor's
        // refuse-to-scan-unwired guard) must land in the log as an ERROR immediately, not
        // surface minutes later as an unobserved-task finalizer message.
        _ = System.Threading.Tasks.Task.Run(() => RepositoryMonitor.RescanAsync(roots))
            .ContinueWith(
                t => FileLog.Write($"[App] ERROR repository rescan FAILED: {t.Exception?.GetBaseException().Message}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// When multiple named instances exist and none was explicitly requested via
    /// <c>--instance</c>, show the selection box. Returns true when this process should
    /// abort because it relaunched a different instance; false to continue as the default.
    /// </summary>
    private static async Task<bool> MaybePickInstanceAsync(Window owner)
    {
        try
        {
            if (InstanceContext.WasExplicitlySelected)
                return false;

            var instances = NamedInstanceRegistry.List();
            if (instances.Count <= 1)
                return false;

            var dlg = new SelectDirectorDialog("Which director do you want to launch?");
            var ok = await dlg.ShowDialog<bool?>(owner);
            if (ok != true)
                return false; // cancelled -> continue as the default instance

            if (dlg.WantsNew)
            {
                var create = new CreateInstanceDialog();
                var created = await create.ShowDialog<bool?>(owner);
                if (created == true && create.CreatedInstance is not null && create.LaunchAfter)
                {
                    InstanceProcess.Launch(create.CreatedInstance.Name);
                    return true;
                }
                return false;
            }

            var slug = dlg.LaunchSlug;
            if (string.IsNullOrEmpty(slug))
                return false;
            if (string.Equals(slug, InstanceContext.Slug, StringComparison.OrdinalIgnoreCase))
                return false; // picked the current (default) instance -> continue

            InstanceProcess.Launch(slug);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] MaybePickInstance FAILED: {ex.Message}");
            return false;
        }
    }

    private void InitializeServices(SplashScreen splash)
    {
        RepositoryRegistry = new RepositoryRegistry();
        RepositoryRegistry.Load();
        RepositoryRegistry.SeedFrom(Repositories);

        RootDirectoryStore = new RootDirectoryStore();
        RootDirectoryStore.Load();

        // Warm start: show the last run's repositories instantly from the cache, then scan in the
        // background to re-verify and reconcile (issue #507, phase 2).
        RepositoryMonitor.LoadCache();

        // After each completed scan, watch the roots and every known repository's git signals so a
        // change recomputes only the affected repo - react to change instead of re-scanning (#510 A).
        _repositoryWatcher = new RepositoryWatcher(RepositoryMonitor);
        // A watcher buffer overflow or the periodic reconciliation tick asks for a full rescan, so
        // working-tree changes the incremental watch missed, a "git init" in an existing folder, a
        // slow clone, and any dropped events are eventually reconciled (issue 516).
        _repositoryWatcher.ReconciliationRequested += StartRepositoryRescan;
        RepositoryMonitor.ScanCompleted += () =>
        {
            try
            {
                var watchRoots = RootDirectoryStore.Roots
                    .Select(r => r.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();
                _repositoryWatcher.SyncWatches(watchRoots, RepositoryMonitor.Snapshot().Select(s => s.Path));
            }
            catch (Exception ex)
            {
                CcDirector.Core.Utilities.FileLog.Write($"[App] watcher sync failed: {ex.Message}");
            }
        };

        // NOTE: the first repository rescan is deliberately NOT started here. The monitor requires
        // its live-session source, which the MainWindow constructor wires - ShowMainWindow starts
        // the scan right after that wiring (ruling R2-8).

        SessionStateStore = new SessionStateStore();

        RecentSessionStore = new RecentSessionStore();
        RecentSessionStore.Load();

        SessionHistoryStore = new SessionHistoryStore();
        MigrateRecentSessionsToHistory();

        FileLog.Start();
        var retiredFilesRemoved = LegacyPrivacyDataCleanup.Run();
        if (retiredFilesRemoved > 0)
            FileLog.Write($"[App] Removed {retiredFilesRemoved} retired local tracking file(s).");

        DetectAbnormalTermination();

        // Crash-recovery roster (issue #212 L5): claim any crash journal left by a Director
        // that died abnormally, so its interrupted sessions are recorded for recovery instead
        // of vanishing (as ten did on 2026-06-06). Detection only logs + preserves here; the
        // Cockpit "Interrupted sessions" surface and restore skill (later workstreams) consume
        // the claimed .dirty.json files.
        try
        {
            // Sweep stale crash journals first (issue #961): only the last week of crashes is
            // useful for recovery, so older .dirty.json files are removed instead of accumulating.
            var swept = DirectorCrashJournal.SweepExpired();
            if (swept > 0)
                FileLog.Write($"[App] Crash recovery: swept {swept} stale crash journal(s) older than {DirectorCrashJournal.DirtyJournalRetention.TotalDays:0} days.");

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
        // Without this, an exception in a Dispatcher.UIThread.Post lambda (e.g. a
        // rail repaint or a wingman status callback) can vanish
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
        // The ONE place skill placement is turned on. It writes into the user's own home directory, so
        // it is opt-in and the running app is what opts in - see SessionManager.PlacesSkillsOnLaunch.
        SessionManager = new SessionManager(Options, log) { PlacesSkillsOnLaunch = true };
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

        // Gateway Centralization Phase 2 migration (issue #642), with the two-step install exception
        // (Slice A): the deletion of the Director's own credential blob is GATED on gateway presence.
        // With a gateway configured the Gateway is the single account authority (issue #651) and the
        // Director copy is genuinely stale, so it is deleted exactly as before. With NO gateway the
        // Director blob is the LIVE credential a gateway-less Director legitimately keeps, so it is kept.
        // A failure here only logs (it must never block startup).
        UpdateSplashStatus(splash, "Checking account...");
        try
        {
            var outcome = DevThrottleCredentialMigration.RunStartupMigration(GatewayConfig.Load());
            log(outcome switch
            {
                DirectorCredentialStartupOutcome.DeletedStaleBlob =>
                    "DevThrottle credential migration: deleted a stale Director credential blob (Gateway is the authority now, issue #642)",
                DirectorCredentialStartupOutcome.NoBlobToDelete =>
                    "DevThrottle credential migration: no stale Director credential blob to delete (issue #642)",
                _ =>
                    "DevThrottle credential migration: no gateway configured -> keeping the Director's own credential (Slice A exception to issue #642 for a gateway-less Director)",
            });
        }
        catch (Exception ex)
        {
            log($"DevThrottle credential migration FAILED (ignored, startup must not block): {ex.Message}");
        }

        UpdateSplashStatus(splash, "Starting engine...");
        StartEngine(log);

        StartLifecycleSignals(log);

        UpdateSplashStatus(splash, "Starting control API...");
        StartControlApi(log);
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
            {
                // Surface the "Restart now" banner so a person can restart by hand whenever they like.
                // Installing it without being asked is NOT done here any more (issue #1033): the launcher
                // watches for a staged update, waits until this Director holds no sessions, and then
                // stops it, swaps the build, starts it and confirms the new version answers. A Director
                // that swapped itself could never confirm anything - the only process able to check
                // whether the relaunch came up was the one that had just exited - so a relaunch that
                // never appeared was written down as a success.
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => mainWindow.ShowUpdateReady(staged.Version));
            };
            Updater.ProgressChanged += progress =>
            {
                // The board first, and off the UI thread: it is what the Control API and any later
                // surface read, and it must not depend on a window existing to be correct.
                UpdateStatusBoard.ReportProgress(progress);
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => mainWindow.OnUpdateProgress(progress));
            };

            // Registered even when this build has no updater. A development or slot build has to be able
            // to SAY it does not update itself; leaving the board empty would have made it look like a
            // machine that simply had nothing to report, which is the confusion this all exists to end.
            UpdateStatusBoard.Register(
                Updater,
                currentVersion: $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}",
                automaticUpdatesEnabled: enabled,
                runningSessionCount: () => SessionManager?.ListSessions().Count ?? 0);

            // The window is built before this runs, so its panel has nothing to show until now. Paint it
            // immediately rather than leaving a blank where the status goes until the next slow tick -
            // a gap where the status is missing is a small version of the whole problem.
            global::Avalonia.Threading.Dispatcher.UIThread.Post(mainWindow.RefreshUpdateStatus);

            FileLog.Write($"[App] StartUpdateService: enabled={enabled}, current={current}, target={options.InstallTarget}");

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                var outcome = await Updater.CheckAndStageAsync();   // initial Director self-check (inert if !enabled)
                if (!enabled) return;                               // dev/slot build: no periodic auto-update

                // Periodic silent auto-update of the per-user tier (Director self + tools), on the
                // configured cadence. Tools aren't locked, so they swap in place; the Director self-update
                // stages and applies at the next restart. All failures only log. Re-reads the config each
                // cycle so toggling autoUpdate.enabled / intervalHours takes effect without a restart.
                var layout = CcDirector.Setup.Engine.InstallLayout.Default();
                var releaseNotReadyRetry = new ReleaseNotReadyRetry();
                while (true)
                {
                    var cfg = CcDirector.Setup.Engine.AutoUpdateConfig.Load(layout);
                    if (cfg.Enabled)
                    {
                        try
                        {
                            outcome = await Updater.CheckAndStageAsync();
                            var toolResult = await new CcDirector.Setup.Engine.ToolUpdater(layout).RefreshAsync();
                            FileLog.Write($"[App] tool auto-update: updated={toolResult.Updated}, failed={toolResult.Failed}");
                        }
                        catch (Exception ex)
                        {
                            outcome = UpdatePhase.Failed;
                            FileLog.Write($"[App] auto-update cycle FAILED: {ex.Message}");
                        }

                        // The launcher's own update, which the Director owns (issue #2719). The mirror
                        // of the launcher owning the Director's: whatever replaces a binary has to
                        // outlive the process being replaced, and the launcher cannot honestly swap
                        // itself. Nothing owned this before, so a machine whose launcher fell behind
                        // became permanently uncommandable - and could not be fixed remotely, because
                        // the thing that would receive the fix was the broken thing. A Director update
                        // still reaches it, which is why this runs here.
                        //
                        // IT HAS ITS OWN try, DELIBERATELY. Inside the one above, a failure in the
                        // Director's own check or the tool refresh - a network timeout, most likely -
                        // skipped this entirely. That is precisely backwards: a launcher build is
                        // ALREADY DOWNLOADED and needs no network at all to install, and the machines
                        // whose launcher is stale are exactly the machines with something else wrong.
                        // A stranded launcher update is the failure this whole change exists to end,
                        // so it does not share a failure path with the network.
                        await RunLauncherUpdatePassAsync();
                    }

                    // Tool reconcile is governed by its OWN switch (tools.autoUpdate.enabled), independent
                    // of the Director self-update switch above (issue #827): where RefreshAsync only
                    // version-refreshes EXISTING tools, the reconcile also installs-missing, purges-drift,
                    // and repairs a broken install. Gated + logged inside the helper; never throws. Safe
                    // under multiple Directors via the engine's machine-wide mutex (no new lock here).
                    await CcDirector.Setup.Engine.ToolAutoUpdateTrigger.RunIfEnabledAsync(layout, "periodic");

                    // A release whose downloads have not been attached yet is worth another look in
                    // MINUTES, not in an hour (issue #1079) - but a BOUNDED number of times. Publishing
                    // makes a release "latest" the instant the tag is pushed and its files arrive about
                    // five and a half minutes later, so a machine checking inside that window has found a
                    // real update it cannot yet fetch. A release whose assets never arrive at all is
                    // permanently in that state, though, and an unbounded short poll against one would
                    // hammer GitHub for as long as this Director runs. See ReleaseNotReadyRetry for the
                    // policy and why it is bounded; every other outcome uses the configured cadence.
                    var ordinary = cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1);
                    // A cycle with auto-update switched off did not check anything, so it has no outcome
                    // to pace from - and must not go on pacing from the last one it had, which would spin
                    // this loop every few minutes doing nothing at all.
                    if (!cfg.Enabled) releaseNotReadyRetry.Reset();
                    var delay = cfg.Enabled ? releaseNotReadyRetry.NextDelay(outcome, ordinary) : ordinary;
                    if (delay != ordinary)
                        FileLog.Write($"[App] the latest release has no downloads attached yet; looking again in "
                                      + $"{delay.TotalMinutes:0} minutes (attempt {releaseNotReadyRetry.Consecutive} of "
                                      + $"{ReleaseNotReadyRetry.MaxConsecutive}) rather than waiting for the next full cycle.");
                    await Task.Delay(delay);
                }
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] StartUpdateService FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Kick off the startup tool reconcile in the background (issue #827). Runs off the UI thread and
    /// fire-and-forget so it NEVER gates or delays boot - it follows the updater's "failures only log,
    /// never block startup" discipline. The reconcile is gated by <c>tools.autoUpdate.enabled</c> inside
    /// <see cref="CcDirector.Setup.Engine.ToolAutoUpdateTrigger"/>, so when that flag is false nothing
    /// runs. When a Director self-update was just applied on this boot
    /// (<paramref name="selfUpdateApplied"/> - the version bump is the strongest signal the bundled
    /// tools manifest changed) the single boot-time reconcile is labeled "post-self-update" so the
    /// forced-once-after-a-version-bump trigger is observable in the log; otherwise it is the ordinary
    /// "startup" reconcile. Either way exactly one reconcile runs at boot - never throws.
    /// </summary>
    private static void StartToolReconcile(bool selfUpdateApplied)
    {
        var trigger = selfUpdateApplied ? "post-self-update" : "startup";
        FileLog.Write($"[App] StartToolReconcile: scheduling background tool reconcile (trigger={trigger})");
        _ = Task.Run(async () =>
        {
            var layout = CcDirector.Setup.Engine.InstallLayout.Default();
            await CcDirector.Setup.Engine.ToolAutoUpdateTrigger.RunIfEnabledAsync(layout, trigger);
        });
    }

    /// <summary>
    /// Shut this Director down because something outside it asked - the lifecycle signal, or the
    /// Gateway tunnel's shutdown verb.
    ///
    /// A programmatic lifetime.Shutdown() does NOT raise ShutdownRequested, so the shutdown routine
    /// (kill sessions, Gateway farewell, stop hosts, delete the crash journal) must run EXPLICITLY here
    /// first - otherwise an externally requested stop exits without any of it and is indistinguishable
    /// from a crash (issue #2194 found this: no session removals, no farewell, crash journal left
    /// behind). Off the UI thread, exactly like the ShutdownRequested path; the guard inside OnShutdown
    /// makes a later ShutdownRequested double-fire a no-op.
    /// </summary>
    internal async Task RequestShutdownAsync()
    {
        await Task.Run(() => OnShutdown(msg => FileLog.Write($"[CcDirector] {msg}")));
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown();
        });
    }

    /// <summary>
    /// Begin answering the two lifecycle requests that must work when nothing else does: "shut down"
    /// and "check for updates now".
    ///
    /// WHY THESE ARE SIGNALS AND NOT ROUTES. Both used to be posts to this Director's own web
    /// interface, which meant the launcher could only stop it - and therefore only update it - while a
    /// loopback socket was accepting connections and a credential file could be read. Every other thing
    /// an agent asks for goes through the Gateway on purpose, so there is one door; lifecycle cannot,
    /// because its whole job is to work when the Gateway is unreachable and to make this process exit
    /// so its executable can be replaced. A named signal delivered by the operating system needs no
    /// port, no address, no token, and no service to be up.
    ///
    /// THE NAME IS KEYED TO THIS DIRECTOR'S IDENTIFIER, which is the only string that names one
    /// process. This machine routinely runs several Directors - named instances and development slots -
    /// and a shutdown request that could reach any of them would eventually reach the wrong one.
    ///
    /// Started before the Control API rather than after it: a Director whose web interface fails to
    /// bind is exactly the Director somebody needs to be able to stop.
    /// </summary>
    private void StartLifecycleSignals(Action<string> log)
    {
        try
        {
            var directorId = DirectorIdStore.LoadOrCreate();

            _shutdownSignal = CcDirector.Core.Lifecycle.LifecycleSignal.Listen(
                CcDirector.Core.Lifecycle.LifecycleSignalNames.DirectorShutdown(directorId),
                () =>
                {
                    FileLog.Write("[CcDirector] shutdown requested by lifecycle signal");
                    RequestShutdownAsync().GetAwaiter().GetResult();
                });

            _updateCheckSignal = CcDirector.Core.Lifecycle.LifecycleSignal.Listen(
                CcDirector.Core.Lifecycle.LifecycleSignalNames.DirectorUpdateCheck(directorId),
                () =>
                {
                    FileLog.Write("[CcDirector] update check requested by lifecycle signal");
                    var outcome = CcDirector.Core.Update.UpdateStatusBoard.CheckNowAsync()
                        .GetAwaiter().GetResult();
                    FileLog.Write($"[CcDirector] on-demand update check concluded: "
                                  + $"{outcome?.ToString() ?? "the updater has not started yet"}");
                    // "Check for updates now" covers the machine's launcher too, not only this
                    // Director. A machine whose launcher is too old to be commanded is exactly the
                    // machine somebody is standing at when they ask for this, and the periodic loop
                    // does not run at all on a development build.
                    RunLauncherUpdatePassAsync().GetAwaiter().GetResult();
                });

            log($"Lifecycle signals listening for directorId={directorId}");
        }
        catch (Exception ex)
        {
            // Loud, and NOT fatal. A Director that refused to start because it could not be remotely
            // stopped would be a Director nobody could start either.
            log($"Lifecycle signals FAILED to start: {ex.Message}. This Director cannot be stopped or "
                + "asked to check for updates from outside itself; closing its window still works.");
        }
    }

    /// <summary>
    /// One pass of the Director's ownership of the LAUNCHER's update (issue #2719): install a staged
    /// cc-launcher build over the installed one, and confirm the new launcher is alive AND commandable
    /// before believing it.
    ///
    /// THE ROOT IS THE SHARED ONE. This Director's own storage is redirected to its instance home, so
    /// the layout must be built from <see cref="InstanceContext.SharedRoot"/>. Resolved the ordinary
    /// way it would look for a staged launcher under <c>instances/&lt;slug&gt;/state</c> - a directory
    /// no installer has ever written - and report "nothing staged" for ever, on every machine.
    ///
    /// Never throws: this is called from a background loop and from a signal handler.
    /// </summary>
    private static async Task RunLauncherUpdatePassAsync()
    {
        try
        {
            var directorId = DirectorIdStore.LoadOrCreate();
            var owner = new CcDirector.Setup.Engine.LauncherUpdateOwner(
                InstanceContext.SharedRoot,
                directorStillHoldsItsInstance: () => DirectorStillHoldsItsInstance(directorId));

            var result = await owner.RunOnceAsync();
            if (result.Decision != CcDirector.Setup.Engine.LauncherUpdateDecision.NothingStaged)
                FileLog.Write($"[App] launcher update pass: {result.Decision} - {result.Message}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[App] launcher update pass FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Is this Director still the live owner of its instance - the check that a launcher swap did not
    /// take it with it, or leave a second Director holding the same home? Read from the registration
    /// this process writes about itself, so a yes means THIS process id is still the registered owner
    /// and not merely that some Director is.
    /// </summary>
    private static bool DirectorStillHoldsItsInstance(string directorId)
    {
        var path = Path.Combine(InstanceRegistration.InstancesDirectory, $"{directorId}.json");
        if (!File.Exists(path)) return false;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("pid", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var pid))
                return pid == Environment.ProcessId;
        }
        return false;
    }

    private void StartControlApi(Action<string> log)
    {
        try
        {
            // Clean semver on the wire (gateway registration / status surfaces).
            var version = AppVersion.Semver;

            ControlApiHost = new ControlApiHost(SessionManager, version, RequestShutdownAsync,
                repositoryRegistry: RepositoryRegistry,
                // Repositories mission (#510 phase C): the monitor feeds the Gateway push and the
                // /fleet/repositories - /fleet/worktrees standalone fallback.
                repositoryMonitor: RepositoryMonitor);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ControlApiHost.StartAsync();
                    // No listener and no port: the Director accepts nothing inbound (the
                    // Remove-the-network-port mission). Everything reaches it through the Gateway,
                    // down the tunnel the host just started dialling.
                    log($"Director services started (directorId={ControlApiHost.DirectorId}; no inbound listener - fleet access is the outbound Gateway tunnel)");

                    // Open this Director's crash journal now that its id is known (issue #212 L5).
                    // Seed it immediately so even a session-less Director records its presence;
                    // PersistSessionState refreshes the roster on every change after that.
                    CrashJournal = new DirectorCrashJournal(
                        ControlApiHost.DirectorId, Environment.ProcessId,
                        Environment.MachineName, Environment.UserName, DateTimeOffset.UtcNow);
                    CrashJournal.Update(Array.Empty<DirectorCrashJournalSession>());

                }
                catch (Exception ex)
                {
                    // Loud in the log; the session-state services start first inside StartAsync, so
                    // the local badge keeps working even here.
                    log($"Director services failed to start: {ex.Message}");
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
            var dir = CcStorage.ToolLogs("director");
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

    /// <summary>0 until the shutdown routine has run. OnShutdown is reachable from TWO paths - the
    /// lifetime's ShutdownRequested event (user/OS close) and the programmatic path below, which runs
    /// it explicitly because a programmatic <c>lifetime.Shutdown()</c> does NOT raise
    /// ShutdownRequested (observed: POST /shutdown exited without killing sessions, without the
    /// Gateway farewell, and left the crash journal behind - a clean stop indistinguishable from a
    /// crash). First caller wins; the second is a no-op.</summary>
    private int _shutdownRoutineRan;

    internal void OnShutdown(Action<string> log)
    {
        if (Interlocked.CompareExchange(ref _shutdownRoutineRan, 1, 0) != 0)
        {
            log("OnShutdown: already ran (programmatic path); skipping");
            return;
        }
        try
        {
            // Issue #2194: the work-history farewell, FIRST - before the sessions are killed - so the
            // Gateway rules every session this shutdown takes with it "Director stopped" (a
            // per-session remove arriving during the kill keeps that first ruling). Time-boxed inside
            // the client; the extra wait here is a hard cap so shutdown can never hang on it.
            if (ControlApiHost != null)
            {
                try
                {
                    var farewellTask = Task.Run(() => ControlApiHost.NotifyDirectorStoppingAsync());
                    if (!farewellTask.Wait(TimeSpan.FromSeconds(3)))
                        log("Gateway farewell timed out after 3 seconds");
                }
                catch (Exception ex)
                {
                    log($"Gateway farewell error: {ex.Message}");
                }
            }

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

            // Clean shutdown: delete the crash journal so this exit is NOT seen as a crash
            // by the next Director's recovery scan (issue #212 L5).
            try { CrashJournal?.MarkClean(); }
            catch (Exception ex) { log($"CrashJournal MarkClean error: {ex.Message}"); }

            // Stop answering lifecycle requests LAST of the listeners: this runs on the shutdown path
            // the signal itself may have started, and on Windows the kernel destroys the named object
            // the moment this process ends anyway, so a later signal cannot be delivered to a Director
            // that is already leaving.
            try { _shutdownSignal?.Dispose(); _updateCheckSignal?.Dispose(); }
            catch (Exception ex) { log($"Lifecycle signal stop error: {ex.Message}"); }

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
                // Faster STOP: the fleet/remote stop graceful window (ms) before force-kill. A non-positive
                // value disables the fast path (falls back to the standard GracefulShutdownTimeoutSeconds).
                if (agentSection.TryGetProperty("FleetKillGraceMs", out var fkg) && fkg.TryGetInt32(out var fkgMs))
                    Options.FleetKillGraceMs = fkgMs;
            }

            // Fleet-message steward (messaging.steward): dedupe + per-source rate limit + broadcast throttle
            // on a session's outgoing /fleet/* messages. Default-on-generous; every threshold is tunable.
            if (doc.RootElement.TryGetProperty("Messaging", out var messagingSection)
                && messagingSection.TryGetProperty("Steward", out var stewardSection))
            {
                if (stewardSection.TryGetProperty("Enabled", out var mse) && (mse.ValueKind == System.Text.Json.JsonValueKind.True || mse.ValueKind == System.Text.Json.JsonValueKind.False))
                    Options.MessageSteward.Enabled = mse.GetBoolean();
                if (stewardSection.TryGetProperty("DedupeWindowMs", out var mdw) && mdw.TryGetInt32(out var mdwMs))
                    Options.MessageSteward.DedupeWindowMs = mdwMs;
                if (stewardSection.TryGetProperty("PerSourcePerMin", out var mps) && mps.TryGetInt32(out var mpsN))
                    Options.MessageSteward.PerSourcePerMin = mpsN;
                if (stewardSection.TryGetProperty("BroadcastsPerMin", out var mbp) && mbp.TryGetInt32(out var mbpN))
                    Options.MessageSteward.BroadcastsPerMin = mbpN;
            }

            if (doc.RootElement.TryGetProperty("Voice", out var voiceSection))
            {
                // Issue #839: the standalone config.json Voice.OpenAiKey copy is removed - the key
                // vault is the single transcription key store. Only the non-secret voice/TTS settings
                // remain in config.
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
