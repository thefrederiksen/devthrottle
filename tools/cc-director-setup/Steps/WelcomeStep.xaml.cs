using System.Windows;
using System.Windows.Controls;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class WelcomeStep : UserControl
{
    public WelcomeStep(bool isUpdate, string? installedVersion)
    {
        InitializeComponent();

        if (isUpdate)
        {
            TitleText.Text = "Update CC Director";
            DescriptionText.Text = "Checking for updates...";

            // Role is a first-install choice; an update refreshes whatever is already installed.
            RolePanel.Visibility = Visibility.Collapsed;

            if (installedVersion != null)
            {
                var displayVersion = installedVersion.Split('+')[0];
                VersionInfoText.Text = $"Currently installed: v{displayVersion}";
                VersionInfoText.Visibility = Visibility.Visible;
            }
        }

        SetupLog.Write($"[WelcomeStep] Created: isUpdate={isUpdate}");
    }

    /// <summary>The install type the user picked (Workstation by default).</summary>
    public InstallRole SelectedRole =>
        GatewayRadio.IsChecked == true ? InstallRole.Gateway : InstallRole.Workstation;

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
