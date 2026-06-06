using System.Diagnostics;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using CcDirector.Setup.Engine;

namespace CcDirector.GatewayApp;

/// <summary>
/// The Gateway's status + settings window, opened from the tray menu (or a left-click on the
/// tray icon). Shows live status (state, port, uptime, Director count, Cockpit reachability)
/// refreshed by a background timer, and owns the autostart toggle. All I/O (Cockpit probe,
/// folder opens, registry writes) runs off the UI thread; the window opens instantly.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly GatewayTrayController _controller;
    private readonly DispatcherTimer _refresh;
    private bool _suppressAutostartEvent;

    // XAML-less designer ctor (Avalonia previewer); never used at runtime.
    public SettingsWindow() : this(null!) { }

    public SettingsWindow(GatewayTrayController controller)
    {
        _controller = controller;
        InitializeComponent();
        FileLog.Write("[SettingsWindow] open");

        VersionText.Text = $"v{AppVersion.Full}";
        PortText.Text = controller?.Port.ToString() ?? "?";
        ModeText.Text = GatewayAppOptions.Managed
            ? "managed (installed: supervises the Cockpit, auto-updates)"
            : "dev (Cockpit not supervised, no auto-update)";

        CloseButton.Click += (_, _) => Close();
        OpenLogsButton.Click += (_, _) => OpenFolder(Path.GetDirectoryName(FileLog.CurrentLogPath));
        OpenConfigButton.Click += (_, _) => OpenFolder(Path.GetDirectoryName(InstallLayout.Default().ConfigPath));
        AutostartCheck.IsCheckedChanged += OnAutostartToggled;
        RestartBrainButton.Click += OnRestartBrainClick;

        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshAsync());
        Closed += (_, _) =>
        {
            _refresh.Stop();
            FileLog.Write("[SettingsWindow] closed");
        };

        _ = RefreshAsync();
        _refresh.Start();
    }

    private async Task RefreshAsync()
    {
        if (_controller is null) return;

        // Cheap reads first (instant), the Cockpit probe + registry read off-thread.
        StateText.Text = _controller.StateText;
        var up = DateTime.UtcNow - _controller.StartedAtUtc;
        UptimeText.Text = up.TotalHours >= 1
            ? $"{(int)up.TotalHours}h {up.Minutes:D2}m"
            : $"{up.Minutes}m {up.Seconds:D2}s";
        DirectorsText.Text = (_controller.Host?.Registry.ListDirectors().Count ?? 0).ToString();

        var cockpitPort = CockpitSupervisor.ResolvePort();
        var (cockpitUp, autostart) = await Task.Run(async () =>
        {
            bool reachable;
            try
            {
                using var resp = await Http.GetAsync($"http://127.0.0.1:{cockpitPort}/");
                reachable = resp.IsSuccessStatusCode;
            }
            catch
            {
                reachable = false;
            }
            var registered = OperatingSystem.IsWindows() && GatewayAutostart.IsRegistered();
            return (reachable, registered);
        });

        CockpitText.Text = cockpitUp ? $"reachable on :{cockpitPort}" : $"not reachable on :{cockpitPort}";

        _suppressAutostartEvent = true;
        AutostartCheck.IsChecked = autostart;
        _suppressAutostartEvent = false;

        await RefreshBrainAsync();
    }

    /// <summary>
    /// Brain status line (issue #184). GetHealthAsync reads transcript files off disk,
    /// so it runs off the UI thread; it never spawns the brain - a dormant brain just
    /// shows "not started".
    /// </summary>
    private async Task RefreshBrainAsync()
    {
        BrainModelText.Text = _controller.Host?.BrainModel ?? "-";

        var brain = _controller.Brain;
        if (brain is null)
        {
            BrainStateText.Text = "unavailable";
            BrainSessionText.Text = "-";
            return;
        }

        var sessionId = brain.SessionId;
        var pid = brain.ProcessId;
        var health = await Task.Run(() => brain.GetHealthAsync());

        BrainStateText.Text = health.Status == "NotStarted"
            ? "not started (spawns on first use)"
            : health.IsAlive
                ? $"alive - {health.ActivityState}, idle {health.IdleSeconds:F0}s, context {health.ContextTokens:N0} tokens"
                : $"DEAD ({health.Status}) - use Restart Brain";
        BrainSessionText.Text = sessionId is null ? "-" : $"{sessionId} (pid {pid})";
    }

    private void OnRestartBrainClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var brain = _controller?.Brain;
        if (brain is null) return;

        RestartBrainButton.IsEnabled = false;
        BrainActionText.Text = "restarting (spawning claude.exe)...";
        FileLog.Write("[SettingsWindow] Restart Brain clicked");
        _ = Task.Run(async () =>
        {
            string outcome;
            try
            {
                await brain.RestartAsync();
                outcome = $"brain ready (pid {brain.ProcessId})";
                FileLog.Write($"[SettingsWindow] brain restart OK: pid={brain.ProcessId}, session={brain.SessionId}");
            }
            catch (Exception ex)
            {
                outcome = $"restart FAILED: {ex.Message}";
                FileLog.Write($"[SettingsWindow] brain restart FAILED: {ex.Message}");
            }
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                BrainActionText.Text = outcome;
                RestartBrainButton.IsEnabled = true;
                await RefreshBrainAsync();
            });
        });
    }

    private void OnAutostartToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressAutostartEvent || _controller is null) return;
        var enable = AutostartCheck.IsChecked == true;
        _ = Task.Run(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return;
                if (enable)
                {
                    var exe = Environment.ProcessPath
                              ?? throw new InvalidOperationException("Could not resolve own exe path");
                    GatewayAutostart.EnsureRegistered(exe, GatewayAppOptions.AutostartArguments());
                    FileLog.Write("[SettingsWindow] autostart enabled");
                }
                else
                {
                    GatewayAutostart.Unregister();
                    FileLog.Write("[SettingsWindow] autostart disabled");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsWindow] autostart toggle FAILED: {ex.Message}");
            }
        });
    }

    private static void OpenFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                FileLog.Write($"[SettingsWindow] opened folder {dir}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsWindow] OpenFolder FAILED: {ex.Message}");
            }
        });
    }
}
