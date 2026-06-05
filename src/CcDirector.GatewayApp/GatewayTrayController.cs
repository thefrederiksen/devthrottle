using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.Gateway.Cockpit;
using CcDirector.Setup.Engine;

namespace CcDirector.GatewayApp;

/// <summary>
/// Owns the tray icon, the in-process <see cref="GatewayHost"/>, the Cockpit supervisor, and (in
/// managed mode) the periodic self-update check. This is the whole app: no main window, just a
/// notification-area icon whose menu drives the gateway. Closing nothing kills the process - only
/// the Quit menu item (or a /shutdown request from the self-update helper) shuts it down.
/// </summary>
public sealed class GatewayTrayController : IDisposable
{
    private enum HostState { Starting, Running, Stopped, Failed }
    private enum PortProbe { Nothing, OurGateway, OtherListener }

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private GatewayHost? _host;
    private CockpitSupervisor? _cockpit;
    private SettingsWindow? _settingsWindow;
    private HostState _state = HostState.Stopped;
    private bool _busy;
    private bool _disposed;

    public GatewayTrayController(IClassicDesktopStyleApplicationLifetime desktop, int port)
    {
        _desktop = desktop;
        _port = port;
    }

    /// <summary>The gateway's listen port.</summary>
    public int Port => _port;

    /// <summary>When the tray app started (for the Settings window's uptime display).</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>The running host, or null while stopped/starting. Settings reads registry counts off it.</summary>
    public GatewayHost? Host => _host;

    /// <summary>Human-readable host state for the Settings window ("Running", "Failed", ...).</summary>
    public string StateText => _state.ToString();

    /// <summary>Build the tray icon, register autostart, and start the gateway + Cockpit supervisor.</summary>
    public void Start()
    {
        FileLog.Write($"[GatewayTrayController] Start (managed={GatewayAppOptions.Managed})");

        BuildTrayIcon();
        RegisterAutostartSafe();

        SetState(HostState.Starting);
        _ = StartHostAsync();

        StartCockpitSupervisor();

        if (GatewayAppOptions.Managed)
            _ = RunUpdateLoopAsync(_lifetime.Token);

        if (GatewayAppOptions.OpenSettingsOnStart)
            OpenSettings();
    }

    private void BuildTrayIcon()
    {
        var menu = new NativeMenu();

        _statusItem = new NativeMenuItem("Gateway starting...") { IsEnabled = false };
        menu.Add(_statusItem);
        menu.Add(new NativeMenuItemSeparator());

        // ONE URL (docs/plans/one-url-cockpit.md): the Cockpit is served through the front
        // door, so there is exactly one thing to open.
        var openCockpit = new NativeMenuItem("Open Cockpit");
        openCockpit.Click += (_, _) => OpenCockpit();
        menu.Add(openCockpit);

        var settings = new NativeMenuItem("Settings...");
        settings.Click += (_, _) => OpenSettings();
        menu.Add(settings);

        var openLogs = new NativeMenuItem("Open Logs Folder");
        openLogs.Click += (_, _) => OpenLogsFolder();
        menu.Add(openLogs);

        var restart = new NativeMenuItem("Restart Gateway");
        restart.Click += (_, _) => _ = RestartAsync();
        menu.Add(restart);

        menu.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();
        menu.Add(quit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://cc-director-gateway/Assets/tray.ico"))),
            ToolTipText = "CC Director Gateway",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => OpenSettings(); // left-click = the status window

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);
        FileLog.Write("[GatewayTrayController] Tray icon created");
    }

    private async Task StartHostAsync()
    {
        try
        {
            // A fresh host each start: StopAsync disposes the registry and Tailscale
            // provisioner, so a restart needs a new instance rather than reusing a torn-down one.
            var host = new GatewayHost(_port);
            // The self-update helper POSTs /shutdown to make this process exit so the exe unlocks.
            host.OnShutdownRequested = () =>
            {
                FileLog.Write("[GatewayTrayController] shutdown requested via /shutdown (self-update)");
                _ = QuitAsync();
            };
            await host.StartAsync();
            _host = host;
            SetState(HostState.Running);
            FileLog.Write($"[GatewayTrayController] Gateway running on :{_port}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] StartHostAsync FAILED: {ex.Message}");
            await DiagnoseStartFailureAsync();
        }
    }

