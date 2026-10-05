using System.Net.Http;
using System.Net.Sockets;
using CcDirector.Core.Utilities;
using CcDirector.HostedAgent;
using CcDirector.Setup.Engine;

namespace CcDirector.Gateway;

/// <summary>The lifecycle state of the Gateway, as a service control would report it.</summary>
public enum GatewayServiceState { Starting, Running, Stopped, Failed }

/// <summary>
/// The few facts about the PROCESS that the service itself cannot know: which port to serve, whether it
/// is a managed install that self-updates, and what to write into the autostart Run key. A host supplies
/// these; everything else the service decides for itself.
/// </summary>
public sealed class GatewayServiceOptions
{
    /// <summary>The port to serve on.</summary>
    public int Port { get; init; } = GatewayHost.DefaultPort;

    /// <summary>Run the periodic self-update loop (managed installs only; never the dev console loop).</summary>
    public bool Managed { get; init; }

    /// <summary>Register the per-user autostart Run key at start.</summary>
    public bool RegisterAutostart { get; init; }

    /// <summary>The arguments to bake into the autostart Run key, or null for none.</summary>
    public string? AutostartArguments { get; init; }

    /// <summary>The run-mode string the Cockpit Settings page shows ("managed" / "dev").</summary>
    public string ModeLabel { get; init; } = "dev";
}

/// <summary>
/// The Gateway, as a service: it owns the <see cref="GatewayHost"/> lifecycle, the managed self-update
/// loop, autostart registration, the Cockpit's settings hooks, port-conflict diagnostics, and the issue
/// #880 shutdown watchdog. Start and stop are its only verbs, because they are the only verbs a Windows
/// service has.
///
/// WHY THIS CLASS EXISTS. All of the above used to live in GatewayTrayController - inside the tray app's
/// user interface, tangled up with a flyout. That, not the existence of screens, was what actually
/// blocked the Gateway from becoming a service: its lifecycle was a property of its window. Deleting the
/// screens did not fix it; moving the lifecycle here does.
///
/// It is deliberately HEADLESS: no Avalonia, no Dispatcher, no windowing type of any kind
/// (HeadlessGatewayGuardTests pins that). It reports state through <see cref="StateChanged"/> and asks to
/// be shut down through <see cref="ShutdownRequested"/>; how a host renders state, or ends the process,
/// is the host's business and not this class's.
///
/// Two hosts drive it, which is the point: the tray shim (CcDirector.GatewayApp), and the console host in
/// this project's Program.cs. They differ only in how they render and how they exit. When the Gateway
/// becomes a real Windows service, that is a third host - and nothing here changes.
/// </summary>
public sealed class GatewayService : IDisposable
{
    private enum PortProbe { Nothing, OurGateway, OtherListener }

    // Issue #880: how long a /shutdown-initiated graceful stop gets before the watchdog hard-exits the
    // process. Must stay well under the self-update helper's 20-second wait for the exe to unlock, so a
    // wedged stop can never strand an update on the old build.
    private static readonly TimeSpan ShutdownWatchdogGrace = TimeSpan.FromSeconds(10);

    private readonly GatewayServiceOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private GatewayHost? _host;
    private bool _disposed;

