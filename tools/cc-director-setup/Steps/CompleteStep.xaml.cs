using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath;
    private readonly string _directorExePath;

    public CompleteStep(int installed, int skipped, string installPath, string directorExePath, bool isUpdate, bool alreadyUpToDate = false)
    {
        InitializeComponent();
        _installPath = installPath;
        _directorExePath = directorExePath;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;

        if (alreadyUpToDate)
        {
            HeadingText.Text = "Already Up to Date";
            DescriptionText.Text = "CC Director is already running the latest version.";
            PathNote.Visibility = Visibility.Collapsed;
        }
        else if (isUpdate)
        {
            HeadingText.Text = "Update Complete";
            DescriptionText.Text = "CC Director tools have been updated successfully.";
            PathNote.Visibility = Visibility.Collapsed;
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}");
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");

        // The Director installs to the app dir (app\cc-director.exe), not the tools bin dir.
        var exePath = _directorExePath;
        if (!File.Exists(exePath))
        {
            SetupLog.Write($"[CompleteStep] cc-director.exe not found at {exePath}");
            return;
        }

        try
        {
            // Build a fresh PATH by reading the current registry value
            // so the launched process inherits the updated PATH
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
            };

            var freshPath = GetFreshPath();
            if (freshPath != null)
            {
                psi.Environment["PATH"] = freshPath;
            }

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: cc-director launched");

            // Close the setup wizard
            Window.GetWindow(this)?.Close();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] LaunchButton_Click FAILED: {ex.Message}");
        }
    }

    private static string? GetFreshPath()
    {
        try
        {
            // Read user PATH from registry
            using var userKey = Registry.CurrentUser.OpenSubKey("Environment");
            var userPath = userKey?.GetValue("Path", "") as string ?? "";

            // Read system PATH from registry
            using var sysKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            var systemPath = sysKey?.GetValue("Path", "") as string ?? "";

            var combined = systemPath + ";" + userPath;
            SetupLog.Write("[CompleteStep] GetFreshPath: built fresh PATH from registry");
            return combined;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] GetFreshPath FAILED: {ex.Message}");
            return null;
        }
    }
}
