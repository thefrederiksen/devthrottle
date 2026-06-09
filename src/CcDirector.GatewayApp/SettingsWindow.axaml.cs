using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using CcDirector.Setup.Engine;

namespace CcDirector.GatewayApp;

/// <summary>
/// The Gateway's tray status window, opened from the tray menu (or a left-click on the tray
/// icon). It is now a pure status light: live state (port, uptime, Director count, Cockpit
/// reachability, a one-line brain summary), refreshed by a background timer. All configuration
/// (API keys, brain restart, autostart) moved into the Cockpit Settings page
/// (docs/architecture/gateway/SETTINGS_OWNERSHIP.md); the "Open Settings in Cockpit" button is
/// the bridge there. All I/O (Cockpit probe, folder opens, browser launch) runs off the UI
/// thread; the window opens instantly.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly GatewayTrayController _controller;
    private readonly DispatcherTimer _refresh;

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

        // About / installed: local, fast reads (file times + installed.json).
        BuildDateText.Text = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(unknown)";
        InstallRootText.Text = AboutInfo.InstallRoot;
        var installed = AboutInfo.InstalledComponents();
        InstalledText.Text = installed.Count == 0
            ? "(no installed.json - dev build?)"
            : string.Join(", ", installed.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key} {kv.Value}"));

        // The front-door URL shells tailscale, so resolve it off the UI thread and fill in when ready.
        _ = Task.Run(() =>
        {
            string url;
            try { url = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } b ? b + "/" : "(Tailscale unavailable)"; }
            catch { url = "(unavailable)"; }
            Dispatcher.UIThread.Post(() => CockpitUrlText.Text = url);
        });

        CloseButton.Click += (_, _) => Close();
        OpenLogsButton.Click += (_, _) => OpenFolder(Path.GetDirectoryName(FileLog.CurrentLogPath));
        OpenConfigButton.Click += (_, _) => OpenFolder(Path.GetDirectoryName(InstallLayout.Default().ConfigPath));
        OpenSettingsButton.Click += (_, _) => OpenCockpitSettings();

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

        // Cheap reads first (instant), the Cockpit probe off-thread.
        StateText.Text = _controller.StateText;
        var up = DateTime.UtcNow - _controller.StartedAtUtc;
        UptimeText.Text = up.TotalHours >= 1
            ? $"{(int)up.TotalHours}h {up.Minutes:D2}m"
            : $"{up.Minutes}m {up.Seconds:D2}s";
        DirectorsText.Text = (_controller.Host?.Registry.ListDirectors().Count ?? 0).ToString();

        var cockpitPort = CockpitSupervisor.ResolvePort();
        var cockpitUp = await Task.Run(async () =>
        {
            try
            {
                using var resp = await Http.GetAsync($"http://127.0.0.1:{cockpitPort}/");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        });

        CockpitText.Text = cockpitUp ? $"reachable on :{cockpitPort}" : $"not reachable on :{cockpitPort}";

        await RefreshBrainAsync();
    }

    /// <summary>
    /// One-line brain summary (issue #184). GetHealthAsync reads transcript files off disk, so it
    /// runs off the UI thread; it never spawns the brain - a dormant brain just shows "not started".
    /// The restart verb itself now lives in the Cockpit Settings page.
    /// </summary>
    private async Task RefreshBrainAsync()
    {
        var brain = _controller.Brain;
        if (brain is null)
        {
            BrainLineText.Text = "unavailable";
            return;
        }

        var health = await Task.Run(() => brain.GetHealthAsync());
        BrainLineText.Text = health.Status == "NotStarted"
            ? "not started (spawns on first use)"
            : health.IsAlive
                ? $"alive ({_controller.Host?.BrainModel ?? "-"}), idle {health.IdleSeconds:F0}s"
                : $"DEAD ({health.Status}) - restart in the Cockpit";
    }

    /// <summary>
    /// Open the Cockpit Settings page in the default browser. The loopback Cockpit child serves
    /// the page directly (no front-door token needed for a local click), so the URL is always the
    /// local Cockpit port - reliable whether or not Tailscale is up.
    /// </summary>
    private void OpenCockpitSettings()
    {
        var port = CockpitSupervisor.ResolvePort();
        var url = $"http://127.0.0.1:{port}/settings";
        FileLog.Write($"[SettingsWindow] Open Settings in Cockpit -> {url}");
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsWindow] OpenCockpitSettings FAILED: {ex.Message}");
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
