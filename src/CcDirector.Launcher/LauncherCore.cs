using System.Diagnostics;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// The launcher's non-visual core: the Gateway registration client and the persistent Gateway
/// command stream, built around one LaunchService and one DirectorSupervisor.
///
/// Remove-the-network-port mission, phase 6: there is NO web host any more. The launcher listens on
/// nothing. Everything the loopback REST interface served has a better home:
///   - commands (start/stop/restart the Director, launch, apps, files) ride DOWN the persistent
///     stream this core opens to the Gateway (<see cref="LauncherStreamClient"/>);
///   - lifecycle (quit, restart the Director) is a named signal (<see cref="StartLifecycleSignals"/>),
///     because it must work when no network does;
///   - health and identity are the registration file this process writes
///     (<see cref="LauncherDiscovery"/>), which is how the installer certifies an install and the
///     Director's update fold sees a live launcher - liveness is the pid in the file being alive,
///     never a socket answering.
///
/// Extracted from LauncherTrayController so the launcher can run in two modes:
///   - Tray mode (normal): LauncherTrayController owns a core and adds the menu-bar
///     icon and flyout on top.
///   - Headless mode (degraded): on an unattended machine whose screen is locked, the
///     operating system refuses to start a user-interface session (Avalonia cannot
///     create its render timer). A supervisor that dies because an icon cannot be
///     drawn is useless, so Program falls back to running JUST this core - every
///     remote capability (registration, stream commands, self-update) stays alive;
///     only the menu-bar icon is missing.
/// </summary>
public sealed class LauncherCore : IAsyncDisposable
{
    private readonly string _version;

    private GatewayRegistrationClient? _gatewayClient;
    private LauncherStreamClient? _launcherStreamClient;
    private CcDirector.Core.Lifecycle.ILifecycleSignalListener? _shutdownSignal;
    private CcDirector.Core.Lifecycle.ILifecycleSignalListener? _restartDirectorSignal;
    private bool _stopped;

    public LauncherCore(string version)
    {
        _version = version ?? throw new ArgumentNullException(nameof(version));
    }

    public string Version => _version;

    /// <summary>
    /// Start the lifecycle signals, write the registration file, register with the Gateway, and join
    /// the persistent command stream. <paramref name="requestShutdownAsync"/> is invoked when the
    /// shutdown lifecycle signal is raised. <paramref name="userInterfaceState"/> is "tray" (normal)
    /// or "degraded" (headless fallback) and is recorded in the registration file.
    /// </summary>
    public async Task StartAsync(Func<Task> requestShutdownAsync, string userInterfaceState = "tray")
    {
        ArgumentNullException.ThrowIfNull(requestShutdownAsync);
        FileLog.Write($"[LauncherCore] StartAsync: version={_version}, userInterface={userInterfaceState}");

        var launchService = new LaunchService();
        var directorSupervisor = new DirectorSupervisor();

        // The two things asked of the launcher that must work with no network at all: quit, and
        // restart the Director (which is how a staged update gets installed). Started FIRST, before
        // anything network-facing, because a launcher that failed to start the rest of itself is
        // exactly the one somebody needs to quit.
        StartLifecycleSignals(requestShutdownAsync, directorSupervisor);

        // The registration this process writes about itself - the launcher's only local surface now,
        // and what the installer's readiness wait and the Director's update fold read. Written before
        // the Gateway is attempted: a launcher with no Gateway configured is still a healthy launcher.
        //
        // It carries the signals ARMED JUST ABOVE, which is why the arming happens first. That is the
        // fact that makes this launcher's command surface observable on a platform where nothing can
        // be asked directly - see LauncherDiscovery.Write.
        _registrationState = (userInterfaceState, written: true);
        LauncherDiscovery.Write(_version, userInterfaceState,
            AutostartChecked, AutostartRegistered, AutostartFailure,
            commandSignals: ArmedLifecycleSignals);

        // One instance of each query service for the Gateway command stream.
        var appCatalog = new AppCatalog();
        var fileSearch = new FileSearchService();

        // Issue #331: register with the Gateway (no-op when gateway not configured).
        var gwConfig = GatewayConfig.Load();
        _gatewayClient = new GatewayRegistrationClient(gwConfig, _version);
        _gatewayClient.Start();

        // launcher-persistent-join: JOIN the Gateway over a persistent SignalR stream so the Gateway
        // can push lifecycle commands DOWN the open connection. This is the ONLY way a command reaches
        // this launcher, so it is gated on nothing but a Gateway being configured. Runs alongside the
        // registration client (which carries presence metadata).
        _launcherStreamClient = new LauncherStreamClient(gwConfig, _version, directorSupervisor, launchService,
            appCatalog, fileSearch);
        _launcherStreamClient.Start();

        await Task.CompletedTask;
    }