    /// <summary>
    /// Supervise the Cockpit web app. Managed mode (installed) supervises the canonical per-user
    /// Cockpit install; a dev launch stays inert unless CC_COCKPIT_MANAGED=1 is set explicitly
    /// (CockpitSupervisor.FromEnvironment), so a repo build never fights the installed Gateway
    /// for the Cockpit port.
    /// </summary>
    private void StartCockpitSupervisor()
    {
        _cockpit = GatewayAppOptions.Managed
            ? new CockpitSupervisor(
                enabled: true,
                exePath: Environment.GetEnvironmentVariable("CC_COCKPIT_EXE") is { Length: > 0 } exe
                    ? exe
                    : InstallLayout.Default().PathFor(ComponentRegistry.Cockpit),
                port: CockpitSupervisor.ResolvePort())
            : CockpitSupervisor.FromEnvironment();
        _cockpit.Start();
    }

    /// <summary>
    /// Periodic machine-tier auto-update (managed mode only): check for a newer Gateway and, if
    /// found, launch the detached self-update helper (it POSTs /shutdown -> swap -> relaunch ->
    /// health -> auto-rollback). The Cockpit picks up its own update on the relaunch. Failures only log.
    /// </summary>
    private static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();
        // Let the gateway settle before the first check; never compete with startup.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = AutoUpdateConfig.Load(layout);
            if (cfg.Enabled && OperatingSystem.IsWindows())
            {
                try
                {
                    var source = new ReleaseSource();
                    var release = await source.FetchLatestAsync(ct);
                    var version = await new GatewayUpdater(layout).CheckStageAndLaunchAsync(release, source, ct);
                    if (version is not null)
                    {
                        FileLog.Write($"[GatewayTrayController] launched Gateway self-update to {version}; this process will be asked to exit");
                        return; // the detached helper POSTs /shutdown, swaps, and relaunches us
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayTrayController] update check failed: {ex.Message}");
                }
            }
            try { await Task.Delay(cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // A bare "FAILED" on a tray icon Windows hides by default is a silent dead-end.
    // The overwhelmingly common cause is the port already being taken, so probe it and
    // say what is actually there. The app stays alive either way, so Restart can retry.
    private async Task DiagnoseStartFailureAsync()
    {
        var probe = await ProbePortAsync();
        var (status, tip) = probe switch
        {
            PortProbe.OurGateway => ($"Another gateway already on :{_port}",
                                     $"CC Director Gateway - another instance is already serving :{_port}"),
            PortProbe.OtherListener => ($"Port {_port} in use by another app",
                                        $"CC Director Gateway - port {_port} is occupied by another app"),
            _ => ("Gateway FAILED - see logs", "CC Director Gateway - failed to start"),
        };
        FileLog.Write($"[GatewayTrayController] DiagnoseStartFailure: probe={probe}, status=\"{status}\"");
        _state = HostState.Failed;
        ApplyStatus(status, tip);
    }

    // Distinguish "our own gateway is already there" (a benign double-start) from
    // "some other app holds the port" (a real conflict) from "nothing listening"
    // (the bind failed for another reason entirely).
    private async Task<PortProbe> ProbePortAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync($"http://127.0.0.1:{_port}/healthz");
            var body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("\"status\":\"ok\"") || body.Contains("\"directors\""))
                return PortProbe.OurGateway;
            return PortProbe.OtherListener; // answered HTTP, but not our gateway shape
        }
        catch
        {
            // Not HTTP (or refused). A raw TCP connect tells us whether anything is
            // listening at all.
            return await CanConnectAsync() ? PortProbe.OtherListener : PortProbe.Nothing;
        }
    }

    private async Task<bool> CanConnectAsync()
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", _port);
            var done = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(1)));
            return done == connect && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task StopHostAsync()
    {
        if (_host is null) return;
        try
        {
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] StopHostAsync error: {ex.Message}");
        }
        finally
        {
            _host = null;
        }
    }

    private async Task RestartAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            FileLog.Write("[GatewayTrayController] RestartAsync");
            SetState(HostState.Starting);
            await StopHostAsync();
            await StartHostAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task QuitAsync()
    {
        FileLog.Write("[GatewayTrayController] QuitAsync");
        _lifetime.Cancel();
        _cockpit?.Dispose();
        _cockpit = null;
        await StopHostAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _settingsWindow?.Close();
            if (_trayIcon is not null) _trayIcon.IsVisible = false;
            _desktop.Shutdown();
        });
    }

    private void OpenSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_settingsWindow is { } open)
            {
                open.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    private void OpenCockpit()
        // ONE URL: the Cockpit is served through the gateway front door
        // (https://<magicdns>/ -> :7878 -> fallback proxy -> loopback Cockpit).
        => OpenTailnetUrl(TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } d ? d + "/" : null, "Cockpit");

    // Open a Tailscale URL in the browser. There is NO localhost fallback by design: every
    // cc-director URL must be the tailnet URL so it works from any node, and a loopback URL
    // would only work on this machine. When Tailscale is unavailable the URL is null; we
    // refuse and say why rather than open a URL that is wrong everywhere but here.
    private void OpenTailnetUrl(string? url, string label)
    {
        // Resolving the front door shells the tailscale CLI, so do it off the UI thread.
        _ = Task.Run(() =>
        {
            if (url is null)
            {
                FileLog.Write($"[GatewayTrayController] Open{label} REFUSED: Tailscale unavailable on this machine, so there is no tailnet URL to open. Bring Tailscale up and retry; cc-director never opens a localhost URL.");
                return;
            }
            try
            {
                FileLog.Write($"[GatewayTrayController] Open{label}: {url}");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayTrayController] Open{label} FAILED: {ex.Message}");
            }
        });
    }

    private void OpenLogsFolder()
    {
        try
        {
            var logDir = Path.GetDirectoryName(FileLog.CurrentLogPath)!;
            Directory.CreateDirectory(logDir);
            FileLog.Write($"[GatewayTrayController] OpenLogsFolder: {logDir}");
            Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] OpenLogsFolder FAILED: {ex.Message}");
        }
    }

    private void RegisterAutostartSafe()
    {
        if (!GatewayAppOptions.RegisterAutostart)
        {
            FileLog.Write("[GatewayTrayController] Autostart registration skipped (--no-autostart)");
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Could not resolve own exe path for autostart");
            GatewayAutostart.EnsureRegistered(exePath, GatewayAppOptions.AutostartArguments());
        }
        catch (Exception ex)
        {
            // Autostart is a convenience, not a hard dependency of running right now.
            // Log truthfully and keep running rather than failing the whole app.
            FileLog.Write($"[GatewayTrayController] Autostart registration FAILED: {ex.Message}");
        }
    }

    private void SetState(HostState state)
    {
        _state = state;
        var (status, tip) = state switch
        {
            HostState.Starting => ("Gateway starting...", "CC Director Gateway - starting"),
            HostState.Running => ($"Gateway running on :{_port}", $"CC Director Gateway - running on :{_port}"),
            HostState.Stopped => ("Gateway stopped", "CC Director Gateway - stopped"),
            HostState.Failed => ("Gateway FAILED - see logs", "CC Director Gateway - failed to start"),
            _ => ("Gateway", "CC Director Gateway"),
        };
        ApplyStatus(status, tip);
    }

    private void ApplyStatus(string status, string tip)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_statusItem is not null) _statusItem.Header = status;
            if (_trayIcon is not null) _trayIcon.ToolTipText = tip;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // Synchronous best-effort stop on shutdown.
        try { _cockpit?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayTrayController] Dispose cockpit error: {ex.Message}"); }
        try { _host?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { FileLog.Write($"[GatewayTrayController] Dispose stop error: {ex.Message}"); }
        _host = null;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        _lifetime.Dispose();
    }
}
