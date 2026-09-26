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
    private IReadOnlyList<string> _skippedNames = [];
    private IReadOnlyList<string> _skippedReasons = [];

    public CompleteStep()
    {
        InitializeComponent();
    }

    public CompleteStep(int installed, int skipped, string installPath, bool isUpdate, bool alreadyUpToDate = false, string? version = null, string? agentNotice = null, IReadOnlyList<string>? skippedNames = null, bool readyToGo = true, IReadOnlyList<string>? skippedReasons = null)
    {
        InitializeComponent();

        // The one thing the wizard still says about the MACHINE rather than about this install: there
        // is no coding agent on it, so the board has nothing to run. Said here, at the end, next to
        // what the user can do about it - never as a wall on an earlier screen.
        if (!string.IsNullOrWhiteSpace(agentNotice))
        {
            CapabilityNoticeText.Text = agentNotice;
            CapabilityPanel.IsVisible = true;
            SetupLog.Write($"[CompleteStep] agent notice shown: {agentNotice}");
        }

        _installPath = installPath;
        _installed = installed;
        _skipped = skipped;
        _isUpdate = isUpdate;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        LogPathBox.Text = SetupLog.Path;

        var versionSuffix = string.IsNullOrEmpty(version) ? "" : $" · v{version.TrimStart('v')}";

        var amberBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

        // One place computes the verdict and this only renders it. The rule lives in the shared
        // InstallCompletion so both wizards read a pass the same way - this screen used to decide for
        // itself, which is how it came to say "Everything went perfectly" about a pass that had not.
        switch (InstallCompletion.Classify(skipped, alreadyUpToDate))
        {
            case InstallCompletionKind.AlreadyUpToDate:
                HeadingText.Text = "✓  Already Up to Date";
                DescriptionText.Text = "The Director is already running the latest version.";
                SummaryLine.Text = $"Nothing to do{versionSuffix}";
                PathNote.IsVisible = false;
                break;

            case InstallCompletionKind.Success when isUpdate:
                HeadingText.Text = readyToGo ? "✓  Director is up to date" : "Director is up to date - one thing left";
                DescriptionText.Text = readyToGo
                    ? "You're ready to go."
                    : "The update finished. One thing below still needs you.";
                if (!readyToGo) HeadingText.Foreground = amberBrush;
                SummaryLine.Text = $"{installed} components updated{versionSuffix}";
                PathNote.IsVisible = false;
                break;

            case InstallCompletionKind.Success:
                // Nothing failed to install - but "ready to go" is a claim about the MACHINE, not about
                // this install, and it is false while there is no coding agent to run. The markup
                // defaults cover the genuinely-ready case.
                if (!readyToGo)
                {
                    HeadingText.Text = "Director is installed - one thing left";
                    HeadingText.Foreground = amberBrush;
                    DescriptionText.Text = "Everything installed. One thing below still needs you.";
                }
                SummaryLine.Text = $"{installed} components installed{versionSuffix}";
                break;
        }

        // Failure path: surface the problem loudly - amber heading, full summary box,
        // and the details/report expander forced open. On success all of that stays
        // out of the way behind the small collapsed expander at the bottom.
        if (skipped > 0)
        {
            _skippedNames = skippedNames ?? [];
            _skippedReasons = skippedReasons ?? [];
            var amber = amberBrush;
            HeadingText.Text = isUpdate ? "Update finished with problems" : "Setup finished with problems";
            HeadingText.Foreground = amber;
            // Name what failed - "1 component(s)" tells the user nothing actionable.
            var what = _skippedNames.Count switch
            {
                0 => skipped == 1 ? "One component" : $"{skipped} components",
                1 => _skippedNames[0],
                _ => string.Join(", ", _skippedNames),
            };
            var why = _skippedReasons.Count > 0 ? "\n" + string.Join("\n", _skippedReasons) : "";
            DescriptionText.Text =
                $"{what} did not install. The Director may still work, but please report this.{why}";
            SummaryLine.IsVisible = false;
            FailurePanel.IsVisible = true;
            if (_skippedNames.Count > 0) SkippedText.Text = $"{skipped} ({string.Join(", ", _skippedNames)})";
            DetailsHeader.Text = $"{what} did not install - please report this";
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
        if (_skippedReasons.Count > 0)
        {
            sb.AppendLine("## Why it failed");
            foreach (var reason in _skippedReasons) sb.AppendLine($"- {reason}");
            sb.AppendLine();
        }
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- Briefly describe the problem. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Mode: {(_isUpdate ? "update" : "install")}");
        sb.AppendLine($"- OS: {os} ({RuntimeInformation.OSDescription})");
        sb.AppendLine($"- Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Installed: {_installed}, Skipped: {_skipped}"
            + (_skippedNames.Count > 0 ? $" ({string.Join(", ", _skippedNames)})" : ""));
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

        // _installPath is the canonical Director path (InstallLayout.PathFor). On Windows that is the
        // installed cc-director.exe; on macOS it is the ~/Applications/Director.app bundle. The two
        // launch differently: run the exe directly on Windows, but on macOS hand the bundle to
        // /usr/bin/open so LaunchServices registers it - that is what gives the app its Dock icon and
        // foreground activation. Launching the inner Mach-O binary directly gives neither.
        try
        {
            ProcessStartInfo psi;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!Directory.Exists(_installPath))
                {
                    LaunchFailed($"The Director was not found at {_installPath}", null);
                    return;
                }

                psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add(_installPath);
            }
            else
            {
                if (!File.Exists(_installPath))
                {
                    LaunchFailed($"The Director was not found at {_installPath}", null);
                    return;
                }

                psi = new ProcessStartInfo { FileName = _installPath, UseShellExecute = false };

                var freshPath = GetFreshPathWindows();
                if (freshPath != null)
                    psi.Environment["PATH"] = freshPath;
            }

            Process.Start(psi);
            SetupLog.Write("[CompleteStep] LaunchButton_Click: Director launched");

            // Close the setup wizard
            var window = this.VisualRoot as Window;
            window?.Close();
        }
        catch (Exception ex)
        {
            LaunchFailed($"The Director could not be started: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A Launch click that fails used to do nothing at all: the button stayed, nothing was said, and the
    /// reason went only to the setup log (#3311). Now it is said on screen and reported.
    /// </summary>
    private void LaunchFailed(string message, Exception? ex)
    {
        SetupLog.Write($"[CompleteStep] LaunchButton_Click FAILED: {message}{(ex is null ? "" : "\n" + ex)}");
        SummaryLine.Text = $"ERROR: {message}. The log is at {SetupLog.Path}";
        SummaryLine.IsVisible = true;
        _ = WizardErrorReport.SendAsync("wizard", "launch-director", message, ex);
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
