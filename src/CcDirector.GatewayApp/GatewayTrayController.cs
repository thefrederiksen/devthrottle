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
using CcDirector.Gateway.Tray;
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

    // Issue #855: how often the background heartbeats refresh the cached flyout values. The Director
    // count read is cheap (an in-memory registry snapshot) so it refreshes often, keeping the flyout
    // count within one interval of the registry. The front-door URL resolution shells the tailscale
    // CLI (up to a ~5s blocking timeout) and the MagicDNS name almost never changes, so it refreshes
    // far less often - and only ever on this background thread, never on the flyout-open path.
    private static readonly TimeSpan DirectorCountHeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrontDoorHeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();

    // Issue #855: the cached values the flyout shows, refreshed by the background heartbeats below so
    // BuildFlyoutModel and OpenCockpit never do a synchronous registry read or tailscale CLI probe on
    // the open/click path (which previously delayed the flyout from painting).
    private readonly GatewayTrayFlyoutCache _flyoutCache = new();

    private TrayIcon? _trayIcon;
    private TrayFlyoutController? _flyout;
    private Bitmap? _icon;
    private string _statusText = "Gateway stopped";
    private GatewayHost? _host;
    private CockpitSupervisor? _cockpit;
    private SettingsWindow? _settingsWindow;
    private PairingWindow? _pairingWindow;
    // Issue #650: the first-run consent screen, shown once at the Gateway's first launch. Tracked so a
    // Gateway restart inside one run does not stack two screens and so Quit closes it.
    private GatewayConsentWindow? _consentWindow;
    private HostState _state = HostState.Stopped;
    private bool _busy;
    private bool _disposed;
    // Issue #637: cancels an in-flight browser loopback sign-in when the app quits, so a pending
    // hand-off never outlives the process. A fresh source is created for each sign-in run.
    private CancellationTokenSource? _signInCts;

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

        // Issue #855: keep the flyout's cached Director count and front-door URL warm on background
        // heartbeats so the left-click flyout paints instantly from cache, never blocking the open on
        // a synchronous registry read or a tailscale CLI probe.
        _ = RunHeartbeatAsync("director-count", DirectorCountHeartbeatInterval, RefreshDirectorCountCache, _lifetime.Token);
        _ = RunHeartbeatAsync("front-door", FrontDoorHeartbeatInterval, RefreshFrontDoorCache, _lifetime.Token);

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

    /// <summary>
    /// Build the flyout's content from current state (called fresh on each open). Issue #855: the
    /// Director count comes from the heartbeat-refreshed cache (<see cref="_flyoutCache"/>) - never a
    /// synchronous registry read - so this method does no blocking I/O and the flyout paints instantly.
    /// Uptime stays computed inline (it is a cheap subtraction, no I/O).
    /// </summary>
    private TrayFlyoutModel BuildFlyoutModel()
    {
        var up = DateTime.UtcNow - StartedAtUtc;
        var uptime = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes:D2}m" : $"{up.Minutes}m {up.Seconds:D2}s";

        // Issue #637: the Gateway's DevThrottle sign-in state, surfaced on the tray. The decision logic
        // lives in GatewaySignInTraySurface (library, unit-tested) so this method stays a thin binding.
        // Read locally from the cached credential (no network). A null sign-in flow (no credential
        // service on this host) shows nothing rather than a misleading "signed out".
        var signIn = _host?.SignIn;
        var accountValue = CcDirector.Gateway.Account.GatewaySignInTraySurface.AccountRowValue(signIn);

        var rows = new List<StatusRow>
        {
            new("Version", AppVersion.Full.Split('+')[0]), // trim the +githash, matching the launcher
            new("Directors", _flyoutCache.DirectorCountDisplay),
            new("Mode", GatewayAppOptions.Managed ? "managed" : "dev"),
            new("Uptime", uptime),
        };
        if (accountValue is not null)
            rows.Add(new(CcDirector.Gateway.Account.GatewaySignInTraySurface.AccountRowLabel, accountValue));

        // ONE URL (docs/plans/one-url-cockpit.md): Open Cockpit is the primary action.
        var actions = new List<FlyoutAction>
        {
            new() { Text = "Open Cockpit", Primary = true, OnClick = OpenCockpit },
            // Issue #856: adding a device leads with signing into the same DevThrottle account (a QR /
            // deep-link), with issue #469's local pairing code kept as the secondary fallback.
            new() { Text = "Add a device", OnClick = OpenPairing },
            new() { Text = "Open Settings", OnClick = OpenSettings },
            new() { Text = "Restart Gateway", OnClick = () => _ = RestartAsync() },
            new() { Text = "Open Logs Folder", OnClick = OpenLogsFolder },
        };
        // Issue #637: the "Sign in to DevThrottle" action starts (or retries) the browser loopback
        // sign-in. Shown only while the Gateway is NOT signed in, so it is the start/retry surface
        // for the forced first-launch sign-in and the retry after a failed/cancelled attempt.
        if (CcDirector.Gateway.Account.GatewaySignInTraySurface.ShouldShowSignInAction(signIn))
            actions.Insert(1, new() { Text = CcDirector.Gateway.Account.GatewaySignInTraySurface.SignInActionText, OnClick = StartSignIn });

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

    /// <summary>
    /// Issue #855: a background heartbeat that periodically runs <paramref name="refresh"/> to keep a
    /// cached flyout value warm. The refresh runs first (so the cache resolves as soon as possible),
    /// then the loop waits <paramref name="interval"/> before the next refresh. A refresh failure only
    /// logs and the heartbeat keeps running - a transient registry or tailscale hiccup must not stop
    /// the cache from refreshing (the same long-running-loop boundary handling as RunUpdateLoopAsync).
    /// </summary>
    private static async Task RunHeartbeatAsync(string name, TimeSpan interval, Action refresh, CancellationToken ct)
    {
        FileLog.Write($"[GatewayTrayController] {name} heartbeat started (interval={interval.TotalSeconds}s)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                refresh();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayTrayController] {name} heartbeat refresh FAILED: {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        FileLog.Write($"[GatewayTrayController] {name} heartbeat stopped");
    }

    /// <summary>
    /// Issue #855: refresh the cached Director count from the registry (off the UI thread). The read is
    /// a cheap in-memory snapshot; it is cached so the flyout open path never calls it synchronously.
    /// While the host is still starting there is no registry yet, so the cache keeps its placeholder.
    /// </summary>
    private void RefreshDirectorCountCache()
    {
        var host = _host;
        if (host is null)
            return; // host not up yet - leave the cached "..." placeholder until it is

        _flyoutCache.SetDirectorCount(host.Registry.ListDirectors().Count);
    }

    /// <summary>
    /// Issue #855: refresh the cached Tailscale front-door base URL (off the UI thread). This is the
    /// only caller that shells the tailscale CLI; the Open Cockpit click reads the cached value so it
    /// never blocks. A null result (Tailscale unavailable) is cached as null so Open Cockpit refuses.
    /// </summary>
    private void RefreshFrontDoorCache()
        => _flyoutCache.SetFrontDoorBaseUrl(TailscaleIdentity.TryGetFrontDoorBaseUrl());

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

            // Issue #637 (Gateway Centralization Phase 2): forced sign-in at the Gateway's first
            // launch. When the Gateway has no stored credential, auto-prompt the browser loopback
            // sign-in; when it already has one, do nothing (a subsequent launch never re-prompts).
            PromptSignInIfNeeded();

            // Issue #650 (Gateway Centralization Phase 3): the first-run consent screen, shown ONCE
            // at the Gateway's first launch alongside the sign-in. When the Gateway has not yet
            // acknowledged it, show it; a subsequent launch never re-shows it.
            ShowConsentIfNeeded();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] StartHostAsync FAILED: {ex.Message}");
            await DiagnoseStartFailureAsync();
        }
    }

    /// <summary>
    /// Issue #650: on launch, show the first-run consent screen only when the Gateway has not yet
    /// acknowledged it (<see cref="CcDirector.Gateway.Account.GatewayConsentSurface"/>). Once
    /// acknowledged, a subsequent launch never re-shows it (acceptance criterion 2). The screen is
    /// shown on the UI thread (it has no owner window - the Gateway is a tray-only app) and is only
    /// raised once per process via <see cref="_consentWindow"/>, so a Gateway restart inside one run
    /// does not stack two screens.
    /// </summary>
    private void ShowConsentIfNeeded()
    {
        if (!CcDirector.Gateway.Account.GatewayConsentSurface.ShouldShowConsentOnLaunch())
        {
            FileLog.Write("[GatewayTrayController] ShowConsentIfNeeded: gateway consent already acknowledged - not showing");
            return;
        }

        FileLog.Write("[GatewayTrayController] ShowConsentIfNeeded: gateway consent not yet acknowledged - showing the consent screen");
        Dispatcher.UIThread.Post(() =>
        {
            if (_consentWindow is { } open)
            {
                open.Activate();
                return;
            }
            _consentWindow = new GatewayConsentWindow();
            _consentWindow.Closed += (_, _) => _consentWindow = null;
            _consentWindow.Show();
        });
    }

    /// <summary>
    /// Issue #637: on launch, prompt the browser loopback sign-in only when the Gateway has no stored
    /// credential. Already signed in -> no prompt (acceptance criterion 3). Fire-and-forget so the
    /// host-start path is never blocked by the browser hand-off.
    /// </summary>
    private void PromptSignInIfNeeded()
    {
        var signIn = _host?.SignIn;
        if (!CcDirector.Gateway.Account.GatewaySignInTraySurface.ShouldPromptOnLaunch(signIn))
        {
            FileLog.Write(signIn is null
                ? "[GatewayTrayController] PromptSignInIfNeeded: no sign-in flow on this host (no credential service) - skipping"
                : "[GatewayTrayController] PromptSignInIfNeeded: Gateway already signed in - not prompting");
            return;
        }

        FileLog.Write("[GatewayTrayController] PromptSignInIfNeeded: Gateway has no credential - prompting browser sign-in");
        StartSignIn();
    }

    /// <summary>
    /// Issue #637: start (or retry) the browser loopback sign-in. Detached and guarded so the tray
    /// click returns immediately and a second click while one is running is a no-op (the service's
    /// single-flight guard). A failed or cancelled sign-in only logs - the Gateway stays un-signed-in
    /// and the tray "Sign in to DevThrottle" action remains, so it is retryable with no crash.
    /// </summary>
    private void StartSignIn()
    {
        var signIn = _host?.SignIn;
        if (signIn is null)
        {
            FileLog.Write("[GatewayTrayController] StartSignIn: no sign-in flow on this host - ignoring");
            return;
        }
        if (signIn.IsSignInRunning)
        {
            FileLog.Write("[GatewayTrayController] StartSignIn: a sign-in is already in flight - ignoring");
            return;
        }

        _signInCts?.Dispose();
        _signInCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _signInCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await signIn.RunSignInAsync(ct);
                FileLog.Write(result.Succeeded
                    ? "[GatewayTrayController] StartSignIn: signed in to DevThrottle"
                    : $"[GatewayTrayController] StartSignIn: not signed in ({result.FailureReason}); retryable from the tray");
            }
            catch (Exception ex)
            {
                // Boundary swallow for the detached task: a sign-in failure must never crash the tray.
                FileLog.Write($"[GatewayTrayController] StartSignIn FAILED: {ex.Message}; retryable from the tray");
            }
        });
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
            _pairingWindow?.Close();
            _consentWindow?.Close();
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

    /// <summary>
    /// Open the "Add a device" window (issue #856). It leads with signing into the same DevThrottle
    /// account (QR / deep-link to the plain sign-in URL) and keeps issue #469's local pairing code as
    /// the secondary fallback - that code lives ONLY on this host's screen (the local-presence root of
    /// trust).
    /// </summary>
    private void OpenPairing()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_pairingWindow is { } open)
            {
                open.Activate();
                return;
            }
            _pairingWindow = new PairingWindow(this);
            _pairingWindow.Closed += (_, _) => _pairingWindow = null;
            _pairingWindow.Show();
        });
    }

    private void OpenCockpit()
        // ONE URL: the Cockpit is served through the gateway front door
        // (https://<magicdns>/ -> :7878 -> fallback proxy -> loopback Cockpit).
        // Issue #855: read the front-door URL from the heartbeat-refreshed cache instead of shelling
        // the tailscale CLI on the click. When it has not resolved yet (or Tailscale is unavailable)
        // the cached value is null, so OpenTailnetUrl takes the existing refuse-with-clear-message
        // path rather than hanging the click on a CLI probe.
        => OpenTailnetUrl(_flyoutCache.FrontDoorBaseUrl is { } d ? d + "/" : null, "Cockpit");

    // Open a Tailscale URL in the browser. There is NO localhost fallback by design: every
    // cc-director URL must be the tailnet URL so it works from any node, and a loopback URL
    // would only work on this machine. When Tailscale is unavailable the URL is null; we
    // refuse and say why rather than open a URL that is wrong everywhere but here.
    private void OpenTailnetUrl(string? url, string label)
    {
        // The URL is already resolved by the caller (issue #855: from the heartbeat cache, not a
        // synchronous probe). Process.Start is still launched off the UI thread so the click returns
        // immediately and any shell-launch latency never touches the UI.
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
        // Issue #637: _lifetime.Cancel() above already cancelled any in-flight sign-in (its token is
        // linked); dispose the source so it does not leak.
        try { _signInCts?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayTrayController] Dispose sign-in cts error: {ex.Message}"); }
        _signInCts = null;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        _lifetime.Dispose();
    }
}
