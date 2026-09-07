using System.Diagnostics;
using Avalonia;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

public static class Program
{
    // Session-scoped (not Global\) mutex: one launcher per logged-in user session PER STORAGE ROOT.
    // A second launch (e.g. autostart racing a manual start) sees the mutex held and exits. Keyed to
    // the storage root this launcher serves - the same scoping as the lifecycle signals - so a test
    // rig running against its own redirected root and the installed launcher can coexist, which the
    // old port-keyed name only achieved by accident of both fighting over one number.
    private static string SingleInstanceMutexName =>
        $"CcDirector.Launcher.SingleInstance.{CcDirector.Core.Lifecycle.LifecycleSignalNames.RootKey()}";

    [STAThread]
    public static int Main(string[] args)
    {
        FileLog.Start();

        // Detached self-update helper mode: this process is a STAGED copy of the new Launcher exe.
        // It asks the running tray app to exit (the shutdown lifecycle signal), swaps itself into the
        // installed location, relaunches, and verifies the new build is healthy - rolling back to the
        // .old build (and pinning the bad version) if not. NEVER the normal startup path: it exits
        // when done. Launched by LauncherUpdater.LaunchDetachedUpdater.
        if (Array.IndexOf(args, "--apply-update") >= 0)
            return ApplyUpdate(args);

        LauncherAppOptions.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FileLog.Write("[Program] CC Launcher already running in this session; exiting second instance.");
            FileLog.Stop();
            return 0;
        }

        FileLog.Write($"[Program] CC Launcher starting, log: {FileLog.CurrentLogPath}");

