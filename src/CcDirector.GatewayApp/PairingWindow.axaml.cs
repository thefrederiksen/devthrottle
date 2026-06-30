using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;

namespace CcDirector.GatewayApp;

/// <summary>
/// The Gateway host's "Add a device" window (issue #856, re-framing issue #469's Anchor B). Adding a
/// device now LEADS with signing into the same DevThrottle account: the window shows a QR code and a
/// plain sign-in link the user opens on the new device, and signing into the same account is what joins
/// it (the cloud issues the per-device key on that sign-in - sibling issue). The QR/link carries ONLY a
/// plain sign-in URL, never a secret.
///
/// The 4-digit local pairing code (#469) is kept as a clearly SECONDARY, labelled fallback. When the
/// user chooses it, the code is shown ONLY here, on the gateway host's own screen - never over the
/// network and never in the Cockpit - so that fallback enrollment stays rooted in local presence at the
/// host machine.
///
/// Three states:
///   1. Idle      - account sign-in (QR + link) as primary, the pairing-code fallback below it.
///   2. Waiting   - the 4-digit code with a live countdown, while a Director submits it (the fallback).
///   3. Joined    - "&lt;machine&gt; joined, N devices registered".
///
/// The pairing fallback drives the in-process Gateway directly: it mints the code via the host's
/// <see cref="PairingCodeService"/> and detects the join by watching the device-registry count
/// (the enrollment endpoint records the new device when the code is consumed).
/// </summary>
public partial class PairingWindow : Window
{
    private readonly GatewayTrayController _controller;
    private DispatcherTimer? _tick;
    private int _baselineDeviceCount;
    private bool _joined;

    /// <summary>
    /// The plain DevThrottle sign-in URL shown as the QR code / deep-link (issue #856). Resolved on
    /// load and exposed so a proof harness can assert what the QR resolves to. Empty until loaded.
    /// </summary>
    public string ResolvedSignInUrl { get; private set; } = "";

    // XAML-less designer ctor (Avalonia previewer); never used at runtime.
    public PairingWindow() : this(null!) { }

    public PairingWindow(GatewayTrayController controller)
    {
        _controller = controller;
        InitializeComponent();
        FileLog.Write("[PairingWindow] open");

        ShowCodeButton.Click += (_, _) => OnShowPairingCode();
        CancelCodeButton.Click += (_, _) => OnCancelCode();
        DoneButton.Click += (_, _) => Close();
        Closed += (_, _) => OnClosed();

        // Responsive UI: the window paints immediately with the "Preparing sign-in code..."
        // placeholder; the QR is rendered on load (CPU-bound, off the UI thread) and shown when ready.
        Loaded += async (_, _) => await ShowSignInQrAsync();
    }

