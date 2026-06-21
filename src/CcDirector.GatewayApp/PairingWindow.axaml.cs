using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;

namespace CcDirector.GatewayApp;

/// <summary>
/// The Gateway host's "Register a new device" window (issue #469, Anchor B). The pairing code is
/// shown ONLY here, on the gateway host's own screen - never over the network and never in the
/// Cockpit - so enrollment is rooted in local presence at the host machine.
///
/// Three states, matching REGISTER_DIRECTOR_MOCKUP.html:
///   1. Idle      - a "Register a new device" button.
///   2. Waiting   - the 4-digit code with a live countdown, while a Director submits it.
///   3. Joined    - "&lt;machine&gt; joined, N devices registered".
///
/// The window drives the in-process Gateway directly: it mints the code via the host's
/// <see cref="PairingCodeService"/> and detects the join by watching the device-registry count
/// (the enrollment endpoint records the new device when the code is consumed).
/// </summary>
public partial class PairingWindow : Window
{
    private readonly GatewayTrayController _controller;
    private DispatcherTimer? _tick;
    private int _baselineDeviceCount;
    private bool _joined;

    // XAML-less designer ctor (Avalonia previewer); never used at runtime.
    public PairingWindow() : this(null!) { }

    public PairingWindow(GatewayTrayController controller)
    {
        _controller = controller;
        InitializeComponent();
        FileLog.Write("[PairingWindow] open");

        RegisterButton.Click += (_, _) => OnRegister();
        CancelCodeButton.Click += (_, _) => OnCancelCode();
        DoneButton.Click += (_, _) => Close();
        Closed += (_, _) => OnClosed();
    }

    private void OnRegister()
    {
        try
        {
            var host = _controller?.Host;
            if (host is null)
            {
                FileLog.Write("[PairingWindow] OnRegister: gateway host not running");
                ShowWaitText("The Gateway is not running yet. Try again in a moment.");
                return;
            }

            _baselineDeviceCount = host.Devices.Count;
            var state = host.Pairing.Mint();
            FileLog.Write("[PairingWindow] OnRegister: minted a pairing code");

            ShowCodeState(state);
            StartTick();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PairingWindow] OnRegister FAILED: {ex}");
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

    /// <summary>Render the code-waiting state. Public so a screenshot/proof harness can show it.</summary>
    public void ShowCodeState(PairingCodeState state)
    {
        IdlePanel.IsVisible = false;
        JoinedPanel.IsVisible = false;
        CodePanel.IsVisible = true;
        CodeText.Text = state.Code;
        WaitText.Text = "Waiting for the device...";
        UpdateCountdown(state.ExpiresUtc);
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