    public GatewayService(GatewayServiceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The port the Gateway serves on.</summary>
    public int Port => _options.Port;

    /// <summary>When this service was constructed.</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>The running host, or null while stopped/starting.</summary>
    public GatewayHost? Host => _host;

    /// <summary>
    /// The Gateway's in-process brain (issue #184): a warm claude.exe the host owns - no Director
    /// dependency. Null while the host is stopped/starting.
    /// </summary>
    public BrainSupervisor? Brain => _host?.Brain;

    /// <summary>The lifecycle state.</summary>
    public GatewayServiceState State { get; private set; } = GatewayServiceState.Stopped;

    /// <summary>
    /// A human-readable one-liner for the current state, including the diagnosed reason when a start
    /// failed (for example "Port 7900 in use by another app"). A host may show this verbatim.
    /// </summary>
    public string StatusText { get; private set; } = "Gateway stopped";

    /// <summary>Raised whenever <see cref="State"/> or <see cref="StatusText"/> changes. May fire on any thread.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised when the Gateway has stopped in response to a <c>/shutdown</c> request (the self-update
    /// helper) and the PROCESS should now end. The host decides how - Avalonia's Shutdown, or
    /// Environment.Exit. The issue #880 watchdog hard-exits regardless if the host does not, so a host
    /// that ignores this cannot strand a self-update on the old build.
    /// </summary>
    public event Action? ShutdownRequested;

    /// <summary>Start the Gateway. Safe to call again after a stop; a fresh host is built each time.</summary>
    public async Task StartAsync()
    {
        if (_host is not null)
        {
            FileLog.Write("[GatewayService] StartAsync: already running - ignoring");
            return;
        }

        RegisterAutostartSafe();
        SetState(GatewayServiceState.Starting, "Gateway starting...");

        try
        {
            // A fresh host each start: StopAsync disposes the registry and Tailscale provisioner, so a
            // restart needs a new instance rather than reusing a torn-down one.
            var host = new GatewayHost(_options.Port);
            host.OnShutdownRequested = OnHostShutdownRequested;
            host.SettingsHooks = BuildSettingsHooks();
            await host.StartAsync();
            _host = host;
            SetState(GatewayServiceState.Running, $"Gateway running on :{_options.Port}");
            FileLog.Write($"[GatewayService] running on :{_options.Port}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayService] StartAsync FAILED: {ex.Message}");
            await DiagnoseStartFailureAsync();
        }
    }

    /// <summary>Stop the Gateway. The service can be started again afterwards.</summary>
    public async Task StopAsync()
    {
        await StopHostAsync();
        SetState(GatewayServiceState.Stopped, "Gateway stopped");
    }

    /// <summary>
    /// Start the background work a long-running host wants: the managed self-update loop. Separate from
    /// <see cref="StartAsync"/> so a test (or a one-shot host) can run the Gateway without it.
    /// </summary>
    public void StartBackgroundWork()
    {
        if (_options.Managed)
            _ = RunUpdateLoopAsync(_lifetime.Token);
    }

    /// <summary>
    /// The self-update helper POSTs /shutdown to make this process exit so the exe unlocks and can be
    /// swapped.
    /// </summary>
    private void OnHostShutdownRequested()
    {
        FileLog.Write("[GatewayService] shutdown requested via /shutdown (self-update)");
        // Issue #880: /shutdown must ALWAYS end this process - the self-update swap waits (bounded) for
        // the exe to unlock, and a graceful stop wedged behind a stuck host or a frozen host thread would
        // strand it on the old build. The watchdog hard-exits after the grace period. Every store the
        // Gateway owns is written through on mutation except the repository history, which is saved on a
        // five-minute clock (Money Saver). A graceful stop saves it first thing, before the slow drains, so
        // this hard exit normally loses nothing; at worst it loses unsaved trend rows - daily maxima for a
        // chart, never sessions, keys or settings.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ShutdownWatchdogGrace);
            FileLog.Write($"[GatewayService] graceful stop did not finish within {ShutdownWatchdogGrace.TotalSeconds:0}s of /shutdown -> hard process exit (issue #880 watchdog)");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "shutdown-watchdog" };
        watchdog.Start();
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        FileLog.Write("[GatewayService] ShutdownAsync");
        _lifetime.Cancel();
        // Issue #880: the host stop (which also gracefully stops the host-owned brain) must never run
        // with a host's UI context captured, or a slow brain stop freezes that host and wedges the exit.
        // Task.Run gives the chain a context-free thread.
        await Task.Run(StopHostAsync);
        ShutdownRequested?.Invoke();
    }

    /// <summary>
    /// Back the Cockpit Settings page with the bits only this PROCESS knows: its run mode, and the
    /// per-user autostart Run key (which needs this exe's path and its launch arguments). These hooks are
    /// optional by design - GatewaySettingsHooks degrades to "unknown"/"unsupported" when they are null.
    /// </summary>
    private Api.GatewaySettingsHooks BuildSettingsHooks() => new()
    {
        Mode = () => _options.ModeLabel,
        AutostartEnabled = () => OperatingSystem.IsWindows() ? GatewayAutostart.IsRegistered()
            : OperatingSystem.IsMacOS() ? GatewayLaunchdAutostart.IsRegistered()
            : (bool?)null,
        SetAutostart = enable =>
        {
            // Windows and macOS have real per-user autostart; every other platform (the container)
            // reports unsupported rather than pretending, per GatewaySettingsHooks' contract.
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return false;
            if (enable)
            {
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Could not resolve own exe path");
                if (OperatingSystem.IsWindows())
                    GatewayAutostart.EnsureRegistered(exe, _options.AutostartArguments);
                else
                    GatewayLaunchdAutostart.EnsureRegistered(exe, _options.AutostartArguments);
                return true;
            }
            if (OperatingSystem.IsWindows())
                GatewayAutostart.Unregister();
            else
                GatewayLaunchdAutostart.Unregister();
            return false;
        },
    };

