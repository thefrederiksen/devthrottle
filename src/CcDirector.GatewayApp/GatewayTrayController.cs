using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CcDirector.Core.Network;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.Gateway.Cockpit;
using CcDirector.HostedAgent;
using CcDirector.Setup.Engine;
using CcDirector.TrayUi;

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
    private TrayFlyoutController? _flyout;
    private Bitmap? _icon;
    private string _statusText = "Gateway stopped";
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

    /// <summary>
    /// The Gateway's in-process brain (issue #184): a warm claude.exe this process hosts
    /// itself - no Director dependency. Owned by the <see cref="GatewayHost"/> (the brief
    /// agent drives it, issue #185); null while the host is stopped/starting. Dormant until
    /// first use (a turn brief, or a RESTART BRAIN click in Settings).
    /// </summary>
    public BrainSupervisor? Brain => _host?.Brain;

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
        // Modern interaction (consistent with the Launcher): LEFT-CLICK opens the OneDrive-style
        // flyout with live status + action buttons + the Start-on-login toggle. The detailed status
        // window is still one click away ("Open Settings" in the flyout). Right-click is reduced to a
        // single Quit escape hatch.
        _icon = new Bitmap(AssetLoader.Open(new Uri("avares://devthrottle-gateway/Assets/icon.png")));
        _flyout = new TrayFlyoutController(BuildFlyoutModel);

        var menu = new NativeMenu();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();
        menu.Add(quit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://devthrottle-gateway/Assets/tray.ico"))),
            ToolTipText = "DevThrottle Gateway",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => _flyout?.Toggle();

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);
        FileLog.Write("[GatewayTrayController] Tray icon created");
    }

    /// <summary>Build the flyout's content from current state (called fresh on each open).</summary>
    private TrayFlyoutModel BuildFlyoutModel()
    {
        var directors = _host?.Registry.ListDirectors().Count ?? 0;
        var up = DateTime.UtcNow - StartedAtUtc;
        var uptime = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes:D2}m" : $"{up.Minutes}m {up.Seconds:D2}s";

        var rows = new List<StatusRow>
        {
            new("Version", AppVersion.Full),
            new("Directors", directors.ToString()),
            new("Mode", GatewayAppOptions.Managed ? "managed" : "dev"),
            new("Uptime", uptime),
        };

        // ONE URL (docs/plans/one-url-cockpit.md): Open Cockpit is the primary action.
        var actions = new List<FlyoutAction>
        {
            new() { Text = "Open Cockpit", Primary = true, OnClick = OpenCockpit },
            new() { Text = "Open Settings", OnClick = OpenSettings },
            new() { Text = "Restart Gateway", OnClick = () => _ = RestartAsync() },
            new() { Text = "Open Logs Folder", OnClick = OpenLogsFolder },
        };

        ToggleSpec? toggle = OperatingSystem.IsWindows()
            ? new ToggleSpec { Label = "Start on login", IsOn = GatewayAutostart.IsRegistered(), OnChanged = SetAutostart }
            : null;

        return new TrayFlyoutModel
        {
            AppName = "DevThrottle Gateway",
            Icon = _icon,
            StatusTitle = _statusText,
            Status = _state switch
            {
                HostState.Running => StatusLevel.Ok,
                HostState.Starting => StatusLevel.Warn,
                HostState.Stopped => StatusLevel.Warn,
                _ => StatusLevel.Error,
            },
            Accent = Color.Parse("#007ACC"), // gateway blue (matches its existing UI)
            Rows = rows,
            Actions = actions,
            Toggle = toggle,
            OnQuit = () => _ = QuitAsync(),
        };
    }

    /// <summary>Enable/disable the Start-on-login autostart from the flyout toggle.</summary>
    private void SetAutostart(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (enable)
            {
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Could not resolve own exe path");
                GatewayAutostart.EnsureRegistered(exe, GatewayAppOptions.AutostartArguments());
                FileLog.Write("[GatewayTrayController] Autostart enabled by user");
            }
            else
            {
                GatewayAutostart.Unregister();
                FileLog.Write("[GatewayTrayController] Autostart disabled by user");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] SetAutostart FAILED: {ex.Message}");
        }
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
            // Back the Cockpit Settings page with the tray-owned bits (run mode + autostart Run-key):
            // the host hosts the REST surface, but only the tray process knows its mode and owns the
            // per-user autostart entry (which needs THIS exe path + the managed-launch arguments).
            host.SettingsHooks = new CcDirector.Gateway.Api.GatewaySettingsHooks
            {
                Mode = () => GatewayAppOptions.Managed ? "managed" : "dev",
                AutostartEnabled = () =>
                    OperatingSystem.IsWindows() ? GatewayAutostart.IsRegistered() : (bool?)null,
                SetAutostart = enable =>
                {
                    if (!OperatingSystem.IsWindows()) return false;
                    if (enable)
                    {
                        var exe = Environment.ProcessPath
                                  ?? throw new InvalidOperationException("Could not resolve own exe path");
                        GatewayAutostart.EnsureRegistered(exe, GatewayAppOptions.AutostartArguments());
                        return true;
                    }
                    GatewayAutostart.Unregister();
                    return false;
                },
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
                                     $"DevThrottle Gateway - another instance is already serving :{_port}"),
            PortProbe.OtherListener => ($"Port {_port} in use by another app",
                                        $"DevThrottle Gateway - port {_port} is occupied by another app"),
            _ => ("Gateway FAILED - see logs", "DevThrottle Gateway - failed to start"),
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
        await StopHostAsync(); // also gracefully stops the host-owned brain
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _flyout?.Close();
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
            HostState.Starting => ("Gateway starting...", "DevThrottle Gateway - starting"),
            HostState.Running => ($"Gateway running on :{_port}", $"DevThrottle Gateway - running on :{_port}"),
            HostState.Stopped => ("Gateway stopped", "DevThrottle Gateway - stopped"),
            HostState.Failed => ("Gateway FAILED - see logs", "DevThrottle Gateway - failed to start"),
            _ => ("Gateway", "DevThrottle Gateway"),
        };
        ApplyStatus(status, tip);
    }

    private void ApplyStatus(string status, string tip)
    {
        _statusText = status; // the flyout reads this live on open
        Dispatcher.UIThread.Post(() =>
        {
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
        try { _host?.StopAsync().GetAwaiter().GetResult(); } // also gracefully stops the host-owned brain
        catch (Exception ex) { FileLog.Write($"[GatewayTrayController] Dispose stop error: {ex.Message}"); }
        _host = null;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        _lifetime.Dispose();
    }
}
