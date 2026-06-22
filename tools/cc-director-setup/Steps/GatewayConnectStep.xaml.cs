using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// The mandatory gateway-pairing step for a Workstation install (issue #646). A machine that joins
/// an existing fleet must connect to its gateway before the install can finish - the gateway is the
/// account authority, so a Workstation with no gateway connection is useless. The user enters the
/// gateway URL and the 4-digit pairing code shown on the gateway host; clicking Connect verifies
/// reachability and the pairing via the existing <c>POST /devices/register</c> flow (the gateway
/// issues a per-device key) and, on success, persists the gateway URL + device key to config.json.
///
/// Until that verification succeeds the step stays unverified, so the wizard keeps Next/Finish
/// disabled (mirrors the forced Sign-in gate, issue #657). A bad URL or wrong code shows a clear
/// message and does NOT mark the step verified, so the install cannot complete. The step raises
/// <see cref="PairingVerified"/> exactly once, when a device key is issued and persisted.
///
/// This step is only shown on the Workstation path; the Gateway role IS the gateway and skips it.
/// </summary>
public partial class GatewayConnectStep : UserControl
{
    private readonly GatewayPairingRunner _runner;
    private readonly string _deviceId;
    private readonly string _machineName;

    /// <summary>True once the gateway has verified the pairing and a device key was persisted. The
    /// wizard gates Next on this; once true it stays true so returning via Back keeps the state.</summary>
    public bool IsVerified { get; private set; }

    /// <summary>Raised once when the pairing verifies, so the wizard can enable Next.</summary>
    public event EventHandler? PairingVerified;

    public GatewayConnectStep() : this(runner: null)
    {
    }

    /// <summary>Constructor seam so a test or proof harness can inject a runner (e.g. one with a fake
    /// HTTP handler). When no runner is supplied a default one is built that makes the real
    /// <c>/devices/register</c> call and persists the issued key to config.json.</summary>
    public GatewayConnectStep(GatewayPairingRunner? runner)
    {
        InitializeComponent();
        _runner = runner ?? new GatewayPairingRunner();
        // The installer has no running Director yet, so it mints a stable device id for this machine.
        // The gateway registry records it; the credential the Director presents at runtime is the
        // issued per-device KEY (persisted to config.json), not this id.
        _deviceId = Guid.NewGuid().ToString();
        _machineName = Environment.MachineName;

        CodeBox.TextChanged += (_, _) => UpdateConnectEnabled();
        UpdateConnectEnabled();
        SetupLog.Write($"[GatewayConnectStep] Created: machine={_machineName}");
    }

    /// <summary>Connect enables only at exactly 4 digits (mirrors the Connect-to-Gateway dialog).</summary>
    private void UpdateConnectEnabled()
    {
        var code = (CodeBox.Text ?? "").Trim();
        ConnectButton.IsEnabled = code.Length == 4 && code.All(char.IsDigit);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] ConnectButton_Click");
        try
        {
            EnterWaitingState();

            var url = (UrlBox.Text ?? "").Trim();
            var code = (CodeBox.Text ?? "").Trim();

            // The runner does the I/O (HTTP + config.json write); keep it off the UI thread.
            var result = await Task.Run(() => _runner.VerifyAndSaveAsync(url, _deviceId, _machineName, code));

            ApplyResult(result);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[GatewayConnectStep] ConnectButton_Click FAILED: {ex}");
            ShowRetryable("Connecting to the gateway failed unexpectedly. Please try again.");
        }
    }

    /// <summary>Shows the "verifying..." state: hide any prior message/success, show the waiting row,
    /// and disable the inputs while the call is in flight.</summary>
    private void EnterWaitingState()
    {
        ConnectButton.IsEnabled = false;
        UrlBox.IsEnabled = false;
        CodeBox.IsEnabled = false;
        StatusText.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
    }

    private void ApplyResult(OperationResult<DeviceRegistrationResponse> result)
    {
        WaitingPanel.Visibility = Visibility.Collapsed;

        if (result.Success && result.Value is not null)
        {
            SetupLog.Write($"[GatewayConnectStep] ApplyResult: verified, machine={result.Value.MachineName}");
            IsVerified = true;
            ConnectButton.Visibility = Visibility.Collapsed;
            UrlBox.IsEnabled = false;
            CodeBox.IsEnabled = false;
            SuccessText.Text = $"Connected to the gateway as {result.Value.MachineName}. Click Next to continue.";
            SuccessPanel.Visibility = Visibility.Visible;
            PairingVerified?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetupLog.Write($"[GatewayConnectStep] ApplyResult: blocked - {result.ErrorMessage}");
        ShowRetryable(result.ErrorMessage ?? "The gateway did not accept the pairing.");
    }

    /// <summary>Returns the step to a retryable state with a message - the inputs and Connect button
    /// are re-enabled so the user can correct the URL/code and try again. The step stays UNVERIFIED,
    /// so the wizard keeps Next disabled and the install cannot complete.</summary>
    private void ShowRetryable(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        UrlBox.IsEnabled = true;
        CodeBox.IsEnabled = true;
        UpdateConnectEnabled();
    }
}
