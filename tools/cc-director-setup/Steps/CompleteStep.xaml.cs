using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath;
    private readonly string _directorExePath;
    private readonly int _installed;
    private readonly int _skipped;
    private readonly bool _isUpdate;

    public CompleteStep(int installed, int skipped, string installPath, string directorExePath, bool isUpdate, bool alreadyUpToDate = false)
    {
        InitializeComponent();
        _installPath = installPath;
        _directorExePath = directorExePath;
        _installed = installed;
        _skipped = skipped;
        _isUpdate = isUpdate;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        LogPathBox.Text = SetupLog.Path;

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

        // Emphasize the report panel when something was skipped/failed.
        if (skipped > 0)
        {
            ReportHeading.Text = $"{skipped} component(s) did not install - please report this";
            ReportHeading.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}");
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] OpenLogButton_Click");
        try
        {
            // Open Explorer with the log file selected.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] OpenLogButton_Click FAILED: {ex.Message}");
        }
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] ReportButton_Click");
        try
        {
            var title = _skipped > 0
                ? $"[install] Setup failed on Windows ({_skipped} component(s) skipped)"
                : "[install] Setup problem on Windows";
            IssueReporter.Open(IssueReporter.BuildUrl(title, BuildIssueBody()));
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] ReportButton_Click FAILED: {ex.Message}");
            MessageBox.Show(
                $"Could not open the browser. Please file an issue at {IssueReporter.NewIssueBase} and attach the log:\n{SetupLog.Path}",
                "Report a problem", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Assemble the pre-filled issue body: environment + result + the tail of the setup log.</summary>
    private string BuildIssueBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- Briefly describe the problem. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Mode: {(_isUpdate ? "update" : "install")}");
        sb.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Installed: {_installed}, Skipped: {_skipped}");
        sb.AppendLine();
        sb.AppendLine("## Setup log");
        sb.AppendLine($"Full log (please attach it): `{SetupLog.Path}`");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(ReadLogTail(160));
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>The last <paramref name="lines"/> lines of the setup log, best-effort.</summary>
    private static string ReadLogTail(int lines)
    {
        try
        {
            var all = File.ReadAllLines(SetupLog.Path);
            var start = Math.Max(0, all.Length - lines);
            return string.Join("\n", all[start..]);
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
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