    /// <summary>Stop the stream, unregister from the Gateway, and remove the registration file. Safe to call twice.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write("[LauncherCore] StopAsync");

        if (_launcherStreamClient is not null)
        {
            await _launcherStreamClient.StopAsync();
            _launcherStreamClient = null;
        }

        if (_gatewayClient is not null)
        {
            await _gatewayClient.StopAsync();
            _gatewayClient = null;
        }

        LauncherDiscovery.Delete();
        _registrationState = null;

        try
        {
            _shutdownSignal?.Dispose();
            _restartDirectorSignal?.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherCore] stopping the lifecycle signals failed: {ex.Message}");
        }
        _shutdownSignal = null;
        _restartDirectorSignal = null;
    }

    /// <summary>
    /// Answer the two lifecycle requests aimed at this launcher, with no network involved.
    ///
    /// Both are keyed to the storage root this launcher serves, not to a port and not to the product
    /// name, so a test rig running against its own root and the installed launcher never hear each
    /// other's requests - which a port-based interface could not promise, because a port is a single
    /// machine-wide number that whoever binds it first owns.
    ///
    /// The restart request is what installs a staged update on demand. It deliberately does the same
    /// thing the launcher's own update pass would have done later, rather than a shortcut around it:
    /// whatever replaces the executable has to outlive the process being replaced, so the launcher has
    /// to be the one doing it.
    /// </summary>
    private void StartLifecycleSignals(Func<Task> requestShutdownAsync, DirectorSupervisor directorSupervisor)
    {
        var armed = new List<string>();
        try
        {
            var shutdownName = CcDirector.Core.Lifecycle.LifecycleSignalNames.LauncherShutdown();
            _shutdownSignal = CcDirector.Core.Lifecycle.LifecycleSignal.Listen(
                shutdownName,
                () =>
                {
                    FileLog.Write("[LauncherCore] quit requested by lifecycle signal");
                    requestShutdownAsync().GetAwaiter().GetResult();
                });
            armed.Add(shutdownName);

            var restartName = CcDirector.Core.Lifecycle.LifecycleSignalNames.LauncherRestartDirector();
            _restartDirectorSignal = CcDirector.Core.Lifecycle.LifecycleSignal.Listen(
                restartName,
                () =>
                {
                    FileLog.Write("[LauncherCore] Director restart requested by lifecycle signal");
                    directorSupervisor.RestartAsync().GetAwaiter().GetResult();
                });
            armed.Add(restartName);

            FileLog.Write("[LauncherCore] lifecycle signals listening for root key "
                          + CcDirector.Core.Lifecycle.LifecycleSignalNames.RootKey());
        }
        catch (Exception ex)
        {
            // Loud, and NOT fatal - a launcher that refused to start because it could not be quit
            // remotely would be a launcher nobody could remove. What DID arm is still returned, so the
            // registration describes the half-working launcher this now is rather than claiming both.
            FileLog.Write($"[LauncherCore] lifecycle signals FAILED to start: {ex.Message}. This launcher cannot "
                          + "be quit or asked to restart the Director from outside itself.");
        }

        ArmedLifecycleSignals = armed;
    }

    /// <summary>
    /// The lifecycle signals this process actually ARMED, named exactly as it armed them. Empty until
    /// <see cref="StartLifecycleSignals"/> has run, and empty afterwards when arming failed.
    ///
    /// ONE FACT, TWO READERS, AND THEY MUST NOT BE TWO FACTS. The registration file publishes it so a
    /// Director can tell whether this launcher may be swapped
    /// (<see cref="CcDirector.Core.Configuration.LauncherDiscovery.Write"/>), and the Gateway
    /// declaration publishes it so the Cockpit can tell whether a restart may be sent
    /// (<see cref="LauncherDeclaredCapabilities"/>). Those are the same question asked by two callers,
    /// and answering it twice from two pieces of code is how they come to disagree.
    ///
    /// A NAME IS ADDED ONLY AFTER ITS LISTENER IS CONSTRUCTED, so this is evidence rather than an
    /// intention: a partial failure reports exactly what works, and every reader is entitled to treat
    /// an entry here as a listener that was armed.
    /// </summary>
    public static IReadOnlyList<string> ArmedLifecycleSignals { get; private set; } = [];

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Why this launcher is not registered to start at login, or null when it is (or was never
    /// asked to be). RECORDED IN THE REGISTRATION FILE: a launcher whose autostart registration
    /// failed must not look identical to one that is properly managed.
    ///
    /// This exists because of a Mac that could not install anything. The registration failed, the
    /// failure was caught, one line went to a log, and the launcher carried on reporting perfect
    /// health. Nothing else on the machine or in the fleet could tell that launcher from a managed
    /// one - so the installer certified it, the uninstaller could not stop it, and every later
    /// install collided with it. The state was invisible, which is what made it survive.
    /// </summary>
    public static string? AutostartFailure { get; private set; }

    /// <summary>
    /// Has the autostart state been decided yet? Until it has, "no failure" is not the same as
    /// "healthy" - the registration file is written BEFORE autostart is registered on some paths, so
    /// the file says null (undecided) rather than "ok" during that window.
    /// </summary>
    public static bool AutostartChecked { get; private set; }

    /// <summary>
    /// Is this launcher registered to start at login? Distinct from <see cref="AutostartFailure"/>:
    /// autostart turned OFF on purpose is not a failure, but it is also NOT registered - and reporting
    /// "ok" for it left the fleet unable to tell a managed launcher from an unmanaged one, which is the
    /// whole point of publishing this.
    /// </summary>
    public static bool AutostartRegistered { get; private set; }

    /// <summary>The running core's registration context, so an autostart change can rewrite the file
    /// with the same user-interface state it was first written with.</summary>
    private static (string UserInterfaceState, bool written)? _registrationState;

    /// <summary>
    /// Record the autostart state from somewhere other than startup - the tray's own enable/disable
    /// toggle - and REWRITE the registration file so the recorded state is the visible state. Without
    /// this the file kept reporting a startup-era failure after the user had fixed it, or healthy
    /// after a later toggle failed.
    /// </summary>
    public static void RecordAutostartState(string? failure, bool registered)
    {
        // Written in this order so a reader that sees Checked=true cannot then read a stale failure.
        AutostartFailure = failure;
        AutostartRegistered = registered;
        AutostartChecked = true;
        FileLog.Write($"[LauncherCore] autostart state recorded: registered={registered}, "
                      + $"failure={failure ?? "none"}");

        if (_registrationState is { } reg)
            LauncherDiscovery.Write(ReadVersion(), reg.UserInterfaceState,
                AutostartChecked, AutostartRegistered, AutostartFailure,
                commandSignals: ArmedLifecycleSignals);
    }

    /// <summary>
    /// Register the start-at-login autostart for the current executable (the Run key on
    /// Windows, the launchd launch agent on macOS), honoring --no-autostart.
    ///
    /// A failure does NOT take the launcher down - it is still useful without autostart - but it is
    /// recorded in <see cref="AutostartFailure"/> and in the registration file. Silence was the defect.
    /// </summary>
    public static void RegisterAutostartSafe()
    {
        if (!LauncherAppOptions.RegisterAutostart)
        {
            FileLog.Write("[LauncherCore] Autostart registration skipped (--no-autostart)");
            // Not a failure - it was not asked for - but NOT registered either.
            RecordAutostartState(failure: null, registered: false);
            return;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            RecordAutostartState(failure: null, registered: false);   // no mechanism on this platform
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Could not resolve own exe path for autostart");
            // EnsureRegistered returns FALSE for "already correct, nothing written" on both platforms, so
            // its return value cannot be read as success or failure. Ask the machine what the state IS.
            // Getting this wrong would have reported every normally-registered launcher as broken from
            // its second login onward - and on macOS from the FIRST run, because the installer now
            // registers the agent before the launcher starts.
            if (OperatingSystem.IsWindows())
                LauncherAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());
            else
                LauncherLaunchdAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());

            var registered = OperatingSystem.IsWindows()
                ? LauncherAutostart.IsRegistered()
                : LauncherLaunchdAutostart.IsRegistered();

            RecordAutostartState(
                registered
                    ? null
                    : (OperatingSystem.IsWindows()
                        ? "the autostart Run key is not present after registering"
                        : "the launch agent property list is not present after registering"),
                registered);
        }
        catch (Exception ex)
        {
            RecordAutostartState(ex.Message, registered: false);
            FileLog.Write($"[LauncherCore] Autostart registration FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Periodic machine-tier auto-update (managed mode only). Two separate jobs, in this order:
    ///
    ///   1. The LAUNCHER's own update. STAGED ON EVERY PLATFORM - download, verify, put it where the
    ///      Director looks. Applied HERE only on Windows, by launching the detached self-update helper
    ///      (it raises the shutdown lifecycle signal -> swap -> relaunch -> health -> auto-rollback),
    ///      because on Windows a locked executable needs something outside itself to replace it.
    ///      Elsewhere the staged build is left for the Director to install, which is the better job for
    ///      it: the Director is already running, is not the file being replaced, and can witness the
    ///      result.
    ///   2. The DIRECTOR's update, which the launcher now owns (issue #1033): if one is staged and the
    ///      Director has no sessions running, stop it, swap the build, start it, and confirm the new
    ///      version answers - rolling back if it does not. On BOTH platforms, because the reason the
    ///      Director cannot do this for itself has nothing to do with which operating system it is on.
    ///
    /// THE WHOLE OF JOB 1 USED TO SIT BEHIND AN IsWindows GATE, and that is the defect this shape
    /// replaces: a Mac never looked for a launcher release, so there was never anything staged, so the
    /// Director had nothing to install, so NO LAUNCHER ON A MAC EVER UPDATED ITSELF - it kept whatever
    /// the last installer run left behind while its Director updated every week. What is genuinely
    /// Windows-only is the detached helper, and now only that is.
    ///
    /// Both jobs are governed by the same auto-update switch the Director used to read, so a machine
    /// with auto-update turned off is still left alone. Failures only log.
    /// </summary>
    public static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();
        var directorUpdates = new DirectorUpdateOwner(new DirectorSupervisor());

        // Held across cycles: a release that is published but still uploading its assets is looked at
        // again in MINUTES rather than at the next hourly cycle. This is the condition that cost a
        // machine up to an hour of staleness for a window measured at 5m23s (v1.8.8).
        var notReady = new ReleaseNotReadyRetry();

        // Let the launcher settle before the first check; never compete with startup.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = AutoUpdateConfig.Load(layout);
            TimeSpan? shortRetry = null;
            if (cfg.Enabled)
            {
                try
                {
                    var source = new ReleaseSource();
                    var release = await source.FetchLatestAsync(ct);
                    notReady.Reset();

                    // STAGE ON EVERY PLATFORM; APPLY WHERE THIS PROCESS CAN. The download, the hash
                    // check and the copy are the same everywhere, and this whole block used to sit
                    // behind an IsWindows gate - so a Mac never even looked, and there was never
                    // anything for its Director to install. That is why no launcher on a Mac has ever
                    // updated itself.
                    var updater = new LauncherUpdater(layout);
                    if (OperatingSystem.IsWindows())
                    {
                        var version = await updater.CheckStageAndLaunchAsync(release, source, ct);
                        if (version is not null)
                        {
                            FileLog.Write($"[LauncherCore] launched Launcher self-update to {version}; this process will be asked to exit");
                            return; // the detached helper raises the shutdown signal, swaps, and relaunches us
                        }
                    }
                    else
                    {
                        // Staged and left. The Director installs it - it is already running, it is not
                        // the file being replaced, and it can witness the new build afterwards, which
                        // is more than this process could do for itself.
                        var staged = await updater.StageAsync(release, source, ct);
                        if (staged is not null)
                            FileLog.Write($"[LauncherCore] staged Launcher {staged.Value.Version} at "
                                          + $"{staged.Value.StagedPath}; the Director installs it when it can witness the result");
                    }
                }
                // Published but incomplete is NOT a failure, and it must not be logged as one: the
                // release is fine and this check was simply early. Say so, and look again in minutes.
                catch (ReleaseNotReadyException ex)
                {
                    shortRetry = notReady.NextDelay();
                    FileLog.Write($"[LauncherCore] {notReady.Describe(ex, shortRetry)}");
                }
                catch (Exception ex)
                {
                    notReady.Reset();
                    FileLog.Write($"[LauncherCore] update check failed: {ex.Message}");
                }
            }

            if (cfg.Enabled)
            {
                // RunOnceAsync never throws; the catch is here only so a future change to it cannot take
                // the whole loop down and leave the machine with no update path at all.
                try
                {
                    var decision = await directorUpdates.RunOnceAsync(ct);
                    if (decision != DirectorUpdateDecision.NothingStaged)
                        FileLog.Write($"[LauncherCore] Director update pass: {decision}");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[LauncherCore] Director update pass FAILED: {ex.Message}");
                }
            }

            try { await Task.Delay(shortRetry ?? (cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// The background scan of this machine's disks (Reclaim the Disk, phase 5), hosted here because
    /// there is one launcher for a machine and several Directors. Installed mode only, like the update
    /// loop beside it: a launcher started from a repository build or a test rig must never begin
    /// walking the whole disk. It scans and recommends and NEVER removes - see
    /// <see cref="BackgroundDiskScan"/>.
    /// </summary>
    public static async Task RunBackgroundDiskScanAsync(CancellationToken ct)
    {
        FileLog.Write("[LauncherCore] RunBackgroundDiskScanAsync: starting the background disk scan host");
        try
        {
            await BackgroundDiskScan.ForThisMachine().RunLoopAsync(ct);
        }
        catch (Exception ex)
        {
            // Started and not awaited, so nothing else would ever see this. A launcher that cannot
            // scan is still a launcher; it says so loudly and carries on.
            FileLog.Write($"[LauncherCore] RunBackgroundDiskScanAsync FAILED, no background scan will run: {ex}");
        }
    }

    /// <summary>The launcher's informational version from the executing assembly.</summary>
    public static string ReadVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var attr = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
            return attr?.InformationalVersion ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