    /// <summary>
    /// Resolve the plain DevThrottle sign-in URL and render its QR code (issue #856). The URL is the
    /// account sign-in page only - no loopback callback, no pairing code, no secret - so it is safe to
    /// show and to scan on a second device. Public so a screenshot/proof harness can drive it.
    /// </summary>
    public async Task ShowSignInQrAsync()
    {
        try
        {
            var url = FirstRunLoginCoordinator.ResolveSignInBaseUrl();
            ResolvedSignInUrl = url;
            SignInUrlText.Text = url;
            FileLog.Write($"[PairingWindow] ShowSignInQrAsync: sign-in URL resolved to {url}");

            var png = await Task.Run(() => DeviceSignInQrCode.RenderPng(url));
            using var stream = new MemoryStream(png);
            var bitmap = new Bitmap(stream);

            QrImage.Source = bitmap;
            QrFallbackText.IsVisible = false;
            FileLog.Write("[PairingWindow] ShowSignInQrAsync: QR code rendered");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PairingWindow] ShowSignInQrAsync FAILED: {ex}");
            QrFallbackText.Text = "Could not render the sign-in code. Use the link or a pairing code.";
        }
    }

    /// <summary>
    /// The secondary fallback: mint and show the 4-digit pairing code (issue #469). Triggered by the
    /// "Show a pairing code" button under the account sign-in primary.
    /// </summary>
    private void OnShowPairingCode()
    {
        try
        {
            var host = _controller?.Host;
            if (host is null)
            {
                FileLog.Write("[PairingWindow] OnShowPairingCode: gateway host not running");
                ShowCodeState(null);
                ShowWaitText("The Gateway is not running yet. Try again in a moment.");
                return;
            }

            _baselineDeviceCount = host.Devices.Count;
            var state = host.Pairing.Mint();
            FileLog.Write("[PairingWindow] OnShowPairingCode: minted a pairing code");

            ShowCodeState(state);
            StartTick();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PairingWindow] OnShowPairingCode FAILED: {ex}");
            ShowCodeState(null);
            ShowWaitText("Could not start pairing. See the logs.");
        }
    }

    private void OnCancelCode()
    {
        try
        {
            _controller?.Host?.Pairing.Cancel();
            FileLog.Write("[PairingWindow] OnCancelCode");
            StopTick();
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PairingWindow] OnCancelCode FAILED: {ex}");
        }
    }

    private void StartTick()
    {
        StopTick();
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnTick());
        _tick.Start();
    }

    private void StopTick()
    {
        _tick?.Stop();
        _tick = null;
    }

    private void OnTick()
    {
        try
        {
            var host = _controller?.Host;
            if (host is null) return;

            // Join detection: the enrollment endpoint records the new device when the code is
            // consumed, so a count above the baseline means a Director just joined.
            if (!_joined && host.Devices.Count > _baselineDeviceCount)
            {
                _joined = true;
                StopTick();
                var devices = host.Devices.List();
                var newest = devices.Count > 0 ? devices[0] : null;
                ShowJoinedState(newest?.MachineName ?? "A device", host.Devices.Count);
                return;
            }

            var state = host.Pairing.Current();
            if (state is null)
            {
                // Code expired or was consumed without our seeing the count yet; re-check the count
                // once more, otherwise report expiry.
                if (!_joined && host.Devices.Count > _baselineDeviceCount)
                {
                    _joined = true;
                    StopTick();
                    var devices = host.Devices.List();
                    var newest = devices.Count > 0 ? devices[0] : null;
                    ShowJoinedState(newest?.MachineName ?? "A device", host.Devices.Count);
                    return;
                }
                StopTick();
                ShowWaitText("The code expired. Click Cancel and register again.");
                return;
            }

            UpdateCountdown(state.ExpiresUtc);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PairingWindow] OnTick FAILED: {ex}");
            StopTick();
        }
    }

    /// <summary>
    /// Render the code-waiting fallback state. Pass null to switch to the panel without a code (so a
    /// wait/error message can be shown). Public so a screenshot/proof harness can show it.
    /// </summary>
    public void ShowCodeState(PairingCodeState? state)
    {
        IdlePanel.IsVisible = false;
        JoinedPanel.IsVisible = false;
        CodePanel.IsVisible = true;
        if (state is not null)
        {
            CodeText.Text = state.Code;
            UpdateCountdown(state.ExpiresUtc);
        }
        WaitText.Text = "Waiting for the device...";
    }

    private void UpdateCountdown(DateTime expiresUtc)
    {
        var remaining = expiresUtc - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        CountdownText.Text = $"Expires in {(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
    }

    /// <summary>Render the joined state. Public so a screenshot/proof harness can show it.</summary>
    public void ShowJoinedState(string machineName, int deviceCount)
    {
        FileLog.Write($"[PairingWindow] ShowJoinedState: machine={machineName}, deviceCount={deviceCount}");
        IdlePanel.IsVisible = false;
        CodePanel.IsVisible = false;
        JoinedPanel.IsVisible = true;
        JoinedTitleText.Text = $"{machineName} joined";
        JoinedSubText.Text = deviceCount == 1
            ? "1 device registered"
            : $"{deviceCount} devices registered";
    }

    private void ShowWaitText(string text)
    {
        WaitText.Text = text;
    }

    private void OnClosed()
    {
        StopTick();
        // Cancel any still-active code so a closed window never leaves a live grant on screen.
        if (!_joined)
            _controller?.Host?.Pairing.Cancel();
        FileLog.Write("[PairingWindow] closed");
    }
}
