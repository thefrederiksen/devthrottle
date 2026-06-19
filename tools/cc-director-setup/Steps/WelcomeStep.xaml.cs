using System.Windows;
using System.Windows.Controls;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class WelcomeStep : UserControl
{
    /// <summary>Raised when the user clicks Uninstall (update mode only). MainWindow runs the
    /// engine uninstaller; the step itself stays UI-only.</summary>
    public event EventHandler? UninstallRequested;

    /// <summary>Raised on a fresh install when the user picks a role, so the wizard can enable
    /// Next. Neither role is pre-selected, so Next stays disabled until this fires.</summary>
    public event EventHandler? RoleSelected;

    public WelcomeStep(bool isUpdate, string? installedVersion, InstallRole installedRole = InstallRole.Workstation)
    {
        InitializeComponent();

        if (isUpdate)
        {
            TitleText.Text = "Update DevThrottle";
            DescriptionText.Text = "Checking for updates...";

            // Role is a first-install choice; an update refreshes whatever is already installed.
            // Hide the interactive picker, but show, read-only, which type this machine actually is
            // so the user can see they are updating a Workstation and not the Gateway.
            RolePanel.Visibility = Visibility.Collapsed;
            InstalledRoleText.Text = installedRole == InstallRole.Gateway
                ? "Gateway -- DevThrottle app, all CLI tools, plus the Gateway tray app and Cockpit web UI. There should be only one Gateway."
                : "Workstation -- DevThrottle app + all CLI tools on this machine. Connects to a Gateway; it is not the Gateway itself.";
            InstalledRolePanel.Visibility = Visibility.Visible;

            if (installedVersion != null)
            {
                var displayVersion = installedVersion.Split('+')[0];
                VersionInfoText.Text = $"Currently installed: v{displayVersion}";
                VersionInfoText.Visibility = Visibility.Visible;
            }

            // An existing install is present, so offer to remove it (issue #257).
            UninstallButton.Visibility = Visibility.Visible;
            UninstallHint.Visibility = Visibility.Visible;
        }
        else
        {
            // Fresh install: the role picker is the hero of the screen, so the long marketing
            // paragraph is hidden to keep the one decision front and center. The "Click Next"
            // hint is redundant with the role cards + Next button and is dropped so the whole
            // screen fits the fixed window without a scrollbar.
            DescriptionText.Visibility = Visibility.Collapsed;
            ClickNextHint.Visibility = Visibility.Collapsed;

            // No silent default: neither role is pre-selected, so the user is forced to make the
            // one decision this screen exists for. Either pick raises RoleSelected so MainWindow can
            // enable the (initially disabled) Next button.
            WorkstationRadio.Checked += (_, _) => RoleSelected?.Invoke(this, EventArgs.Empty);
            GatewayRadio.Checked += (_, _) => RoleSelected?.Invoke(this, EventArgs.Empty);
        }

        SetupLog.Write($"[WelcomeStep] Created: isUpdate={isUpdate}");
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[WelcomeStep] UninstallButton_Click");
        UninstallRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The install type the user picked, or null if neither card is selected yet.
    /// There is no default - the wizard keeps Next disabled until this is non-null.</summary>
    public InstallRole? SelectedRole =>
        GatewayRadio.IsChecked == true ? InstallRole.Gateway
        : WorkstationRadio.IsChecked == true ? InstallRole.Workstation
        : null;

    public void UpdateVersionInfo(string? installedVersion, string? latestVersion)
    {
        SetupLog.Write($"[WelcomeStep] UpdateVersionInfo: installed={installedVersion}, latest={latestVersion}");

        Dispatcher.BeginInvoke(() =>
        {
            if (installedVersion == null || latestVersion == null)
                return;

            var installedClean = installedVersion.Split('+')[0].TrimStart('v');
            var latestClean = latestVersion.TrimStart('v');

            if (installedClean == latestClean)
            {
                DescriptionText.Text = "No upgrade available. You can reinstall tools as a repair.";
                VersionInfoText.Text = $"Installed: v{installedClean} (latest)";
                VersionInfoText.Visibility = Visibility.Visible;
            }
            else
            {
                DescriptionText.Text = $"Upgrade available: v{installedClean} -> v{latestClean}";
                VersionInfoText.Text = $"Currently installed: v{installedClean}";
                VersionInfoText.Visibility = Visibility.Visible;
            }
        });
    }
}
