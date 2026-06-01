using System.Windows;
using System.Windows.Controls;
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

            if (installedVersion != null)
            {
                var displayVersion = installedVersion.Split('+')[0];
                VersionInfoText.Text = $"Currently installed: v{displayVersion}";
                VersionInfoText.Visibility = Visibility.Visible;
            }
        }

        SetupLog.Write($"[WelcomeStep] Created: isUpdate={isUpdate}");
    }

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
