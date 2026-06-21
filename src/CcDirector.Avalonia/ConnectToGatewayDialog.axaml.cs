using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The Director's "Connect to Gateway" dialog (issue #469). The user enters the Gateway URL and
/// the 4-digit pairing code read off the gateway host's screen; on Join the dialog enrolls this
/// device, the Gateway issues a unique per-device key, and the key is written to the local
/// credential file the Director and local cc-* tools both read.
///
/// Join stays disabled until exactly 4 digits are entered (mirrors the mockup). On success the
/// dialog flips to "Registered as &lt;machine&gt;".
/// </summary>
public partial class ConnectToGatewayDialog : Window
{
    private readonly string _deviceId;
    private readonly string _machineName;

    public ConnectToGatewayDialog() : this("", "") { }

    public ConnectToGatewayDialog(string deviceId, string prefillUrl)
    {
        _deviceId = deviceId ?? "";
        _machineName = Environment.MachineName;
        InitializeComponent();
        FileLog.Write($"[ConnectToGatewayDialog] open: deviceId={_deviceId}, machine={_machineName}");

        if (!string.IsNullOrWhiteSpace(prefillUrl))
            UrlBox.Text = prefillUrl;

        // Join enables only at exactly 4 digits (issue #469 acceptance criterion).
        CodeBox.TextChanged += (_, _) => UpdateJoinEnabled();
        UpdateJoinEnabled();
    }

    /// <summary>Set the URL + code for a screenshot/proof harness (drives the enabled-at-4-digits state).</summary>
    public void SetForProof(string url, string code)
    {
        UrlBox.Text = url;
        CodeBox.Text = code;
        UpdateJoinEnabled();
    }

    private void UpdateJoinEnabled()
    {
        var code = (CodeBox.Text ?? "").Trim();
        var isFourDigits = code.Length == 4 && code.All(char.IsDigit);
        JoinButton.IsEnabled = isFourDigits;
        StatusText.Text = isFourDigits
            ? "Code entered - ready to join."
            : "Waiting for a pairing code...";
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ConnectToGatewayDialog] cancelled");
        Close();
    }

    private async void BtnJoin_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ConnectToGatewayDialog] Join clicked");
        try
        {
            JoinButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusText.Foreground = global::Avalonia.Media.Brushes.Gray;
            StatusText.Text = "Registering this device with the Gateway...";

            var result = await EnrollAndSaveAsync();
            if (!result.Success)
            {
                StatusText.Foreground = global::Avalonia.Media.Brush.Parse("#E05656");
                StatusText.Text = result.ErrorMessage ?? "Registration failed.";
                JoinButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                return;
            }

            ShowSuccess(result.Value);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectToGatewayDialog] Join FAILED: {ex}");
            StatusText.Foreground = global::Avalonia.Media.Brush.Parse("#E05656");
            StatusText.Text = "Registration failed unexpectedly. See the logs.";
            JoinButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private async Task<OperationResult<DeviceRegistrationResult>> EnrollAndSaveAsync()
    {
        var url = (UrlBox.Text ?? "").Trim();
        var code = (CodeBox.Text ?? "").Trim();

        var enrollment = await GatewayEnrollmentClient.EnrollAsync(url, _deviceId, _machineName, code);
        if (!enrollment.Success || enrollment.Value is null)
            return OperationResult<DeviceRegistrationResult>.Fail(
                enrollment.ErrorMessage ?? "Registration failed.");

        var response = enrollment.Value;
        // Persist the issued per-device key to the local credential file + config.json off the UI thread.
        await Task.Run(() => GatewayCredentialStore.SaveEnrolledKey(url, response.DeviceKey));

        var maskedKey = response.DeviceKey.Length > 8
            ? response.DeviceKey[..4] + "..." + response.DeviceKey[^4..]
            : "********";
        return OperationResult<DeviceRegistrationResult>.Ok(
            new DeviceRegistrationResult(response.MachineName, maskedKey));
    }

    /// <summary>
    /// Render the success state for a given machine + masked key. Public so a screenshot/proof
    /// harness can show it without a live Gateway.
    /// </summary>
    public void ShowSuccessForProof(string machineName, string maskedKey)
        => ShowSuccess(new DeviceRegistrationResult(machineName, maskedKey));

    private void ShowSuccess(DeviceRegistrationResult? result)
    {
        var machine = string.IsNullOrWhiteSpace(result?.MachineName) ? _machineName : result.MachineName;
        FileLog.Write($"[ConnectToGatewayDialog] registered as {machine}");
        FormPanel.IsVisible = false;
        SuccessPanel.IsVisible = true;
        JoinButton.IsVisible = false;
        CancelButton.IsVisible = false;
        DoneButton.IsVisible = true;
        SuccessTitleText.Text = $"Registered as {machine}";
        SuccessSubText.Text = $"device key  {result?.MaskedKey ?? "********"}  (saved to local credential file)";
    }

    private void BtnDone_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    /// <summary>The fields the success state renders: the registered machine and a masked key.</summary>
    private sealed record DeviceRegistrationResult(string MachineName, string MaskedKey);
}
