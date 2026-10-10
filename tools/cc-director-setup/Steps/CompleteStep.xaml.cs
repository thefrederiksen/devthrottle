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
    private IReadOnlyList<string> _skippedReasons = [];

    /// <summary>The one amber used for "something still needs you" - headline, notice and summary.</summary>
    private static System.Windows.Media.SolidColorBrush AmberBrush =>
        new(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

    /// <param name="readyToGo">
    /// May this screen tell the user they are ready? False when no coding agent is installed at all -
    /// a board with nothing to run is not "ready", and saying so next to an amber note about what is
    /// missing is the lie this parameter removes.
    /// Computed once by <see cref="InstallCompletion.IsReadyToGo"/>; this screen only renders it.
    /// </param>
    public CompleteStep(int installed, int skipped, string installPath, string directorExePath, bool isUpdate, bool alreadyUpToDate = false, string? version = null, string? gatewayFailureReason = null, string? agentNotice = null, bool readyToGo = true, IReadOnlyList<string>? skippedNames = null, IReadOnlyList<string>? skippedReasons = null)
    {
        InitializeComponent();

        // The one thing the wizard still says about the MACHINE rather than about this install: there
        // is no coding agent on it, so the board has nothing to run. Said here, at the end, next to
        // what the user can do about it - never as a wall on an earlier screen.
        if (!string.IsNullOrWhiteSpace(agentNotice))
        {
            CapabilityNoticeText.Text = agentNotice;
            CapabilityPanel.Visibility = Visibility.Visible;
            SetupLog.Write($"[CompleteStep] agent notice shown: {agentNotice}");
        }

        _installPath = installPath;
        _directorExePath = directorExePath;
        _installed = installed;
        _skipped = skipped;
        _isUpdate = isUpdate;
        InstalledText.Text = installed.ToString();
        SkippedText.Text = skipped.ToString();
        PathText.Text = installPath;
        LogPathBox.Text = SetupLog.Path;

        var versionSuffix = string.IsNullOrEmpty(version) ? "" : $" · v{version.TrimStart('v')}";

        // One place computes the verdict; this only renders it (any skipped component reads as
        // Problems, so a failure can never render as "Everything went perfectly").
        switch (InstallCompletion.Classify(skipped, alreadyUpToDate))
        {
            case InstallCompletionKind.AlreadyUpToDate:
                HeadingText.Text = "✓  Already Up to Date";
                DescriptionText.Text = "The Director is already running the latest version.";
                SummaryLine.Text = $"Nothing to do{versionSuffix}";
                PathNote.Visibility = Visibility.Collapsed;
                break;

            case InstallCompletionKind.Success when isUpdate:
                HeadingText.Text = readyToGo ? "✓  Director is up to date" : "Director is up to date - one thing left";
                DescriptionText.Text = readyToGo
                    ? "You're ready to go."
                    : "The update finished. One thing below still needs you.";
                if (!readyToGo) HeadingText.Foreground = AmberBrush;
                SummaryLine.Text = $"{installed} components updated{versionSuffix}";
                PathNote.Visibility = Visibility.Collapsed;
                break;

            case InstallCompletionKind.Success:
                // Nothing failed to install - but "ready to go" is a claim about the MACHINE, not
                // about this install, and it is false while there is no coding agent to run. The XAML
                // defaults cover the genuinely-ready case.
                if (!readyToGo)
                {
                    HeadingText.Text = "Director is installed - one thing left";
                    HeadingText.Foreground = AmberBrush;
                    DescriptionText.Text = "Everything installed. One thing below still needs you.";
                }
                SummaryLine.Text = $"{installed} components installed{versionSuffix}";
                break;

            case InstallCompletionKind.Problems:
                // Failure path: surface the problem loudly - amber heading, full summary box,
                // and the details/report expander forced open. On success all of that stays
                // out of the way behind the small collapsed expander at the bottom.
                var amber = AmberBrush;
                HeadingText.Text = isUpdate ? "Update finished with problems" : "Setup finished with problems";
                HeadingText.Foreground = amber;
                // NAME what failed. "1 component(s) did not install" is a count, not information the
                // reader can act on. macOS already named the component; this was the last content
                // difference between the two Complete screens.
                var names = skippedNames ?? [];
                _skippedReasons = skippedReasons ?? [];
                var what = names.Count switch
                {
                    0 => skipped == 1 ? "One component" : $"{skipped} components",
                    1 => names[0],
                    _ => string.Join(", ", names),
                };
                // Carry the specific Gateway failure reason onto the final screen when we have it, so a
                // failed Gateway update tells the user WHY - not just that something did not install.
                // The REASON, not just the name. The engine computed it, the install card showed it, and
                // this screen used to drop it - sending the user to a log for a sentence we already had.
                var why = _skippedReasons.Count > 0 ? "\n" + string.Join("\n", _skippedReasons) : "";
                var gateway = string.IsNullOrWhiteSpace(gatewayFailureReason) ? "" : "\n" + gatewayFailureReason;
                DescriptionText.Text =
                    $"{what} did not install. The Director may still work, but please report this.{why}{gateway}";
                SummaryLine.Visibility = Visibility.Collapsed;
                FailurePanel.Visibility = Visibility.Visible;
                if (names.Count > 0) SkippedText.Text = $"{skipped} ({string.Join(", ", names)})";
                DetailsHeader.Text = $"{what} did not install - please report this";
                DetailsHeader.Foreground = amber;
                DetailsExpander.IsExpanded = true;
                break;
        }

        SetupLog.Write($"[CompleteStep] Created: installed={installed}, skipped={skipped}, isUpdate={isUpdate}, alreadyUpToDate={alreadyUpToDate}, version={version}");
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
            _ = WizardProgressReport.Error("open-log", $"Windows setup wizard could not open the log folder: {ex.GetType().Name}: {ex.Message}", ex);
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
            _ = WizardProgressReport.Error("report-problem", $"Windows setup wizard could not open the browser to report a problem: {ex.GetType().Name}: {ex.Message}", ex);
            MessageBox.Show(
                $"Could not open the browser. Please file an issue at {IssueReporter.NewIssueBase} and attach the log:\n{SetupLog.Path}",
                "Report a problem", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Assemble the pre-filled issue body: environment + result + the tail of the setup log.</summary>
    private string BuildIssueBody()
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
            // Not reported: the reason is written into the issue text the person is about to read and send.
            return $"(could not read log: {ex.Message})";
        }
    }

    /// <summary>True once this screen has started the Director, by the button or on close.</summary>
    public bool DirectorOpened { get; private set; }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[CompleteStep] LaunchButton_Click");
        if (OpenDirector())
            Window.GetWindow(this)?.Close();
    }

    /// <summary>
    /// Start the installed Director with a PATH read fresh from the registry, so it sees the tools
    /// directory this install just added. Returns whether it was started. Used by the Open Director
    /// button and, on a first install, by the wizard closing (issue #3503).
    /// </summary>
    public bool OpenDirector()
    {
        SetupLog.Write($"[CompleteStep] OpenDirector: {_directorExePath}");

        // The Director installs to the app dir (app\cc-director.exe), not the tools bin dir.
        var exePath = _directorExePath;
        if (!File.Exists(exePath))
        {
            SetupLog.Write($"[CompleteStep] cc-director.exe not found at {exePath}");
            return false;
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
            DirectorOpened = true;
            SetupLog.Write("[CompleteStep] OpenDirector: cc-director launched");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[CompleteStep] OpenDirector FAILED: {ex.Message}");
            _ = WizardProgressReport.Error("launch-director", $"Windows setup wizard could not open DevThrottle: {ex.GetType().Name}: {ex.Message}", ex);
            return false;
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
            _ = WizardProgressReport.Error("fresh-path", $"Windows setup wizard could not read PATH from the registry: {ex.GetType().Name}: {ex.Message}", ex);
            return null;
        }
    }
}
