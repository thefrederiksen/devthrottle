using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Microsoft.Win32;

namespace CcDirectorSetup.Steps;

public partial class CompleteStep : UserControl
{
    private readonly string _installPath = "";
    private int _installed;
    private int _skipped;
    private bool _isUpdate;

    public CompleteStep()
    {
        InitializeComponent();
    }

    public CompleteStep(int installed, int skipped, string installPath, bool isUpdate, bool alreadyUpToDate = false, string? version = null)
    {
        InitializeComponent();
        _installPath = installPath;
        _installed = installed;
        _skipped = skipped;
        _isUpdate = isUpdate;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        LogPathBox.Text = SetupLog.Path;

        var versionSuffix = string.IsNullOrEmpty(version) ? "" : $" · v{version.TrimStart('v')}";

        if (alreadyUpToDate)
        {
            HeadingText.Text = "✓  Already Up to Date";
            DescriptionText.Text = "DevThrottle is already running the latest version.";
            SummaryLine.Text = $"Nothing to do{versionSuffix}";
            PathNote.IsVisible = false;
        }
        else if (isUpdate)
        {
            HeadingText.Text = "✓  Update Complete";
            DescriptionText.Text = "Everything went perfectly. You're up to date.";
            SummaryLine.Text = $"{installed} components updated{versionSuffix}";
            PathNote.IsVisible = false;
        }
        else
        {
            SummaryLine.Text = $"{installed} components installed{versionSuffix}";
        }

        // Failure path: surface the problem loudly - amber heading, full summary box,
        // and the details/report expander forced open. On success all of that stays
        // out of the way behind the small collapsed expander at the bottom.
        if (skipped > 0)
        {
            var amber = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
            HeadingText.Text = isUpdate ? "Update finished with problems" : "Setup finished with problems";
            HeadingText.Foreground = amber;
            DescriptionText.Text = $"{skipped} component(s) did not install. DevThrottle may still work, but please report this.";
            SummaryLine.IsVisible = false;
            FailurePanel.IsVisible = true;
            DetailsHeader.Text = $"{skipped} component(s) did not install - please report this";
            DetailsHeader.Foreground = amber;
            DetailsExpander.IsExpanded = true;
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}, version={version}");
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] OpenLogButton_Click");
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true });
            else
            {
                var psi = new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open");
                psi.ArgumentList.Add(SetupLog.Dir);
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] OpenLogButton_Click FAILED: {ex.Message}");
        }
    }

    private void ReportButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] ReportButton_Click");
        try
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Windows";
            var title = _skipped > 0
                ? $"[install] Setup failed on {os} ({_skipped} component(s) skipped)"
                : $"[install] Setup problem on {os}";
            IssueReporter.Open(IssueReporter.BuildUrl(title, BuildIssueBody(os)));
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] ReportButton_Click FAILED: {ex.Message}");
        }
    }

    private string BuildIssueBody(string os)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- Briefly describe the problem. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Mode: {(_isUpdate ? "update" : "install")}");
        sb.AppendLine($"- OS: {os} ({RuntimeInformation.OSDescription})");
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

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");

        var binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cc-director.exe"
            : "cc-director";
        var exePath = Path.Combine(_installPath, binName);

        if (!File.Exists(exePath))
        {
            SetupLog.Write($"[CompleteStep] cc-director not found at {exePath}");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var freshPath = GetFreshPathWindows();
                if (freshPath != null)
                    psi.Environment["PATH"] = freshPath;
            }

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: cc-director launched");

            // Close the setup wizard
            var window = this.VisualRoot as Window;
            window?.Close();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] LaunchButton_Click FAILED: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetFreshPath()
    {
        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey("Environment");
            var userPath = userKey?.GetValue("Path", "") as string ?? "";

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

    private static string? GetFreshPathWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetFreshPath();
        return null;
    }
}