    private async Task StopHostAsync()
    {
        if (_host is null) return;
        try
        {
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayService] StopHostAsync error: {ex.Message}");
        }
        finally
        {
            _host = null;
        }
    }

    /// <summary>
    /// Periodic machine-tier auto-update (managed mode only): check for a newer Gateway and, if found,
    /// launch the detached self-update helper (it POSTs /shutdown -> swap -> relaunch -> health ->
    /// auto-rollback). The Cockpit picks up its own update on the relaunch. Failures only log.
    /// </summary>
    private static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();

        // Held across cycles: a release that is published but still uploading its assets is looked at
        // again in MINUTES rather than at the next hourly cycle - the same policy the launcher uses,
        // for the same reason (see ReleaseNotReadyRetry).
        var notReady = new ReleaseNotReadyRetry();

        // Let the gateway settle before the first check; never compete with startup.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = AutoUpdateConfig.Load(layout);
            TimeSpan? shortRetry = null;
            if (cfg.Enabled && OperatingSystem.IsWindows())
            {
                try
                {
                    var source = new ReleaseSource();
                    var release = await source.FetchLatestAsync(ct);
                    notReady.Reset();
                    var version = await new GatewayUpdater(layout).CheckStageAndLaunchAsync(release, source, ct);
                    if (version is not null)
                    {
                        FileLog.Write($"[GatewayService] launched Gateway self-update to {version}; this process will be asked to exit");
                        return; // the detached helper POSTs /shutdown, swaps, and relaunches us
                    }
                }
                // Published but incomplete is NOT a failure - the release is fine and this check was
                // early. Name it, and look again in minutes instead of at the next hourly cycle.
                catch (ReleaseNotReadyException ex)
                {
                    shortRetry = notReady.NextDelay();
                    FileLog.Write($"[GatewayService] {notReady.Describe(ex, shortRetry)}");
                }
                catch (Exception ex)
                {
                    notReady.Reset();
                    FileLog.Write($"[GatewayService] update check failed: {ex.Message}");
                }
            }
            try { await Task.Delay(shortRetry ?? (cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // A bare "FAILED" is a silent dead-end. The overwhelmingly common cause is the port already being
    // taken, so probe it and say what is actually there. The service stays alive either way, so Start
    // can retry.
    private async Task DiagnoseStartFailureAsync()
    {
        var probe = await ProbePortAsync();
        var status = probe switch
        {
            PortProbe.OurGateway => $"Another gateway already on :{_options.Port}",
            PortProbe.OtherListener => $"Port {_options.Port} in use by another app",
            _ => "Gateway FAILED - see logs",
        };
        FileLog.Write($"[GatewayService] DiagnoseStartFailure: probe={probe}, status=\"{status}\"");
        SetState(GatewayServiceState.Failed, status);
    }

    // Distinguish "our own gateway is already there" (a benign double-start) from "some other app holds
    // the port" (a real conflict) from "nothing listening" (the bind failed for another reason entirely).
    private async Task<PortProbe> ProbePortAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync($"http://127.0.0.1:{_options.Port}/healthz");
            var body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("\"status\":\"ok\"") || body.Contains("\"directors\""))
                return PortProbe.OurGateway;
            return PortProbe.OtherListener; // answered HTTP, but not our gateway shape
        }
        catch
        {
            // Not HTTP (or refused). A raw TCP connect tells us whether anything is listening at all.
            return await CanConnectAsync() ? PortProbe.OtherListener : PortProbe.Nothing;
        }
    }

    private async Task<bool> CanConnectAsync()
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", _options.Port);
            var done = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(1)));
            return done == connect && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void RegisterAutostartSafe()
    {
        if (!_options.RegisterAutostart)
        {
            FileLog.Write("[GatewayService] Autostart registration skipped (not requested)");
            return;
        }
        // Autostart is a per-user, per-platform mechanism: the Run key on Windows, a launchd user
        // launch agent on macOS. Linux has neither by design - the container runtime keeps the
        // hosted Gateway alive, so there is nothing to register and we say so rather than guess.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            FileLog.Write("[GatewayService] Autostart registration skipped (not Windows or macOS)");
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Could not resolve own exe path for autostart");
            if (OperatingSystem.IsWindows())
                GatewayAutostart.EnsureRegistered(exePath, _options.AutostartArguments);
            else
                GatewayLaunchdAutostart.EnsureRegistered(exePath, _options.AutostartArguments);
        }
        catch (Exception ex)
        {
            // Autostart is a convenience, not a hard dependency of running right now. Log truthfully and
            // keep running rather than failing the whole service.
            FileLog.Write($"[GatewayService] Autostart registration FAILED: {ex.Message}");
        }
    }

    private void SetState(GatewayServiceState state, string status)
    {
        State = state;
        StatusText = status;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // Synchronous best-effort stop on shutdown (also gracefully stops the host-owned brain).
        try { _host?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { FileLog.Write($"[GatewayService] Dispose stop error: {ex.Message}"); }
        _host = null;
        _lifetime.Dispose();
    }
}