        // TRAY MODE MUST ANSWER SIGTERM TOO.
        //
        // Headless mode has handled it from the start (see RunHeadless), and tray mode never did -
        // so the launcher a person actually runs ignored the one signal every tool uses to ask a
        // process to stop. The orphan investigated on 2026-07-29 was a tray-mode instance: it
        // ignored SIGTERM and had to be killed. launchd bootout sends SIGTERM, our own uninstaller
        // asks politely first, and an operator at a terminal reaches for kill before kill -9; a
        // process that ignores all three is unmanageable by design rather than by accident.
        //
        // Shutting the classic desktop lifetime down is what lets the normal exit path run, so the
        // Gateway is unregistered and the log is closed instead of the process simply vanishing.
        using var sigtermTray = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            ctx =>
            {
                ctx.Cancel = true;
                FileLog.Write("[Program] SIGTERM received - shutting the tray launcher down");
                RequestTrayShutdown();
            });

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (!App.FrameworkInitialized)
        {
            // The user-interface platform itself could not initialize - on macOS this happens
            // when the launcher starts while the screen is locked (the window server refuses a
            // render timer). An unattended supervisor must not die because an icon cannot be
            // drawn: fall back to HEADLESS mode - Gateway registration and the command stream
            // run; only the tray icon is missing. The registration file records
            // userInterface=degraded so the difference is visible. This fallback is ONLY for
            // platform initialization failures (before any App code ran); a fault in the
            // running app still exits loudly below.
            FileLog.Write($"[Program] User-interface platform failed to initialize: {ex.Message}");
            FileLog.Write("[Program] DEGRADED: running HEADLESS (no tray icon) - Gateway stream + registration only");
            return RunHeadless();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] FATAL: {ex}");
            return 1;
        }
        finally
        {
            _shutdownCompleted = true;
            FileLog.Write("[Program] CC Launcher exited");
            FileLog.Stop();
        }
    }

    /// <summary>
    /// Ask the running tray application to shut down, from a signal handler. Marshalled onto the
    /// user-interface thread because that is the only thread allowed to stop the lifetime; if the
    /// application is not up yet (a signal during startup) the process exits directly, which is the
    /// honest thing to do rather than ignoring the signal.
    /// </summary>
    /// <summary>Set when the normal exit path has finished, so the SIGTERM watchdog stands down instead
    /// of force-exiting a shutdown that is working.</summary>
    private static volatile bool _shutdownCompleted;

    private static void RequestTrayShutdown()
    {
        // Deliberately NOT a blocking call on the user-interface thread. Asking the desktop lifetime to
        // shut down runs the tray controller's disposal, which waits synchronously on an asynchronous
        // stop - so posting Shutdown to that thread and waiting could wedge the process precisely where
        // this handler is supposed to make it stoppable. Signal handlers must not be able to hang.
        //
        // So: ask nicely on a background thread, give it a bounded moment, then exit regardless. A
        // launcher that will not stop cleanly still stops.
        _ = Task.Run(() =>
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => lifetime.Shutdown());
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Program] SIGTERM: could not ask the lifetime to shut down: {ex.Message}");
            }
        });

        // The watchdog exists so a wedged shutdown cannot ignore the signal for ever - but it must not
        // cut a clean shutdown short either. The downstream steps have their own bounds (a Gateway
        // unregister allows five seconds, the host stop two, on top of an unbounded stream stop), so the
        // budget is generous and the exit is cancelled the moment shutdown actually finishes.
        _ = Task.Run(async () =>
        {
            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(25); waited += TimeSpan.FromMilliseconds(250))
            {
                if (_shutdownCompleted) return;
                await Task.Delay(250);
            }
            FileLog.Write("[Program] SIGTERM: shutdown did not complete in 25s - exiting anyway");
            FileLog.Stop();
            Environment.Exit(0);
        });
    }

    /// <summary>
    /// Headless degraded mode: everything except the tray icon. Runs until the shutdown
    /// lifecycle signal is raised or the process receives SIGTERM/SIGINT (launchd
    /// bootout sends SIGTERM; the graceful stop unregisters from the Gateway).
    /// </summary>
    private static int RunHeadless()
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task RequestShutdown() { shutdown.TrySetResult(); return Task.CompletedTask; }

        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; FileLog.Write("[Program] SIGTERM received"); shutdown.TrySetResult(); });
        using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT,
            ctx => { ctx.Cancel = true; FileLog.Write("[Program] SIGINT received"); shutdown.TrySetResult(); });

        using var lifetime = new CancellationTokenSource();
        var core = new LauncherCore(LauncherCore.ReadVersion());
        try
        {
            core.StartAsync(RequestShutdown, userInterfaceState: "degraded").GetAwaiter().GetResult();
            LauncherCore.RegisterAutostartSafe();
            if (LauncherAppOptions.Managed)
                _ = LauncherCore.RunUpdateLoopAsync(lifetime.Token);

            FileLog.Write("[Program] Headless launcher running");
            shutdown.Task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] Headless FATAL: {ex}");
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            core.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static int ApplyUpdate(string[] args)
    {
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var target = Arg("--target");
        var version = Arg("--new-version");
        // Relaunch arguments: the installed Launcher always relaunches managed; the self-update
        // test harness overrides this to keep its throwaway instance off the real install.
        var relaunchArgs = Arg("--args");
        if (relaunchArgs.Length == 0) relaunchArgs = LauncherTrayInstaller.InstalledArguments;
        var stagedSelf = Environment.ProcessPath ?? "";
        FileLog.Write($"[Program] --apply-update: version={version}, target={target}, args={relaunchArgs}, staged={stagedSelf}");

        // The identity the health check verifies: the pid of whichever launcher THIS HELPER started
        // most recently. Updated by startLauncher on the new build AND again on a rollback relaunch,
        // so both waits certify the process they actually started - never whatever else wrote the file.
        var relaunchedPid = 0;

        // THE THIRD SWAP ROUTE, AND IT TAKES THE SAME MACHINE-WIDE LOCK (issue #2719).
        //
        // This helper replaces the launcher binary, and since Phase 0 the DIRECTOR also replaces that
        // same binary through LauncherUpdateOwner. Two swaps overlapping on one target is not a near
        // miss: both write the same target, the same .new and the same .old, so the second swap can
        // delete the FIRST swap's real backup and leave a copy of the new build in its place. If that
        // build then fails its health check, the rollback faithfully "restores" the bad build - and
        // the machine ends up on it with no way back.
        //
        // It WAITS for the lock rather than skipping, unlike the two periodic owners. This process was
        // started for the single purpose of performing one swap and has no next cycle to return on;
        // skipping would silently drop the update it exists to apply.
        var result = BinarySwapLock.RunExclusivelyAsync<SelfUpdateResult>(
            work: () => new LauncherSelfUpdate().ApplyAsync(
            target, stagedSelf, version,
            stopLauncher: () =>
            {
                // A named lifecycle signal, not a post to the launcher's own web interface.
                //
                // The route this replaced was behind the old web host's token gate, and issue #1609 is what
                // that cost: sent token-less it was answered 401, the launcher never exited, its
                // executable never unlocked, and the self-update aborted with the misleading "exe still
                // locked after stop". The signal removes the whole class of failure - there is no
                // credential to omit, no port to have moved, and no socket that has to be accepting
                // connections at the moment a swap needs the process to leave.
                //
                // False here means nothing was listening, which is the same answer the refusal path
                // gave and is handled the same way: the swap aborts rather than writing over a locked
                // executable. The wait for the executable to become writable is still the barrier and
                // still cannot substitute for the process actually exiting.
                var signal = CcDirector.Core.Lifecycle.LifecycleSignalNames.LauncherShutdown();
                var asked = CcDirector.Core.Lifecycle.LifecycleSignal.Raise(signal);
                FileLog.Write($"[Program] asked the Launcher to quit via {signal}: delivered={asked}");
                if (!asked)
                    FileLog.Write("[Program] nothing is listening for the launcher shutdown signal; the launcher is "
                                  + "not running, or is a build older than this one. The swap will abort rather than "
                                  + "write over a locked executable.");
                return asked;
            },
            startLauncher: () =>
            {
                try
                {
                    // UseShellExecute=true so the relaunched Launcher does NOT inherit this helper's
                    // stdio handles - an inherited stdout pipe keeps the caller's pipe open for the
                    // Launcher's whole lifetime (observed as a hang in any script that pipes output).
                    var psi = new ProcessStartInfo
                    {
                        FileName = target,
                        Arguments = relaunchArgs,
                        WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
                        UseShellExecute = true,
                    };
                    using var proc = Process.Start(psi);
                    relaunchedPid = proc?.Id ?? 0;
                    FileLog.Write($"[Program] relaunched Launcher pid={proc?.Id}");
                    return proc is not null;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Program] relaunch FAILED: {ex.Message}");
                    return false;
                }
            },
            isHealthy: ct =>
            {
                // The registration file the RUNNING launcher writes, not a socket: the launcher
                // listens on nothing. Healthy means the file names THE PROCESS THIS HELPER STARTED
                // and that process is alive - the same liveness-is-not-identity rule the installer's
                // readiness wait follows. Pinning the pid (rather than the version) also keeps the
                // rollback wait honest: after a rollback the OLD build is the one relaunched, and a
                // version pin would call a healthy rollback dead.
                var fact = CcDirector.Core.Configuration.LauncherDiscovery.Read();
                var healthy = fact.Installed
                    && relaunchedPid != 0
                    && fact.Pid == relaunchedPid
                    && CcDirector.Core.Configuration.LauncherDiscovery.IsRunning(fact);
                return Task.FromResult(healthy);
            },
            healthTimeout: TimeSpan.FromSeconds(30)),
            whenBusy: why => new SelfUpdateResult(SelfUpdateOutcome.Failed,
                $"The launcher self-update did not run: {why}", Array.Empty<string>()),
            who: "launcher-self-update",
            waitFor: TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();

        FileLog.Write($"[Program] self-update outcome={result.Outcome}: {result.Message}");
        foreach (var step in result.Steps) FileLog.Write($"[Program]   {step}");
        FileLog.Stop();
        return result.Outcome == SelfUpdateOutcome.Updated ? 0 : 1;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The launcher is a tray-only app: on macOS it lives in the menu bar and must
            // not occupy the Dock or the application switcher.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
