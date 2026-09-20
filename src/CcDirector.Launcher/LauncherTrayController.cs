using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;
using CcDirector.TrayUi;

namespace CcDirector.Launcher;

/// <summary>
/// Owns the tray icon, the launcher core (Gateway stream + registration), the launch service, and
/// the Director supervisor. This is the whole launcher app: no main window, just a notification-area
/// icon whose menu drives everything. Only the Quit menu item (or the shutdown lifecycle signal)
/// shuts it down.
/// </summary>
public sealed class LauncherTrayController : IDisposable
{
    private enum HostState { Starting, Running, Failed }

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly CancellationTokenSource _lifetime = new();

    private TrayIcon? _trayIcon;
    private TrayFlyoutController? _flyout;
    private Bitmap? _icon;
    private LauncherCore? _core;
    private HostState _state = HostState.Starting;
    private bool _disposed;

    // The flyout's Details values plus the configured Gateway base URL: resolved once, off the UI
    // thread, right after Start (small local file reads that do not change within one run - the
    // Gateway registration also only reads its config at start), so the flyout open stays I/O-free
    // (the same discipline as the Gateway tray, issue #855).
    private const string Placeholder = "...";
    private volatile string _buildDateText = Placeholder;
    private volatile string _installRootText = Placeholder;
    private volatile string _installedText = Placeholder;
    private volatile string? _gatewayBaseUrl;

    public LauncherTrayController(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
    }

    /// <summary>Build the tray icon, register autostart, and start the core.</summary>
    public void Start()
    {
        FileLog.Write($"[LauncherTrayController] Start (managed={LauncherAppOptions.Managed})");

        BuildTrayIcon();
        LauncherCore.RegisterAutostartSafe();

        SetState(HostState.Starting);
        _ = StartHostAsync();

        // The flyout's Details values: read once, off the UI thread - they cannot change within
        // one run, and the flyout open path must never touch the disk.
        _ = Task.Run(ResolveAboutInfo);

        if (LauncherAppOptions.Managed)
            _ = LauncherCore.RunUpdateLoopAsync(_lifetime.Token);
    }

    /// <summary>
    /// Resolve the flyout's Details values (build date, install root, installed component
    /// versions) and the configured Gateway base URL: small local file reads that never change
    /// within one run of this process.
    /// </summary>
    private void ResolveAboutInfo()
    {
        try
        {
            _buildDateText = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm") ?? "(unknown)";
            _installRootText = AboutInfo.InstallRoot;
            var installed = AboutInfo.InstalledComponents();
            _installedText = installed.Count == 0
                ? "(no installed.json - dev build?)"
                : string.Join(", ", installed.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key} {kv.Value}"));

            var gateway = GatewayConfig.Load();
            _gatewayBaseUrl = gateway.IsEnabled ? gateway.Url.TrimEnd('/') : null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] ResolveAboutInfo FAILED: {ex.Message}");
        }
    }

    private void BuildTrayIcon()
    {
        // Modern interaction: LEFT-CLICK the icon opens the OneDrive-style flyout (live status +
        // action buttons + the Start-with-Windows toggle). The right-click menu is reduced to a
        // single Quit escape hatch - everything else lives in the flyout, not a legacy text menu.
        _icon = new Bitmap(AssetLoader.Open(new Uri("avares://cc-launcher/Assets/icon.png")));
        _flyout = new TrayFlyoutController(BuildFlyoutModel);
        // Create + first-layout the panel window off-screen NOW, so the first left-click is
        // instant instead of paying native window creation.
        _flyout.WarmUp();

        var menu = new NativeMenu();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();
        menu.Add(quit);

        // Windows notification areas want the multi-resolution .ico; macOS menu-bar status
        // items decode the .png (ico decoding is not guaranteed off Windows).
        var trayIconAsset = OperatingSystem.IsWindows()
            ? "avares://cc-launcher/Assets/tray.ico"
            : "avares://cc-launcher/Assets/icon.png";
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri(trayIconAsset))),
            ToolTipText = "Launcher",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => _flyout?.Toggle();

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);
        FileLog.Write("[LauncherTrayController] Tray icon created");
    }

    /// <summary>Build the flyout's content from current state (called fresh on each open).</summary>
    private TrayFlyoutModel BuildFlyoutModel()
    {
        var supervisor = new DirectorSupervisor();
        var directorState = supervisor.IsRunning ? "running"
            : supervisor.DirectorExeExists ? "stopped"
            : "not installed";

        var rows = new List<StatusRow>
        {
            new("Version", LauncherCore.ReadVersion().Split('+')[0]),
            new("Director", directorState),
        };

        var actions = new List<FlyoutAction>
        {
            new() { Text = "Restart Director", Primary = true, OnClick = () => _ = RestartDirectorAsync() },
        };
        // The Cockpit (and the one Settings page, which lives there) is served through the
        // configured Gateway, so these jumps only exist when this machine has a Gateway
        // configured - a dead button would be worse than no button.
        if (_gatewayBaseUrl is { } gw)
        {
            actions.Add(new() { Text = "Open Cockpit", OnClick = () => OpenUrl(gw + "/", "Cockpit") });
            actions.Add(new() { Text = "Settings", OnClick = () => OpenUrl(gw + "/settings", "CockpitSettings") });
        }

        // The quieter diagnostics (matching the Gateway flyout).
        var details = new List<StatusRow>
        {
            new("Gateway", _gatewayBaseUrl ?? "not configured"),
            new("Build date", _buildDateText),
            new("Install root", _installRootText),
            new("Installed", _installedText),
        };

        var footerLinks = new List<FlyoutAction>
        {
            new() { Text = "Logs", OnClick = OpenLogsFolder },
            new() { Text = "Config", OnClick = OpenConfigFolder },
        };

        ToggleSpec? toggle = OperatingSystem.IsWindows()
            ? new ToggleSpec { Label = "Start with Windows", IsOn = LauncherAutostart.IsRegistered(), OnChanged = SetAutostart }
            : OperatingSystem.IsMacOS()
                ? new ToggleSpec { Label = "Start at login", IsOn = LauncherLaunchdAutostart.IsRegistered(), OnChanged = SetAutostart }
                : null;

        return new TrayFlyoutModel
        {
            AppName = "Launcher",
            Icon = _icon,
            StatusTitle = _state switch
            {
                HostState.Running => "Running",
                HostState.Starting => "Starting...",
                _ => "Failed - see logs",
            },
            StatusPillText = _state.ToString(),
            Status = _state switch
            {
                HostState.Running => StatusLevel.Ok,
                HostState.Starting => StatusLevel.Warn,
                _ => StatusLevel.Error,
            },
            Accent = Color.Parse("#F2600C"), // launcher orange
            Rows = rows,
            DetailRows = details,
            Actions = actions,
            FooterLinks = footerLinks,
            Toggle = toggle,
            OnQuit = () => _ = QuitAsync(),
        };
    }

    private async Task StartHostAsync()
    {
        try
        {
            _core = new LauncherCore(LauncherCore.ReadVersion());
            await _core.StartAsync(QuitAsync, userInterfaceState: "tray");
            SetState(HostState.Running);
            FileLog.Write("[LauncherTrayController] Core running");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] StartHostAsync FAILED: {ex.Message}");
            SetState(HostState.Failed);
        }
    }

    private async Task RestartDirectorAsync()
    {
        FileLog.Write("[LauncherTrayController] RestartDirectorAsync: user requested");
        try
        {
            if (_core is null)
            {
                FileLog.Write("[LauncherTrayController] RestartDirectorAsync: core not ready");
                return;
            }
            // The core owns its supervisor internally; for the tray menu we create a standalone one.
            var supervisor = new DirectorSupervisor();
            await supervisor.RestartAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] RestartDirectorAsync FAILED: {ex.Message}");
        }
    }

    private async Task QuitAsync()
    {
        FileLog.Write("[LauncherTrayController] QuitAsync");
        _lifetime.Cancel();

        // Stream close, Gateway unregister, registration-file removal - all owned by the core.
        if (_core is not null)
        {
            await _core.StopAsync();
            _core = null;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _flyout?.Close();
            if (_trayIcon is not null) _trayIcon.IsVisible = false;
            _desktop.Shutdown();
        });
    }

    private void OpenLogsFolder()
        => OpenFolder(Path.GetDirectoryName(FileLog.CurrentLogPath), "OpenLogsFolder");

    private void OpenConfigFolder()
        => OpenFolder(Path.GetDirectoryName(InstallLayout.Default().ConfigPath), "OpenConfigFolder");

    private static void OpenFolder(string? dir, string label)
    {
        if (string.IsNullOrEmpty(dir))
        {
            FileLog.Write($"[LauncherTrayController] {label} REFUSED: no folder path resolved");
            return;
        }
        // Off the UI thread so the click returns immediately (shell launch latency).
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                FileLog.Write($"[LauncherTrayController] {label}: {dir}");
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherTrayController] {label} FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>Open a URL in the default browser, off the UI thread so the click returns immediately.</summary>
    private static void OpenUrl(string url, string label)
    {
        _ = Task.Run(() =>
        {
            try
            {
                FileLog.Write($"[LauncherTrayController] Open{label}: {url}");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherTrayController] Open{label} FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Enable/disable the start-at-login autostart from the flyout toggle
    /// (the Run key on Windows, the launchd launch agent on macOS).
    /// </summary>
    private void SetAutostart(bool enable)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        try
        {
            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Could not resolve own exe path");
                if (OperatingSystem.IsWindows())
                    LauncherAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());
                else
                    LauncherLaunchdAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());
                FileLog.Write("[LauncherTrayController] Autostart enabled by user");

                // Tell the registration file what the state IS now. Without this it kept reporting
                // the startup-era verdict: a failure the user had just repaired here, or health after a
                // toggle that silently failed.
                var registered = OperatingSystem.IsWindows()
                    ? LauncherAutostart.IsRegistered()
                    : LauncherLaunchdAutostart.IsRegistered();
                LauncherCore.RecordAutostartState(
                    registered ? null : "autostart was enabled from the tray but is not registered",
                    registered);
            }
            else
            {
                if (OperatingSystem.IsWindows())
                    LauncherAutostart.Unregister();
                else
                    LauncherLaunchdAutostart.Unregister();
                FileLog.Write("[LauncherTrayController] Autostart disabled by user");

                // Turned off ON PURPOSE is not a failure - reporting it as one would cry wolf at a
                // deliberate choice - but it is not registered either.
                LauncherCore.RecordAutostartState(failure: null, registered: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] SetAutostart FAILED: {ex.Message}");
            LauncherCore.RecordAutostartState(
                $"changing autostart from the tray failed: {ex.Message}", registered: false);
        }
    }

    private void SetState(HostState state)
    {
        _state = state;
        var tip = state switch
        {
            HostState.Starting => "Launcher - starting",
            HostState.Running => "Launcher - running",
            HostState.Failed => "Launcher - failed to start",
            _ => "Launcher",
        };
        // The flyout reads state live on open; here we only keep the tray tooltip current.
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
        try { _core?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { FileLog.Write($"[LauncherTrayController] Dispose stop error: {ex.Message}"); }
        _core = null;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        _lifetime.Dispose();
    }
}
